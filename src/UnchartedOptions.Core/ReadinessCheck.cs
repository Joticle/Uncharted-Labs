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
    public static ReadinessReport Run(Account account, TradingProfile profile, decimal expectedEquity)
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
                Name = $"Equity is {Money.Usd(expectedEquity)}",
                Passed = account.Equity == expectedEquity,
                Detail = account.Equity == expectedEquity
                    ? Money.Usd(account.Equity)
                    : $"{Money.Usd(account.Equity)}, expected {Money.Usd(expectedEquity)}",
            },
        ];

        return new ReadinessReport { Items = items };
    }
}
