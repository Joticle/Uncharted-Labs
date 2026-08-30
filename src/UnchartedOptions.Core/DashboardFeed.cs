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



/// <summary>A closed trade with its realised outcome.</summary>

public sealed record FeedClosed

{

    [JsonPropertyName("sym")] public required string Sym { get; init; }

    [JsonPropertyName("title")] public required string Title { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }

    [JsonPropertyName("pnl")] public required decimal Pnl { get; init; }

    [JsonPropertyName("win")] public required bool Win { get; init; }

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

        };

    }



    private static string DayLabel(CompetitionCalendar calendar, DateTimeOffset now)

    {

        if (now < calendar.TradingOpens)

        {

            return "Pre-open";

        }



        int day = (int)(now.Date - calendar.TradingOpens.Date).TotalDays + 1;

        return day is >= 1 and <= 4 ? $"Day {day} of 4" : "Closed";

    }



    private static FeedPosition ToFeed(SpreadPosition p, decimal equity, DateTimeOffset now)

    {

        decimal longStrike = OccSymbol.Strike(p.Spread.LongSymbol) ?? 0m;

        decimal shortStrike = OccSymbol.Strike(p.Spread.ShortSymbol) ?? 0m;

        decimal maxLoss = p.Spread.MaxLoss(p.Contracts);

        int dte = p.Spread.Expiration.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;



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

    };



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

