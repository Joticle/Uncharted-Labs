using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnchartedOptions.Core;

// The dashboard's own vocabulary. It differs from the decision log's on purpose: the log is a
// durable record with stable, self-describing keys, while this is a view model shaped to what
// one front end renders. Mapping between them here keeps the log's contract free to stay
// stable while the dashboard's shape follows its design.

/// <summary>A metric tile: label and pre-formatted value.</summary>
public sealed record FeedMetric
{
    [JsonPropertyName("k")] public required string K { get; init; }
    [JsonPropertyName("v")] public required string V { get; init; }
}

/// <summary>An open position as the dashboard renders it.</summary>
public sealed record FeedPosition
{
    [JsonPropertyName("sym")] public required string Sym { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("qty")] public required int Qty { get; init; }
    [JsonPropertyName("legs")] public required string Legs { get; init; }
    [JsonPropertyName("dte")] public required int Dte { get; init; }
    [JsonPropertyName("open")] public required string Open { get; init; }
    [JsonPropertyName("n")] public required int N { get; init; }
    [JsonPropertyName("mlPer")] public required string MlPer { get; init; }
    [JsonPropertyName("maxLoss")] public required decimal MaxLoss { get; init; }
    [JsonPropertyName("maxLossPct")] public required decimal MaxLossPct { get; init; }
    [JsonPropertyName("metrics")] public required IReadOnlyList<FeedMetric> Metrics { get; init; }
}

/// <summary>A candidate the agent considered, as the dashboard's refusal stream renders it.</summary>
public sealed record FeedRejection
{
    [JsonPropertyName("t")] public required string T { get; init; }
    [JsonPropertyName("cand")] public required string Cand { get; init; }
    [JsonPropertyName("verdict")] public required string Verdict { get; init; }
    [JsonPropertyName("gate")] public required string Gate { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    /// <summary>Whether an order actually exists. Never render a position without this.</summary>
    [JsonPropertyName("executed")] public required bool Executed { get; init; }
}

/// <summary>One underlying in the exposure panel, and whether a gate is holding it shut.</summary>
public sealed record FeedSymbol
{
    [JsonPropertyName("n")] public required string N { get; init; }

    /// <summary>Sub-label: position count, or why the channel is closed.</summary>
    [JsonPropertyName("note")] public required string Note { get; init; }

    /// <summary>True when a blackout bars the underlying outright, whatever the exposure.</summary>
    [JsonPropertyName("blackout")] public required bool Blackout { get; init; }
}

/// <summary>A closed trade with its realised outcome.</summary>
public sealed record FeedClosed
{
    [JsonPropertyName("sym")] public required string Sym { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("pnl")] public required decimal Pnl { get; init; }
    [JsonPropertyName("win")] public required bool Win { get; init; }

    /// <summary>Date the position was unwound, as MM.dd.</summary>
    [JsonPropertyName("closedOn")] public required string ClosedOn { get; init; }

    /// <summary>
    /// Holding period in trading sessions, e.g. <c>1d</c>.
    /// </summary>
    /// <remarks>
    /// Both of these were absent while the consumer read them positionally, so the date column
    /// rendered blank and the mean holding period parsed an empty string into NaN the moment
    /// the first trade closed. Weekends are not sessions, so a Friday-to-Monday hold is one.
    /// </remarks>
    [JsonPropertyName("held")] public required string Held { get; init; }
}

/// <summary>The whole view model, one object per run.</summary>
public sealed record DashboardFeed
{
    [JsonPropertyName("generatedAt")] public required string GeneratedAt { get; init; }
    [JsonPropertyName("day")] public required string Day { get; init; }
    [JsonPropertyName("clock")] public required string Clock { get; init; }
    /// <summary>True when no orders were placed this run.</summary>
    [JsonPropertyName("dryRun")] public required bool DryRun { get; init; }
    [JsonPropertyName("account")] public required string Account { get; init; }
    [JsonPropertyName("equity")] public required decimal Equity { get; init; }
    [JsonPropertyName("positions")] public required IReadOnlyList<FeedPosition> Positions { get; init; }
    [JsonPropertyName("rejections")] public required IReadOnlyList<FeedRejection> Rejections { get; init; }
    [JsonPropertyName("closed")] public required IReadOnlyList<FeedClosed> Closed { get; init; }

    /// <summary>Contracts examined before any gate ran. The denominator for "how much was refused".</summary>
    [JsonPropertyName("preGate")] public required int PreGate { get; init; }

    [JsonPropertyName("wins")] public required int Wins { get; init; }
    [JsonPropertyName("losses")] public required int Losses { get; init; }

    /// <summary>Equity curve, oldest first.</summary>
    [JsonPropertyName("curve")] public required IReadOnlyList<decimal> Curve { get; init; }

    [JsonPropertyName("curveFrom")] public required string CurveFrom { get; init; }
    [JsonPropertyName("curveTo")] public required string CurveTo { get; init; }
    [JsonPropertyName("curveLabel")] public required string CurveLabel { get; init; }

    /// <summary>Total risk deployed against the 3% ceiling, for the gate bars.</summary>
    [JsonPropertyName("riskDeployed")] public required decimal RiskDeployed { get; init; }
    [JsonPropertyName("riskCeiling")] public required decimal RiskCeiling { get; init; }

    /// <summary>
    /// Underlyings the exposure panel should show.
    /// </summary>
    /// <remarks>
    /// Supplied rather than hardcoded in the view. The design shipped with a fixed
    /// SPY/IWM/QQQ universe and a QQQ earnings blackout dated 09.04 -- fixtures convincing
    /// enough to render as fact on a live page, complete with a hatched "gate held" bar for
    /// a rule that does not exist.
    /// </remarks>
    [JsonPropertyName("symbols")] public required IReadOnlyList<FeedSymbol> Symbols { get; init; }

    /// <summary>Explains the blackout when one is in force. Empty when none is.</summary>
    [JsonPropertyName("blackoutNote")] public required string BlackoutNote { get; init; }

    /// <summary>What actually bounds position count. Empty if nothing does.</summary>
    [JsonPropertyName("concurrencyNote")] public required string ConcurrencyNote { get; init; }

    [JsonPropertyName("fundingNote")] public required string FundingNote { get; init; }
}

public static class DashboardFeedBuilder
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>US Eastern is UTC-4 for the whole contest window; no timezone database needed.</summary>
    private static readonly TimeSpan Eastern = TimeSpan.FromHours(-4);

    public static DashboardFeed Build(
        LogRun run,
        IReadOnlyList<SpreadPosition> positions,
        IReadOnlyList<decimal> equityCurve,
        IReadOnlyList<RealisedTrade> realised,
        IReadOnlyList<BlackoutEvent> blackouts,
        string tradedUnderlying,
        RiskMandate mandate,
        int contractsExamined,
        CompetitionCalendar calendar,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(realised);
        ArgumentNullException.ThrowIfNull(calendar);

        DateTimeOffset et = now.ToOffset(Eastern);

        return new DashboardFeed
        {
            GeneratedAt = DecisionLog.Stamp(now),
            Day = DayLabel(calendar, now),
            Clock = $"{et:HH:mm} ET | {et:MM.dd.yy}",
            DryRun = run.DryRun,
            Account = run.Account,
            Equity = run.Equity,
            Positions = positions.Select(p => ToFeed(p, run.Equity, now)).ToList(),
            Rejections = run.Decisions.Select(d => ToFeed(d, run.Timestamp)).ToList(),

            Closed = realised.Select(ToFeed).ToList(),
            Wins = RealisedTrades.Wins(realised),
            Losses = RealisedTrades.Losses(realised),

            PreGate = contractsExamined,
            Curve = equityCurve,
            CurveFrom = $"Inception {calendar.TradingOpens.ToOffset(Eastern):MM.dd}",
            CurveTo = $"{et:MM.dd}",
            CurveLabel = "Account equity",
            RiskDeployed = run.RiskPerTrade.DeployedDollars,
            RiskCeiling = run.RiskPerTrade.CeilingDollars,
            Symbols = BuildSymbols(positions, blackouts, tradedUnderlying, now),
            BlackoutNote = BlackoutNoteFor(blackouts, now),
            ConcurrencyNote =
                $"no count cap | bounded by the 3 and the 5, max {mandate.MaxContractsPerOrder} per order",
            FundingNote = $"funded at {Money.Usd(run.Equity)}",
        };
    }

    /// <summary>
    /// The underlyings worth showing: whatever is held, plus the one being traded, plus
    /// anything a blackout is currently holding shut.
    /// </summary>
    private static IReadOnlyList<FeedSymbol> BuildSymbols(
        IReadOnlyList<SpreadPosition> positions,
        IReadOnlyList<BlackoutEvent> blackouts,
        string traded,
        DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        BlackoutCalendar calendar = new(blackouts);

        return positions.Select(p => p.Spread.Underlying)
            .Append(traded)
            .Concat(blackouts.Select(b => b.Underlying))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                BlackoutVerdict verdict = calendar.Check(t, today);
                int held = positions.Count(p =>
                    string.Equals(p.Spread.Underlying, t, StringComparison.OrdinalIgnoreCase));

                return new FeedSymbol
                {
                    N = t,
                    Blackout = verdict.IsBlackedOut,
                    Note = verdict.IsBlackedOut && verdict.Cause is not null
                        ? $"blackout | {verdict.Cause.Reason.ToString().ToLowerInvariant()} {verdict.Cause.Date:MM.dd}"
                        : held == 0 ? "no position" : $"{held} position{(held > 1 ? "s" : "")}",
                };
            })
            .ToList();
    }

    private static string BlackoutNoteFor(IReadOnlyList<BlackoutEvent> blackouts, DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        BlackoutCalendar calendar = new(blackouts);

        List<string> held = blackouts
            .Select(b => b.Underlying)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => calendar.Check(t, today).IsBlackedOut)
            .ToList();

        return held.Count == 0
            ? string.Empty
            : $"{string.Join(", ", held)} refused outright. A blackout is a gate with no fill "
              + "-- the channel stays empty by rule, not by circumstance.";
    }

    /// <summary>
    /// The contest phase, read from the same calendar the gates read.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="CompetitionCalendar.PermissionAt"/> rather than from arithmetic
    /// on dates, so the header cannot disagree with the gates. Counting calendar days alone
    /// said "Day 4 of 4" for four hours after the Thursday close -- P&amp;L already measured,
    /// the calendar already reporting FlattenAll, and the header still describing a session in
    /// progress. Two clocks telling a judge different things about the same moment.
    /// </remarks>
    private static string DayLabel(CompetitionCalendar calendar, DateTimeOffset now)
    {
        // Counted on the Eastern date, because that is the date every other figure on the
        // page is stated in. now.Date is the UTC date, which rolls over at 20:00 ET and put
        // "Day 4 of 4" on the header at 20:33 ET on Wednesday -- Thursday announced four
        // hours before Thursday, on the one counter a judge reads as the contest clock.
        int day = Math.Clamp(
            (int)(now.ToOffset(Eastern).Date - calendar.TradingOpens.ToOffset(Eastern).Date).TotalDays + 1,
            1, 4);

        return calendar.PermissionAt(now) switch
        {
            TradingPermission.BeforeCompetitionOpens => "Pre-open",
            TradingPermission.OpenAndManage => $"Day {day} of 4",
            TradingPermission.ManageOnly => $"Day {day} of 4",
            TradingPermission.FlattenAll => "P&L measured",
            _ => "Closed",
        };
    }

    private static FeedPosition ToFeed(SpreadPosition p, decimal equity, DateTimeOffset now)
    {
        decimal longStrike = OccSymbol.Strike(p.Spread.LongSymbol) ?? 0m;
        decimal shortStrike = OccSymbol.Strike(p.Spread.ShortSymbol) ?? 0m;
        decimal maxLoss = p.Spread.MaxLoss(p.Contracts);
        // Days to expiry against the Eastern date, for the same reason. On the UTC date a
        // Wednesday-evening cycle reports a Thursday expiry as 0 DTE, a day early.
        int dte = p.Spread.Expiration.DayNumber
                - DateOnly.FromDateTime(now.ToOffset(Eastern).DateTime).DayNumber;

        return new FeedPosition
        {
            Sym = p.Spread.Underlying,
            Title = $"{longStrike:F0}/{shortStrike:F0} call debit spread",
            Kind = "Bull call | defined risk",
            Qty = p.Contracts,
            Legs = $"+{longStrike:F0}C / -{shortStrike:F0}C",
            Dte = dte < 0 ? 0 : dte,
            Open = Money.Usd(p.DebitPaid),
            N = p.Contracts,
            MlPer = Money.Usd(p.Spread.MaxLossPerContract),
            MaxLoss = Math.Round(maxLoss, 2),
            MaxLossPct = equity <= 0m ? 0m : Math.Round(maxLoss / equity * 100m, 2),
            Metrics =
            [
                new FeedMetric { K = "Mark", V = Money.Usd(p.CurrentValue) },
                new FeedMetric { K = "On risk", V = Money.Percent(p.ReturnOnRisk) },
                new FeedMetric { K = "Of max", V = Money.Percent(p.FractionOfMaxProfit) },
                new FeedMetric { K = "DTE", V = (dte < 0 ? 0 : dte).ToString(CultureInfo.InvariantCulture) },
            ],
        };
    }

    private static FeedClosed ToFeed(RealisedTrade t) => new()
    {
        Sym = t.Underlying,
        Title = $"{t.Structure} expiring {t.Expiration:MM.dd}",
        // What the broker can attest to. The ladder's reason for closing is recorded live in
        // the decision stream when it fires; inferring it back from fills would be a guess.
        Reason = $"closed {t.ClosedAt.ToOffset(Eastern):MM.dd HH:mm} ET over {t.Fills} fills",
        Pnl = t.RealisedPnl,
        Win = t.IsWin,
        ClosedOn = t.ClosedAt.ToOffset(Eastern).ToString("MM.dd", CultureInfo.InvariantCulture),
        Held = $"{HeldSessions(t)}d",
    };

    /// <summary>
    /// Trading sessions a position was held for, counted the same way the blackout window
    /// counts them. A position opened and closed inside one session is one, not zero: the
    /// card reports a holding period, and zero would read as never held.
    /// </summary>
    private static int HeldSessions(RealisedTrade t) => Math.Max(1, BlackoutCalendar.SessionsBetween(
        DateOnly.FromDateTime(t.OpenedAt.UtcDateTime),
        DateOnly.FromDateTime(t.ClosedAt.UtcDateTime)));

    private static FeedRejection ToFeed(Decision d, string runTimestamp)
    {
        string hhmm = DateTimeOffset.TryParse(
            runTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset ts)
            ? ts.ToOffset(Eastern).ToString("HH:mm", CultureInfo.InvariantCulture)
            : "--:--";

        return new FeedRejection
        {
            T = hhmm,
            Cand = string.IsNullOrEmpty(d.Structure) ? d.Underlying : $"{d.Underlying} {d.Structure}",
            Verdict = d.Verdict.ToString(),
            Gate = d.Gate,
            Reason = d.Finding,
            Executed = d.Executed,
        };
    }

    public static void Write(string directory, DashboardFeed feed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(feed);

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "dashboard.json"),
            JsonSerializer.Serialize(feed, Pretty));
    }
}
