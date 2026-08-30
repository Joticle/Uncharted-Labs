using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnchartedOptions.Core;

/// <summary>What the agent did with a candidate.</summary>
public enum Verdict
{
    /// <summary>Position opened.</summary>
    TAKEN,

    /// <summary>A spread was constructed and a gate declined it.</summary>
    REJECTED,

    /// <summary>Not evaluated at all -- blacked out, or outside the trading window.</summary>
    SKIPPED,

    /// <summary>An open position was unwound by the exit ladder.</summary>
    CLOSED,

    /// <summary>An open position was evaluated by the ladder and left alone.</summary>
    HELD,
}

/// <summary>
/// Numbers behind a decision. Absent values are zero, never null.
/// </summary>
/// <remarks>
/// A consumer rendering a fill bar cannot branch on null in the middle of a layout pass,
/// so every numeric key is always present and always a number.
/// </remarks>
public sealed record DecisionMetrics
{
    [JsonPropertyName("longStrike")] public decimal LongStrike { get; init; }
    [JsonPropertyName("shortStrike")] public decimal ShortStrike { get; init; }
    [JsonPropertyName("width")] public decimal Width { get; init; }
    [JsonPropertyName("delta")] public decimal Delta { get; init; }
    [JsonPropertyName("debit")] public decimal Debit { get; init; }
    [JsonPropertyName("rewardRisk")] public decimal RewardRisk { get; init; }
    [JsonPropertyName("costDragPercent")] public decimal CostDragPercent { get; init; }
    [JsonPropertyName("maxLossDollars")] public decimal MaxLossDollars { get; init; }
    [JsonPropertyName("contracts")] public int Contracts { get; init; }
    [JsonPropertyName("riskDollars")] public decimal RiskDollars { get; init; }
    [JsonPropertyName("riskPercent")] public decimal RiskPercent { get; init; }
}

/// <summary>One candidate and its outcome.</summary>
public sealed record Decision
{
    [JsonPropertyName("underlying")] public required string Underlying { get; init; }

    /// <summary>Human-readable structure, e.g. <c>776C/781C</c>. Empty when none was formed.</summary>
    [JsonPropertyName("structure")] public string Structure { get; init; } = string.Empty;

    [JsonPropertyName("verdict")]
    [JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
    public required Verdict Verdict { get; init; }

    /// <summary>Which gate decided, e.g. <c>cost-drag</c>, <c>reward-floor</c>, <c>blackout</c>.</summary>
    [JsonPropertyName("gate")] public required string Gate { get; init; }

    /// <summary>The finding, with its numbers in it.</summary>
    [JsonPropertyName("finding")] public required string Finding { get; init; }

    [JsonPropertyName("metrics")] public DecisionMetrics Metrics { get; init; } = new();

    /// <summary>Renders the line the dashboard shows.</summary>
    public string ToLine() =>
        $"{Underlying,-4} {Structure,-12} {Verdict,-9} {Finding}";
}

/// <summary>A gate's ceiling and how much of it is used. Both halves, so nothing is derived downstream.</summary>
public sealed record GateUtilisation
{
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("ceilingPercent")] public required decimal CeilingPercent { get; init; }
    [JsonPropertyName("ceilingDollars")] public required decimal CeilingDollars { get; init; }
    [JsonPropertyName("deployedDollars")] public required decimal DeployedDollars { get; init; }
    [JsonPropertyName("deployedPercent")] public required decimal DeployedPercent { get; init; }

    /// <summary>Fraction of the ceiling consumed, 0-1, pre-computed for a fill bar.</summary>
    [JsonPropertyName("utilisation")]
    public decimal Utilisation => CeilingDollars <= 0m ? 0m
        : Math.Round(Math.Min(1m, DeployedDollars / CeilingDollars), 4);
}

/// <summary>One evaluation cycle.</summary>
public sealed record LogRun
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
    [JsonPropertyName("account")] public required string Account { get; init; }
    [JsonPropertyName("profile")] public required string Profile { get; init; }
    [JsonPropertyName("isCompetition")] public required bool IsCompetition { get; init; }
    [JsonPropertyName("marketOpen")] public required bool MarketOpen { get; init; }
    [JsonPropertyName("equity")] public required decimal Equity { get; init; }
    [JsonPropertyName("calendarState")] public required string CalendarState { get; init; }
    [JsonPropertyName("riskPerTrade")] public required GateUtilisation RiskPerTrade { get; init; }
    [JsonPropertyName("symbolExposure")] public required IReadOnlyList<GateUtilisation> SymbolExposure { get; init; }
    [JsonPropertyName("decisions")] public required IReadOnlyList<Decision> Decisions { get; init; }
}

/// <summary>
/// The record of what the agent refused, and why.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the evidence that the gates are real lives nowhere else. Broker data
/// shows the positions that were opened; it cannot show the candidates that were declined,
/// which is the entire demonstration that a mandate is being enforced rather than described.
/// </para>
/// <para>
/// <b>It is not agent state.</b> Nothing in the mandate, the sizer or the ladder reads it
/// back, and deleting the file changes no decision the agent would make. That distinction is
/// the same one that removed the trailing stop: state the agent depends on would be a seam
/// in the claim that risk containment does not live in software. An append-only record the
/// agent never consults is an artifact, not a dependency.
/// </para>
/// <para>
/// Written as JSON Lines -- one run per line -- so appending is a genuine append rather than
/// a read-modify-write of a growing array. A single-object snapshot of the latest run is
/// written alongside it for consumers that want current state without parsing history.
/// </para>
/// </remarks>
public static class DecisionLog
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>ISO-8601 UTC, seconds precision, always with a trailing Z.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Deterministic run id: the UTC timestamp, compacted.</summary>
    public static string NewRunId(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string SerialiseRun(LogRun run) => JsonSerializer.Serialize(run, Json);

    /// <summary>Appends one run and refreshes the latest-run snapshot beside it.</summary>
    public static void Append(string directory, LogRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(run);

        Directory.CreateDirectory(directory);

        File.AppendAllText(
            Path.Combine(directory, "decisions.jsonl"),
            SerialiseRun(run) + Environment.NewLine);

        File.WriteAllText(
            Path.Combine(directory, "latest.json"),
            JsonSerializer.Serialize(run, Pretty));
    }

    /// <summary>Builds the per-underlying exposure gates from live positions.</summary>
    public static IReadOnlyList<GateUtilisation> ExposureGates(
        IReadOnlyList<OpenPosition> positions, decimal equity, RiskMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(mandate);

        decimal ceiling = equity * mandate.MaxSymbolExposurePct;

        return PortfolioExposure.Underlyings(positions)
            .Select(u =>
            {
                decimal deployed = PortfolioExposure.ForUnderlying(positions, u);
                return new GateUtilisation
                {
                    Label = u,
                    CeilingPercent = Math.Round(mandate.MaxSymbolExposurePct * 100m, 2),
                    CeilingDollars = Math.Round(ceiling, 2),
                    DeployedDollars = Math.Round(deployed, 2),
                    DeployedPercent = equity <= 0m ? 0m : Math.Round(deployed / equity * 100m, 2),
                };
            })
            .ToList();
    }
}
