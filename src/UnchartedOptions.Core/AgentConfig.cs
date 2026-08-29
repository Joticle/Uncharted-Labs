using System.Globalization;

namespace UnchartedOptions.Core;

/// <summary>
/// Everything about what the agent trades, separate from how it manages risk.
/// </summary>
/// <remarks>
/// Expiry in particular must be a value rather than a constant. P&amp;L is scored at a fixed
/// instant, so the right expiry is a function of the measurement date rather than of the
/// strategy -- and it needs to be swappable to compare a short-dated series against a
/// longer-dated one on the dev account before committing to either.
/// </remarks>
public sealed record AgentConfig
{
    public string Underlying { get; init; } = "SPY";

    /// <summary>
    /// Expiry to trade.
    /// </summary>
    /// <remarks>
    /// Defaults to 3 Sep 2026, the final scored day. A spread expiring then settles into the
    /// judged equity at full intrinsic value; a longer-dated spread is instead marked at a
    /// mid-quote after carrying weeks of theta it never gets to convert.
    /// </remarks>
    public DateOnly TargetExpiration { get; init; } = new(2026, 9, 3);

    /// <summary>
    /// Clearing the reward:risk floor is the bar, not a quantity to maximise. Taking the
    /// widest qualifying spread would pay probability for ratio.
    /// </summary>
    public WidthPolicy WidthPolicy { get; init; } = WidthPolicy.NarrowestQualifying;

    public RiskMandate Mandate { get; init; } = new();

    /// <summary>How far above spot to search for strikes.</summary>
    public decimal StrikeSearchBand { get; init; } = 120m;

    /// <summary>
    /// Earnings dates as <c>SYMBOL:YYYY-MM-DD</c>. Supplied explicitly because Alpaca's
    /// corporate-actions endpoint does not publish them.
    /// </summary>
    public IReadOnlyList<string> EarningsDates { get; init; } = [];

    /// <summary>Trading sessions of clearance required either side of a blackout event.</summary>
    public int BlackoutSessions { get; init; } = 3;

    /// <summary>
    /// Where the decision log is written. Committed by CI, never read back.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>log/</c>. The standard .NET gitignore excludes <c>[Ll]og/</c> as
    /// build output, which would silently prevent the record from ever being committed --
    /// the log would appear to work locally and produce nothing in CI.
    /// </remarks>
    public string LogDirectory { get; init; } = "decisions";

    /// <summary>
    /// Applies <c>--expiry YYYY-MM-DD</c>, <c>--underlying SYM</c> and <c>--widest</c>.
    /// </summary>
    public static AgentConfig FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        AgentConfig config = new();

        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--expiry", StringComparison.OrdinalIgnoreCase)
                && DateOnly.TryParseExact(
                    args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateOnly expiry))
            {
                config = config with { TargetExpiration = expiry };
            }

            if (string.Equals(args[i], "--underlying", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { Underlying = args[i + 1].ToUpperInvariant() };
            }
        }

        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--earnings", StringComparison.OrdinalIgnoreCase))
            {
                config = config with
                {
                    EarningsDates = args[i + 1]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                };
            }

            if (string.Equals(args[i], "--blackout-sessions", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sessions))
            {
                config = config with { BlackoutSessions = sessions };
            }

            if (string.Equals(args[i], "--log-dir", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { LogDirectory = args[i + 1] };
            }
        }

        // Earnings may also arrive from the environment, which is how CI supplies them
        // without putting dates in the workflow file.
        string? fromEnv = Environment.GetEnvironmentVariable("UNCHARTED_EARNINGS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            config = config with
            {
                EarningsDates = [.. config.EarningsDates,
                    .. fromEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            };
        }

        if (args.Any(a => string.Equals(a, "--widest", StringComparison.OrdinalIgnoreCase)))
        {
            config = config with { WidthPolicy = WidthPolicy.BestRewardRisk };
        }

        return config;
    }
}
