using System.Globalization;

namespace UnchartedOptions.Core;

/// <summary>
/// The command line the agent accepts, and nothing else.
/// </summary>
/// <remarks>
/// Parsing used to be a set of independent scans, each looking for the flag it cared about
/// and ignoring everything else. An argument nobody recognised was therefore not an error --
/// it was silence. <c>--profile comp</c> looks exactly like a request for the judged account
/// and is not a flag this agent has: it fell through to the dev default and ran against the
/// wrong book for a whole afternoon, producing a fill history that disagreed with the
/// positions on screen and sending a real investigation after a bug that was not there.
/// With <c>--live</c> alongside it, the same silence would have placed orders on the wrong
/// account. A misspelt flag has to stop the run, not redirect it.
/// </remarks>
public static class AgentArguments
{
    /// <summary>Exit code for a command line the agent will not accept.</summary>
    /// <remarks>
    /// Distinct from 2, which CI treats as the competition-window guard declining to trade
    /// and deliberately allows to pass. A usage fault must not hide inside that.
    /// </remarks>
    public const int UsageExitCode = 64;

    private static readonly string[] Switches = ["--live", "--verify", "--preflight", "--comp"];

    private static readonly string[] Valued =
        ["--expiry", "--underlying", "--earnings", "--blackout-sessions", "--log-dir", "--as-of"];

    public static string Usage =>
        "  --live                     place and close real orders (default: dry run)\n"
        + "  --comp                     target the judged competition account (default: dev)\n"
        + "  --verify                   account configuration check, then exit\n"
        + "  --preflight                readiness report, then exit\n"
        + "  --expiry <yyyy-MM-dd>      target expiration\n"
        + "  --underlying <symbol>      underlying to evaluate\n"
        + "  --earnings <d,d,...>       earnings dates to black out\n"
        + "  --blackout-sessions <n>    sessions to avoid either side of earnings\n"
        + "  --log-dir <path>           where decisions and the dashboard feed are written\n"
        + "  --as-of <timestamp>        simulated clock; refused on the competition account";

    /// <summary>
    /// Everything wrong with this command line. Empty means the agent will accept it.
    /// </summary>
    public static IReadOnlyList<string> Faults(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> faults = [];

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (Switches.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Valued.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                faults.Add($"unrecognised argument '{arg}'{Suggest(arg)}");

                // Swallow what follows if it looks like the value that was meant for it, so
                // '--profile comp' reads as one mistake rather than two.
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            // A valued flag in the last position was previously dropped without a word,
            // because every scan stopped at Count - 1 to leave room for the value.
            if (i + 1 >= args.Count)
            {
                faults.Add($"{arg} needs a value");
                continue;
            }

            string value = args[++i];
            string? bad = Reject(arg, value);

            if (bad is not null)
            {
                faults.Add(bad);
            }
        }

        return faults;
    }

    /// <summary>Why a value is unusable, or null if it parses.</summary>
    private static string? Reject(string flag, string value) => flag.ToLowerInvariant() switch
    {
        "--expiry" when !DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            => $"--expiry '{value}' is not a yyyy-MM-dd date",

        "--blackout-sessions" when !int.TryParse(
            value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0
            => $"--blackout-sessions '{value}' is not a session count",

        "--as-of" when !DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)
            => $"--as-of '{value}' is not a timestamp",

        "--underlying" when string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)
            => $"--underlying '{value}' is not a symbol",

        "--log-dir" when string.IsNullOrWhiteSpace(value)
            => "--log-dir needs a path",

        _ => null,
    };

    /// <summary>
    /// Names the flag a typo most likely meant. <c>--profile</c> is called out by name
    /// because it is the plausible-looking one that caused the misfire.
    /// </summary>
    private static string Suggest(string arg)
    {
        if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase))
        {
            return ". The account is selected with --comp, not --profile";
        }

        if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
        {
            return ". A dry run is the default; --live is what opts out of it";
        }

        string? near = Switches.Concat(Valued)
            .FirstOrDefault(k => k.Contains(arg.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

        return near is null ? string.Empty : $". Did you mean {near}?";
    }
}
