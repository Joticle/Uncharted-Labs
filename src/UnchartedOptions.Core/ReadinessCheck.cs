namespace UnchartedOptions.Core;

public sealed record ReadinessItem
{
    public required string Name { get; init; }

    public required bool Passed { get; init; }

    public required string Detail { get; init; }
}

public sealed record ReadinessReport
{
    public required IReadOnlyList<ReadinessItem> Items { get; init; }

    public bool Ready => Items.All(i => i.Passed);
}

/// <summary>
/// Verifies an account is configured to do what the strategy requires, before the bell.
/// </summary>
/// <remarks>
/// Each check here corresponds to a setup mistake that is invisible until it costs a
/// trading day: a new account defaulting below options level 3, a balance that was never
/// set to the required figure, or a CLI profile authenticated against the wrong account.
/// </remarks>
public static class ReadinessCheck
{
    /// <param name="startingBalance">
    /// The balance the account was required to be funded at. Reported for comparison, but not
    /// asserted: equity moves the moment the agent trades, so a hard equality check would turn
    /// every run after the first fill into a failure. What is asserted is that equity is
    /// present and non-zero -- an account returning nothing cannot be sized against.
    /// </param>
    public static ReadinessReport Run(Account account, TradingProfile profile, decimal startingBalance)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(profile);

        bool rightAccount = string.Equals(
            account.AccountNumber, profile.ExpectedAccountNumber, StringComparison.OrdinalIgnoreCase);

        List<ReadinessItem> items =
        [
            new ReadinessItem
            {
                Name = $"Profile '{profile.CliProfile}' resolves to the right account",
                Passed = rightAccount,
                Detail = rightAccount
                    ? account.AccountNumber
                    : $"expected {profile.ExpectedAccountNumber}, got {account.AccountNumber} -- "
                      + "the profile is authenticated against the wrong account",
            },
            new ReadinessItem
            {
                Name = "Options trading level 3 (spreads and multi-leg)",
                Passed = account.CanTradeSpreads,
                Detail = account.CanTradeSpreads
                    ? $"level {account.OptionsTradingLevel}"
                    : $"level {account.OptionsTradingLevel} -- multi-leg orders will be rejected. "
                      + "Raise it under Account > Configure > Options",
            },
            new ReadinessItem
            {
                Name = "Equity is reported and non-zero",
                Passed = account.Equity > 0m,
                Detail = account.Equity <= 0m
                    ? "account reports no equity -- nothing can be sized against it"
                    : account.Equity == startingBalance
                        ? $"{Money.Usd(account.Equity)} (at the required starting balance)"
                        : $"{Money.Usd(account.Equity)} against a {Money.Usd(startingBalance)} "
                          + "starting balance -- expected once trading has begun",
            },
        ];

        return new ReadinessReport { Items = items };
    }
}
