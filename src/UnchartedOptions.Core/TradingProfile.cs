namespace UnchartedOptions.Core;

/// <summary>
/// Which Alpaca account the agent is pointed at.
/// </summary>
/// <remarks>
/// The competition requires a dedicated paper account that is not used for testing, so the
/// agent must be able to target either account with no code change. Selection is by CLI
/// profile name, and <see cref="Dev"/> is the default everywhere -- reaching the competition
/// account always takes an explicit act.
/// </remarks>
public sealed record TradingProfile
{
    /// <summary>Name of the Alpaca CLI profile, passed through as <c>-p</c>.</summary>
    public required string CliProfile { get; init; }

    public required bool IsCompetition { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// The account number this profile must resolve to.
    /// </summary>
    /// <remarks>
    /// Profile names are chosen locally; the OAuth flow decides which account is actually
    /// behind one. Authenticating the wrong account under the name "comp" would look
    /// entirely normal until the agent traded the wrong book, so the binding is asserted
    /// rather than assumed.
    /// </remarks>
    public required string ExpectedAccountNumber { get; init; }

    /// <summary>Development account. Prototype and test freely here.</summary>
    public static TradingProfile Dev { get; } = new()
    {
        CliProfile = "paper",
        IsCompetition = false,
        Description = "dev account (safe to test against)",
        ExpectedAccountNumber = "PA3ILISQPBT4",
    };

    /// <summary>
    /// The judged competition account. Must be funded at $100,000 and never used for testing.
    /// </summary>
    public static TradingProfile Competition { get; } = new()
    {
        CliProfile = "comp",
        IsCompetition = true,
        Description = "COMPETITION account (judged)",
        ExpectedAccountNumber = "PA3BG520YCTT",
    };

    /// <summary>
    /// Resolves a profile from command-line arguments. Anything other than an explicit
    /// <c>--comp</c> yields the dev account.
    /// </summary>
    public static TradingProfile FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(a => string.Equals(a, "--comp", StringComparison.OrdinalIgnoreCase))
            ? Competition
            : Dev;
    }
}
