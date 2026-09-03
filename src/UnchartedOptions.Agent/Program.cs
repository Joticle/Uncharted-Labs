using UnchartedOptions.Alpaca;
using UnchartedOptions.Core;

// Uncharted Options -- one evaluation cycle, then exit.
//
// Single-shot by design, so the same binary runs identically under GitHub Actions cron, a
// scheduler, or a terminal. Nothing is held between runs: the broker is the state, which is
// the same principle as the risk model. The decision log is written, never read back.
//
// The accepted command line is AgentArguments.Usage. Anything else stops the run: an
// unrecognised flag used to fall through in silence, and --profile comp reads as a request
// for the judged account while selecting the dev one.

IReadOnlyList<string> argv = args;

IReadOnlyList<string> usageFaults = AgentArguments.Faults(argv);

if (usageFaults.Count > 0)
{
    foreach (string fault in usageFaults)
    {
        Console.Error.WriteLine($"REFUSED: {fault}.");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("Accepted arguments:");
    Console.Error.WriteLine(AgentArguments.Usage);
    return AgentArguments.UsageExitCode;
}
bool live = argv.Contains("--live", StringComparer.OrdinalIgnoreCase);
bool verifyOnly = argv.Contains("--verify", StringComparer.OrdinalIgnoreCase);
bool preflight = argv.Contains("--preflight", StringComparer.OrdinalIgnoreCase);

TradingProfile profile = TradingProfile.FromArgs(argv);
AgentConfig config = AgentConfig.FromArgs(argv);
CompetitionCalendar calendar = new();
RiskMandate mandate = config.Mandate;
// A simulated clock rehearses contest timing on the dev account. It is refused on the
// judged account: talking the guard out of its own sense of time is precisely what it exists
// to prevent, so the override cannot be aimed there.
if (config.SimulatedNow is not null && profile.IsCompetition)
{
    Console.Error.WriteLine("REFUSED: --as-of is a rehearsal tool and cannot target the competition account.");
    return 2;
}
DateTimeOffset now = config.SimulatedNow ?? DateTimeOffset.UtcNow;
DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
string underlying = config.Underlying;

Console.WriteLine($"Uncharted Options  [{profile.Description}]  {(live ? "LIVE" : "dry run")}");
Console.WriteLine(new string('=', 72));

// The judged account must not carry test orders. Refused here rather than left to discipline.
if (profile.IsCompetition && live && !calendar.MayOpenNewPositions(now))
{
    Console.Error.WriteLine($"REFUSED: {calendar.Describe(now)}");
    Console.Error.WriteLine("The competition account cannot be traded outside the competition window.");
    return 2;
}

CliRunner runner = new(profile: profile.CliProfile);
AlpacaCli cli = new(runner);
ChainReader chains = new(runner);
PositionReader positions = new(runner);
CorporateActionsReader corporateActions = new(runner);
PortfolioReader portfolio = new(runner);
ActivityReader activity = new(runner);

List<Decision> decisions = [];

try
{
    Account account = await cli.GetAccountAsync();

    if (verifyOnly)
    {
        return Report(ReadinessCheck.Run(account, profile, startingBalance: 100_000m));
    }

    MarketClock clock = await cli.GetClockAsync();
    IReadOnlyList<OpenPosition> open = await positions.GetOpenPositionsAsync();
    decimal existingExposure = PortfolioExposure.ForUnderlying(open, underlying);
    decimal spot = await cli.GetUnderlyingMidAsync(underlying);

    // Earnings come from an explicit list; ex-dividends from the broker. Neither is inferred.
    List<BlackoutEvent> events = [.. BlackoutCalendar.ParseEarnings(config.EarningsDates)];
    events.AddRange(await corporateActions.GetExDividendsAsync(
        [underlying], today.AddDays(-10), today.AddDays(30)));

    BlackoutCalendar blackout = new(events, config.BlackoutSessions);
    BlackoutVerdict blackoutVerdict = blackout.Check(underlying, today);

    Console.WriteLine($"Account      {account.AccountNumber}   equity {Money.Usd(account.Equity)}   options level {account.OptionsTradingLevel}");
    Console.WriteLine($"Market       {(clock.IsOpen ? "OPEN" : "closed")}");
    Console.WriteLine($"Calendar     {calendar.Describe(now)}");
    Console.WriteLine($"Target       {underlying} {config.TargetExpiration:yyyy-MM-dd}, {config.WidthPolicy}");
    Console.WriteLine($"{underlying,-12} {Money.Usd(spot)}");
    Console.WriteLine($"Positions    {open.Count} legs open, {Money.Usd(PortfolioExposure.Total(open))} at risk");
    Console.WriteLine($"Blackout     {blackoutVerdict.Explanation}");
    Console.WriteLine($"Events       {events.Count} on file ({events.Count(e => e.Reason == BlackoutReason.Earnings)} earnings, {events.Count(e => e.Reason == BlackoutReason.ExDividend)} ex-dividend)");
    Console.WriteLine();

    // ---- manage what is already held ----
    IReadOnlyDictionary<string, DateTimeOffset> fills = open.Count > 0
        ? await cli.GetFillTimesAsync()
        : new Dictionary<string, DateTimeOffset>();

    // Reconstruction refuses a book it cannot account for. That must stop new entries --
    // a stray leg means something happened that nothing evaluated -- but it must never
    // stop the close. The throw used to sit upstream of the ladder, the close and the log
    // write, so an unpairable leg on Thursday would have blocked the flatten and left the
    // dashboard showing the previous cycle with no sign of failure.
    IReadOnlyList<SpreadPosition> heldSpreads = [];
    string? reconstructionFault = null;

    try
    {
        heldSpreads = SpreadReconstruction.FromLegs(open, fills, now);
    }
    catch (LegConservationException ex)
    {
        reconstructionFault = ex.Message;
        Console.Error.WriteLine($"HALTED: {ex.Message}");

        decisions.Add(new Decision
        {
            Underlying = underlying,
            Verdict = Verdict.SKIPPED,
            Gate = "reconstruction-halt",
            Finding = ex.Message,
        });
    }
    ExitPolicy exitPolicy = new();


    // The contest calendar constrains the judged account only. The dev account exists to be
    // rehearsed against outside contest hours, so applying contest timing to it would block
    // the one thing it is for. The hard refusal on --comp --live above is unaffected.
    CompetitionCalendar? activeCalendar =

        profile.IsCompetition || config.SimulatedNow is not null ? calendar : null;

    // Degraded flatten. Reconstruction has refused the book, so no spread can be formed
    // and the ladder has nothing to evaluate -- but the contest still requires everything
    // flat, and a leg left open through settlement is the outcome the whole design exists
    // to prevent. Legs are closed directly, shorts before longs: buying the shorts back
    // first leaves a long-only book whose loss is bounded by premium already paid, while
    // selling the longs first would leave the shorts uncovered.
    if (reconstructionFault is not null && activeCalendar is not null && clock.IsOpen
        && activeCalendar.PermissionAt(now) is TradingPermission.FlattenAll or TradingPermission.Closed)
    {
        IEnumerable<OpenPosition> shortsFirst = open
            .Where(l => l.IsOption)
            .OrderBy(l => l.Quantity > 0);

        foreach (OpenPosition leg in shortsFirst)
        {
            string side = leg.Quantity > 0 ? "long" : "short";

            if (!live)
            {
                Console.WriteLine($"             would close {side} {leg.Symbol} x{Math.Abs(leg.Quantity)}");
                continue;
            }

            string orderId = await cli.ClosePositionAsync(leg.Symbol);
            Console.WriteLine($"             closed {side} {leg.Symbol} x{Math.Abs(leg.Quantity)} order {orderId}");

            decisions.Add(DecisionLog.Executed(new Decision
            {
                Underlying = leg.Underlying,
                Structure = leg.Symbol,
                Verdict = Verdict.CLOSED,
                Gate = "degraded-flatten",
                Finding = $"closed {side} leg directly; the book could not be reconstructed",
            }, orderId));
        }
    }

    foreach (SpreadPosition held in heldSpreads)
    {
        ExitDecision decision = ExitLadder.Evaluate(held, exitPolicy, spot, now, activeCalendar);
        Console.WriteLine($"Manage       {held.Spread.Underlying} x{held.Contracts}: {decision.Reason} -- {decision.Explanation}");
        // Exits are decisions. Recording only entries would leave the ladder's work
        // invisible in the one artifact that exists to show what the agent decided.
        Decision exitRecord = ExitTaken(held, decision, account.Equity);
        int exitIndex = decisions.Count;
        decisions.Add(exitRecord);

        if (!decision.ShouldClose)
        {
            continue;
        }

        if (!clock.IsOpen)
        {
            Console.WriteLine("             market closed -- close deferred to the next session");
            continue;
        }

        OrderSubmission close = await cli.CloseSpreadAsync(
            held.Spread, held.Contracts, Math.Max(0.01m, held.CurrentValue), dryRun: !live);

        decisions[exitIndex] = DecisionLog.Executed(exitRecord, close.OrderId);

        Console.WriteLine(close.WasDryRun
            ? $"             close validated ({decision.Reason})"
            : $"             CLOSED, order {close.OrderId}");
    }

    // ---- consider a new position ----
    IReadOnlyList<OptionContract> chain = await chains.GetChainAsync(
        underlying, config.TargetExpiration, OptionType.Call,
        Math.Floor(spot), Math.Ceiling(spot) + config.StrikeSearchBand, limit: 200);

    int quoted = chain.Count(c => c.HasGreeks && c.HasTwoSidedQuote);
    int inBand = chain.Count(c => c.HasGreeks && c.HasTwoSidedQuote
                                  && c.Delta >= mandate.MinLongLegDelta && c.Delta <= mandate.MaxLongLegDelta);

    Console.WriteLine();
    Console.WriteLine($"Chain        {chain.Count} contracts, {quoted} quoted ({Pct(quoted, chain.Count)}%), {inBand} in the delta band");

    if (inBand == 0)
    {
        Console.WriteLine("             WARNING: nothing quoted in the delta band. Not tradeable here.");
    }
    else if (inBand <= 2 || quoted * 2 < chain.Count)
    {
        Console.WriteLine($"             WARNING: thin bench -- {inBand} candidate(s), {chain.Count - quoted} of {chain.Count} unquoted.");
    }

    if (preflight)
    {
        Console.WriteLine();
        Console.WriteLine("PREFLIGHT SUMMARY");
        Console.WriteLine($"  account          {account.AccountNumber} ({profile.Description})");
        Console.WriteLine($"  options level    {account.OptionsTradingLevel} {(account.CanTradeSpreads ? "OK" : "-- BELOW 3, multi-leg will be rejected")}");
        Console.WriteLine($"  equity           {Money.Usd(account.Equity)}");
        Console.WriteLine($"  market           {(clock.IsOpen ? "OPEN" : "closed")}");
        Console.WriteLine($"  calendar         {calendar.PermissionAt(now)}");
        Console.WriteLine($"  blackout         {(blackoutVerdict.IsBlackedOut ? "YES -- " + blackoutVerdict.Explanation : "clear")}");
        Console.WriteLine($"  bench            {inBand} candidate(s) of {quoted} quoted at {config.TargetExpiration:yyyy-MM-dd}");
        Console.WriteLine($"  the 3            {Money.Usd(account.Equity * mandate.MaxRiskPerTradePct)} per-trade ceiling");
        Console.WriteLine($"  the 5            {Money.Usd(existingExposure)} of {Money.Usd(account.Equity * mandate.MaxSymbolExposurePct)} used on {underlying}");

        bool ready = account.CanTradeSpreads && inBand > 0 && !blackoutVerdict.IsBlackedOut;
        Console.WriteLine();
        Console.WriteLine(ready ? "READY to trade at the open." : "NOT READY -- see above.");
        return ready ? 0 : 3;
    }

    // ---- the gates, in order, each one recorded ----
    //
    // Evaluation always runs, even when the agent is barred from trading. A blackout or a
    // closed competition window suppresses the order, not the reasoning: the record of what
    // would have been refused, and on which gate, is the evidence the mandate is enforced.
    SpreadCandidate candidate = SpreadSelector.SelectBullCall(underlying, chain, mandate, config.WidthPolicy);

    bool barred = false;

    // A shut market bars everything. The clock was read and displayed but never enforced,
    // which is harmless in a dry run and not once orders are real: GitHub's scheduled runs
    // are best-effort and have already arrived four hours late once, so a pass nominally
    // inside the session can fire after the close. A day limit order submitted then is
    // queued into the next session and fills on terms nothing here evaluated.
    if (!clock.IsOpen)
    {
        barred = true;
        decisions.Add(new Decision
        {
            Underlying = underlying,
            Verdict = Verdict.SKIPPED,
            Gate = "market-closed",
            Finding = $"market is closed; next open {clock.NextOpen:yyyy-MM-dd HH:mm} UTC",
        });
    }
    else if (blackoutVerdict.IsBlackedOut)
    {
        barred = true;
        decisions.Add(new Decision
        {
            Underlying = underlying,
            Verdict = Verdict.SKIPPED,
            Gate = "blackout",
            Finding = blackoutVerdict.Explanation,
        });
    }
    else if ((profile.IsCompetition || config.SimulatedNow is not null) && !calendar.MayOpenNewPositions(now))
    {
        barred = true;
        decisions.Add(new Decision
        {
            Underlying = underlying,
            Verdict = Verdict.SKIPPED,
            Gate = "competition-calendar",
            Finding = calendar.Describe(now),
        });
    }

    foreach (WidthEvaluation e in candidate.Evaluations.Where(e => !e.Qualified))
    {
        decisions.Add(RejectedWidth(underlying, e));
    }

    if (!candidate.Found && candidate.Evaluations.Count == 0)
    {
        decisions.Add(new Decision
        {
            Underlying = underlying,
            Verdict = Verdict.REJECTED,
            Gate = "delta-band",
            Finding = candidate.Reasoning,
        });
    }
    else if (candidate.Found)
    {
        VerticalSpread spread = candidate.Spread!;
        SizingResult sizing = PositionSizer.Size(new SizingRequest
        {
            Account = account,
            Spread = spread,
            ExistingSymbolExposure = existingExposure,
            Mandate = mandate,
        });

        WidthEvaluation chosen = candidate.Evaluations.First(e => e.Qualified && e.Width == spread.StrikeWidth);

        // A candidate that cleared every gate but is barred by the window is recorded as
        // skipped rather than taken -- the sizing stands, the order does not.
        Decision sizedRecord = barred && sizing.ShouldTrade
            ? Barred(underlying, spread, sizing, candidate.LongLegDelta, account,
                     blackoutVerdict.IsBlackedOut ? "blackout" : "competition-calendar")
            : Sized(underlying, spread, chosen, sizing, account, candidate.LongLegDelta);
        int sizedIndex = decisions.Count;
        decisions.Add(sizedRecord);

        if (!barred && sizing.ShouldTrade && account.CanTradeSpreads)
        {
            OrderSubmission submission = await cli.SubmitSpreadAsync(
                spread, sizing.Contracts, spread.NetDebit, dryRun: !live);

            decisions[sizedIndex] = DecisionLog.Executed(sizedRecord, submission.OrderId);

            Console.WriteLine(submission.WasDryRun
                ? "Broker validated the order. Nothing was placed."
                : $"ORDER PLACED. id {submission.OrderId}");
        }
        else if (!barred && !account.CanTradeSpreads)
        {
            Console.Error.WriteLine($"REFUSED: options level {account.OptionsTradingLevel}; multi-leg needs 3.");
        }
    }

    // ---- write the record ----
    Console.WriteLine();
    Console.WriteLine("DECISIONS");
    foreach (Decision d in decisions)
    {
        Console.WriteLine("  " + d.ToLine());
    }

    decimal totalRisk = PortfolioExposure.Total(open);

    LogRun logRun = new()
    {
        RunId = DecisionLog.NewRunId(now),
        Timestamp = DecisionLog.Stamp(now),
        Account = account.AccountNumber,
        Profile = profile.CliProfile,
        IsCompetition = profile.IsCompetition,
        MarketOpen = clock.IsOpen,
        DryRun = !live,
        Equity = Math.Round(account.Equity, 2),
        CalendarState = calendar.PermissionAt(now).ToString(),
        RiskPerTrade = new GateUtilisation
        {
            Label = "risk per trade",
            CeilingPercent = Math.Round(mandate.MaxRiskPerTradePct * 100m, 2),
            CeilingDollars = Math.Round(account.Equity * mandate.MaxRiskPerTradePct, 2),
            DeployedDollars = Math.Round(totalRisk, 2),
            DeployedPercent = account.Equity <= 0m ? 0m : Math.Round(totalRisk / account.Equity * 100m, 2),
        },
        SymbolExposure = DecisionLog.ExposureGates(open, account.Equity, mandate),
        Decisions = decisions,
    };

    DecisionLog.Append(config.LogDirectory, logRun);
    // The dashboard needs more than the decision log carries -- position marks, an equity
    // curve, realised outcomes. The log stays a record of decisions; this is the view model
    // that spans both, written alongside it.
    IReadOnlyList<RealisedTrade> realised = RealisedTrades.FromFills(await activity.GetFillsAsync());
    DashboardFeedBuilder.Write(config.LogDirectory, DashboardFeedBuilder.Build(
        logRun, heldSpreads, await portfolio.GetEquityCurveAsync(), realised,
        events, underlying, mandate, chain.Count, calendar, now));
    Console.WriteLine();
    Console.WriteLine($"Logged {decisions.Count} decision(s) to {config.LogDirectory}/decisions.jsonl");

    return 0;
}
catch (AlpacaCliException ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}
catch (FormatException ex)
{
    // A malformed blackout entry is a configuration error, not a crash. It must still stop
    // the run: a silently dropped earnings date is an underlying the agent believes is clear.
    Console.Error.WriteLine($"CONFIGURATION ERROR: {ex.Message}");
    return 5;
}

static int Pct(int part, int whole) => whole == 0 ? 0 : part * 100 / whole;

static int Report(ReadinessReport report)
{
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

static Decision RejectedWidth(string underlying, WidthEvaluation e)
{
    decimal longStrike = e.Spread is null ? 0m : OccSymbol.Strike(e.Spread.LongSymbol) ?? 0m;

    return new Decision
    {
        Underlying = underlying,
        Structure = longStrike > 0m ? $"{longStrike:F0}C/{longStrike + e.Width:F0}C" : $"${e.Width:F0} width",
        Verdict = Verdict.REJECTED,
        Gate = GateName(e.Outcome),
        Finding = e.Detail,
        Metrics = new DecisionMetrics
        {
            LongStrike = longStrike,
            ShortStrike = longStrike > 0m ? longStrike + e.Width : 0m,
            Width = e.Width,
            Debit = Math.Round(e.CrossedDebit, 2),
            CostDragPercent = Math.Round(e.CostDrag * 100m, 1),
            RewardRisk = e.Spread is null ? 0m : Math.Round(e.Spread.RewardRiskRatio, 2),
        },
    };
}

static Decision Sized(
    string underlying, VerticalSpread spread, WidthEvaluation chosen, SizingResult sizing,
    Account account, decimal longLegDelta)
{
    decimal longStrike = OccSymbol.Strike(spread.LongSymbol) ?? 0m;
    decimal shortStrike = OccSymbol.Strike(spread.ShortSymbol) ?? 0m;

    return new Decision
    {
        Underlying = underlying,
        Structure = $"{longStrike:F0}C/{shortStrike:F0}C",
        Verdict = sizing.ShouldTrade ? Verdict.TAKEN : Verdict.REJECTED,
        Gate = sizing.ShouldTrade ? "sized" : sizing.LimitedBy.ToString(),
        Finding = sizing.ShouldTrade
            ? $"delta {longLegDelta:F2} | {spread.RewardRiskRatio:F2}:1 | "
              + $"{Money.Usd(spread.MaxLossPerContract)} max loss | "
              + $"{Money.Percent(sizing.CapitalAtRisk / account.Equity)} of equity"
            : sizing.Explanation,
        Metrics = new DecisionMetrics
        {
            LongStrike = longStrike,
            ShortStrike = shortStrike,
            Width = spread.StrikeWidth,
            Delta = Math.Round(longLegDelta, 3),
            Debit = Math.Round(spread.NetDebit, 2),
            RewardRisk = Math.Round(spread.RewardRiskRatio, 2),
            CostDragPercent = Math.Round(chosen.CostDrag * 100m, 1),
            MaxLossDollars = Math.Round(spread.MaxLossPerContract, 2),
            Contracts = sizing.Contracts,
            RiskDollars = Math.Round(sizing.CapitalAtRisk, 2),
            RiskPercent = account.Equity <= 0m ? 0m
                : Math.Round(sizing.CapitalAtRisk / account.Equity * 100m, 2),
        },
    };
}

static Decision Barred(
    string underlying, VerticalSpread spread, SizingResult sizing, decimal delta, Account account, string gate)
{
    decimal longStrike = OccSymbol.Strike(spread.LongSymbol) ?? 0m;
    decimal shortStrike = OccSymbol.Strike(spread.ShortSymbol) ?? 0m;

    return new Decision
    {
        Underlying = underlying,
        Structure = $"{longStrike:F0}C/{shortStrike:F0}C",
        Verdict = Verdict.SKIPPED,
        Gate = gate,
        Finding = $"would size {sizing.Contracts} at {spread.RewardRiskRatio:F2}:1, "
                + $"but {gate} bars new positions",
        Metrics = new DecisionMetrics
        {
            LongStrike = longStrike,
            ShortStrike = shortStrike,
            Width = spread.StrikeWidth,
            Delta = Math.Round(delta, 3),
            Debit = Math.Round(spread.NetDebit, 2),
            RewardRisk = Math.Round(spread.RewardRiskRatio, 2),
            MaxLossDollars = Math.Round(spread.MaxLossPerContract, 2),
            Contracts = sizing.Contracts,
            RiskDollars = Math.Round(sizing.CapitalAtRisk, 2),
            RiskPercent = account.Equity <= 0m ? 0m
                : Math.Round(sizing.CapitalAtRisk / account.Equity * 100m, 2),
        },
    };
}

static Decision ExitTaken(SpreadPosition held, ExitDecision decision, decimal equity)
{
    decimal longStrike = OccSymbol.Strike(held.Spread.LongSymbol) ?? 0m;
    decimal shortStrike = OccSymbol.Strike(held.Spread.ShortSymbol) ?? 0m;
    decimal atRisk = held.Spread.MaxLoss(held.Contracts);
    return new Decision
    {
        Underlying = held.Spread.Underlying,
        Structure = $"{longStrike:F0}C/{shortStrike:F0}C",
        Verdict = decision.ShouldClose ? Verdict.CLOSED : Verdict.HELD,
        Gate = decision.Reason.ToString(),
        Finding = decision.Explanation,
        Metrics = new DecisionMetrics
        {
            LongStrike = longStrike,
            ShortStrike = shortStrike,
            Width = held.Spread.StrikeWidth,
            Debit = Math.Round(held.DebitPaid, 2),
            RewardRisk = Math.Round(held.Spread.RewardRiskRatio, 2),
            MaxLossDollars = Math.Round(held.Spread.MaxLossPerContract, 2),
            Contracts = held.Contracts,
            RiskDollars = Math.Round(atRisk, 2),
            RiskPercent = equity <= 0m ? 0m : Math.Round(atRisk / equity * 100m, 2),
        },
    };
}
static string GateName(SelectionFailure f) => f switch
{
    SelectionFailure.CostDragTooHigh => "cost-drag",
    SelectionFailure.RewardRiskBelowFloor => "reward-floor",
    SelectionFailure.LegsTooIlliquid => "liquidity",
    SelectionFailure.NoShortLegAtWidth => "no-short-leg",
    SelectionFailure.NoContractsInDeltaBand => "delta-band",
    SelectionFailure.DebitExceedsWidth => "malformed-spread",
    _ => "none",
};
