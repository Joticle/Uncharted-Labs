namespace UnchartedOptions.Core;

/// <summary>
/// Account state as Uncharted Options sees it. Sizing reads <see cref="Equity"/> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Hazard H1. Alpaca returns five adjacent balance fields, and four of them are wrong
/// for position sizing. Observed on the hackathon paper account:
/// </para>
/// <code>
///   equity                        100,000   &lt;- the only correct sizing base
///   buying_power                  400,000   4x margin
///   regt_buying_power             200,000
///   options_buying_power          100,000   not leveraged
///   non_marginable_buying_power   100,000
/// </code>
/// <para>
/// <c>buying_power</c> sorts first alphabetically and is what an autocomplete lands on.
/// Binding sizing to it would silently quadruple every position. The predecessor codebase
/// had no <c>Equity</c> property at all -- it spelled the concept <c>PortfolioValue</c>,
/// which is precisely why the trap was live there.
/// </para>
/// <para>
/// This type therefore does not expose raw buying power at all. The footgun is removed
/// rather than documented.
/// </para>
/// </remarks>
public sealed record Account
{
    /// <summary>Alpaca account number. Carried for audit trails, never used in arithmetic.</summary>
    public required string AccountNumber { get; init; }

    /// <summary>
    /// Total account equity, mapped from Alpaca's <c>equity</c> field.
    /// This is the sole base for every risk and exposure calculation.
    /// </summary>
    public required decimal Equity { get; init; }

    /// <summary>
    /// Mapped from Alpaca's <c>options_buying_power</c>. Options are not marginable the way
    /// equities are, so this figure is unleveraged and equals equity on a cash-like account.
    /// </summary>
    /// <remarks>
    /// Legitimate as a <c>Math.Min</c> ceiling on affordability. It must never become the
    /// base of a risk or exposure calculation -- that is what <see cref="Equity"/> is for.
    /// </remarks>
    public required decimal OptionsBuyingPower { get; init; }

    /// <summary>
    /// Effective options trading level, 0-3, from Alpaca's <c>options_trading_level</c>.
    /// </summary>
    /// <remarks>
    /// Multi-leg spreads require level 3. A newly created account can default lower, and a
    /// level-2 account rejects every multi-leg order it is sent -- a failure that would
    /// otherwise surface at the opening bell rather than during setup.
    /// </remarks>
    public required int OptionsTradingLevel { get; init; }

    /// <summary>Whether this account may trade the defined-risk verticals the strategy needs.</summary>
    public bool CanTradeSpreads => OptionsTradingLevel >= 3;
}
