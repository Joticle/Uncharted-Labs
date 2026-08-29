using UnchartedOptions.Alpaca;
using UnchartedOptions.Core;

// Uncharted Options -- one evaluation cycle, then exit.
//
// Single-shot by design, so the same binary runs identically under GitHub Actions cron,
// Task Scheduler, or a bare terminal. Nothing is held between runs: the broker is the
// state, which is the same principle as the risk model.
//
//   (no flags)      dry run against the dev account
//   --live          actually place orders
//   --comp          target the judged competition account
//   --verify        check the account is configured correctly, then exit
//
// Reaching the competition account with real orders takes both --comp and --live, and is
// refused outright before the competition opens.

IReadOnlyList<string> argv = args;
bool live = argv.Contains("--live", StringComparer.OrdinalIgnoreCase);
bool verifyOnly = argv.Contains("--verify", StringComparer.OrdinalIgnoreCase);
TradingProfile profile = TradingProfile.FromArgs(argv);
CompetitionCalendar calendar = new();
DateTimeOffset now = DateTimeOffset.UtcNow;

AgentConfig config = AgentConfig.FromArgs(argv);
string underlying = config.Underlying;

Console.WriteLine($"Uncharted Options  [{profile.Description}]  {(live ? "LIVE" : "dry run")}");
Console.WriteLine(new string('=', 68));

// Hard guard. The competition account must not carry test orders, so an order against it
// before the opening bell is refused here rather than being left to discipline.
if (profile.IsCompetition && live && !calendar.MayOpenNewPositions(now))
{
    Console.Error.WriteLine($"REFUSED: {calendar.Describe(now)}");
    Console.Error.WriteLine("The competition account cannot be traded outside the competition window.");
    Console.Error.WriteLine("Use the dev account for testing (omit --comp).");
    return 2;
}

CliRunner runner = new(profile: profile.CliProfile);
AlpacaCli cli = new(runner);
ChainReader chains = new(runner);
PositionReader positions = new(runner);
RiskMandate mandate = config.Mandate;

try
{
    Account account = await cli.GetAccountAsync();
    MarketClock clock = await cli.GetClockAsync();

    if (verifyOnly)
    {
        ReadinessReport report = ReadinessCheck.Run(account, profile, expectedEquity: 100_000m);

        foreach (ReadinessItem item in report.Items)
        {
            Console.WriteLine($"  [{(item.Passed ? "PASS" : "FAIL")}]  {item.Name}");
            Console.WriteLine($"          {item.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine(report.Ready
            ? "READY. This account is configured to trade defined-risk verticals."
            : "NOT READY. Fix the failures above before the opening bell.");

        return report.Ready ? 0 : 3;
    }

    Console.WriteLine($"Account      {account.AccountNumber}   equity {Money.Usd(account.Equity)}   options level {account.OptionsTradingLevel}");
    Console.WriteLine($"Market       {(clock.IsOpen ? "OPEN" : "closed")}");
    Console.WriteLine($"Calendar     {calendar.Describe(now)}");
    Console.WriteLine($"Target       {underlying} {config.TargetExpiration:yyyy-MM-dd}, {config.WidthPolicy}");

    IReadOnlyList<OpenPosition> open = await positions.GetOpenPositionsAsync();
    decimal existingExposure = PortfolioExposure.ForUnderlying(open, underlying);

    Console.WriteLine($"Positions    {open.Count} open, {Money.Usd(PortfolioExposure.Total(open))} at risk total");
    if (existingExposure > 0m)
    {
        Console.WriteLine($"             {underlying} already carries {Money.Usd(existingExposure)} -- netted off the 5 gate");
    }

    decimal spot = await cli.GetUnderlyingMidAsync(underlying);

    // Manage what is already held before considering anything new. An agent that opens
    // faster than it closes is not running a strategy, it is accumulating.
    IReadOnlyDictionary<string, DateTimeOffset> fills = open.Count > 0
        ? await cli.GetFillTimesAsync()
        : new Dictionary<string, DateTimeOffset>();

    IReadOnlyList<SpreadPosition> heldSpreads = SpreadReconstruction.FromLegs(open, fills, now);
    ExitPolicy exitPolicy = new();

    foreach (SpreadPosition held in heldSpreads)
    {
        ExitDecision decision = ExitLadder.Evaluate(held, exitPolicy, spot, now, calendar);
        Console.WriteLine($"Manage       {held.Spread.Underlying} {held.Spread.Expiration:MM-dd} "
                        + $"x{held.Contracts}: {decision.Reason} -- {decision.Explanation}");

        if (!decision.ShouldClose)
        {
            continue;
        }

        // Close at the mark rather than crossing blindly; both legs pay their own spread.
        OrderSubmission close = await cli.CloseSpreadAsync(
            held.Spread,
            held.Contracts,
            limitPrice: Math.Max(0.01m, held.CurrentValue),
            dryRun: !live);

        Console.WriteLine(close.WasDryRun
            ? $"             close validated ({decision.Reason}), nothing placed"
            : $"             CLOSED, order {close.OrderId}");
    }

    Console.WriteLine($"{underlying,-12} {Money.Usd(spot)}");
    Console.WriteLine();

    IReadOnlyList<OptionContract> chain = await chains.GetChainAsync(
        underlying,
        config.TargetExpiration,
        OptionType.Call,
        strikeFrom: Math.Floor(spot),
        strikeTo: Math.Ceiling(spot) + config.StrikeSearchBand,
        limit: 200);

    int quoted = chain.Count(c => c.HasGreeks && c.HasTwoSidedQuote);
    int inBand = chain.Count(c => c.HasGreeks && c.HasTwoSidedQuote
                                  && c.Delta >= mandate.MinLongLegDelta
                                  && c.Delta <= mandate.MaxLongLegDelta);

    Console.WriteLine($"Chain        {chain.Count} contracts, {quoted} quoted "
                    + $"({(chain.Count == 0 ? 0 : quoted * 100 / chain.Count)}%), {inBand} in the delta band");

    // Starvation shows up as a silent no-trade day otherwise. Say it out loud beforehand.
    if (inBand == 0)
    {
        Console.WriteLine("             WARNING: no quoted contract in the delta band. Nothing is tradeable here.");
    }
    else if (inBand <= 2 || quoted * 2 < chain.Count)
    {
        Console.WriteLine($"             WARNING: thin bench -- {inBand} candidate(s), "
                        + $"{chain.Count - quoted} of {chain.Count} contracts unquoted.");
    }

    SpreadCandidate candidate = SpreadSelector.SelectBullCall(underlying, chain, mandate, config.WidthPolicy);
    foreach (WidthEvaluation e in candidate.Evaluations)
    {
        Console.WriteLine($"  width ${e.Width,-3:F0}  {(e.Qualified ? "OK  " : "no  ")}{e.Detail}");
    }

    Console.WriteLine($"Selection    {candidate.Reasoning}");

    if (!candidate.Found)
    {
        Console.WriteLine("\nNo spread met the mandate. Nothing to submit.");
        return 0;
    }

    VerticalSpread spread = candidate.Spread!;

    SizingResult sizing = PositionSizer.Size(new SizingRequest
    {
        Account = account,
        Spread = spread,
        ExistingSymbolExposure = existingExposure,
        Mandate = mandate,
    });

    Console.WriteLine($"Sizing       {sizing.Explanation}");
    Console.WriteLine();

    if (!sizing.ShouldTrade)
    {
        Console.WriteLine("Mandate declined this spread. Nothing to submit.");
        return 0;
    }

    if (!account.CanTradeSpreads)
    {
        Console.Error.WriteLine(
            $"REFUSED: options trading level {account.OptionsTradingLevel}; multi-leg spreads need level 3.");
        return 4;
    }

    OrderSubmission submission = await cli.SubmitSpreadAsync(
        spread,
        sizing.Contracts,
        limitPrice: spread.NetDebit,
        dryRun: !live);

    Console.WriteLine(submission.WasDryRun
        ? "Broker validated the order. Nothing was placed."
        : $"ORDER PLACED. id {submission.OrderId}");

    return 0;
}
catch (AlpacaCliException ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}
