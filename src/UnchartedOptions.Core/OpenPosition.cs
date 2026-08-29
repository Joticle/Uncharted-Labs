namespace UnchartedOptions.Core;

/// <summary>One open position as the broker reports it.</summary>
public sealed record OpenPosition
{
    public required string Symbol { get; init; }

    /// <summary>Underlying ticker. For options this is parsed from the OCC symbol.</summary>
    public required string Underlying { get; init; }

    public required bool IsOption { get; init; }

    /// <summary>Signed contract or share count. Negative for short legs.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>
    /// Total cost basis in dollars. Positive for a long leg, negative for a short leg, so
    /// summing the legs of a debit spread yields the net debit actually paid.
    /// </summary>
    public required decimal CostBasis { get; init; }

    public required decimal MarketValue { get; init; }

    public required decimal UnrealizedPl { get; init; }

    public DateOnly? Expiration => IsOption ? OccSymbol.Expiration(Symbol) : null;
}

/// <summary>Aggregates open positions into the exposure figures the gates consume.</summary>
public static class PortfolioExposure
{
    /// <summary>
    /// Capital currently at risk in one underlying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Summing signed cost basis across an underlying's legs is what makes this correct for
    /// spreads. A bull call debit spread reports as two positions -- a long leg with positive
    /// basis and a short leg with negative basis -- and the sum is the net debit, which for a
    /// defined-risk vertical is precisely the maximum loss.
    /// </para>
    /// <para>
    /// Without this the 5 gate is inert: it is implemented and tested, but nothing supplies
    /// it a real number, so the agent re-enters the same underlying on every cycle and
    /// concentration compounds silently. That is the exact failure the doctrine exists to
    /// prevent, so an unfed gate makes the public claim false rather than merely incomplete.
    /// </para>
    /// </remarks>
    public static decimal ForUnderlying(IReadOnlyList<OpenPosition> positions, string underlying)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);

        decimal net = positions
            .Where(p => string.Equals(p.Underlying, underlying, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.CostBasis);

        // Exposure is a magnitude. A net credit is not negative risk.
        return Math.Max(0m, net);
    }

    /// <summary>Total capital at risk across every underlying.</summary>
    public static decimal Total(IReadOnlyList<OpenPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        return positions
            .Select(p => p.Underlying)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(u => ForUnderlying(positions, u));
    }

    /// <summary>Distinct underlyings currently held.</summary>
    public static IReadOnlyList<string> Underlyings(IReadOnlyList<OpenPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        return positions
            .Select(p => p.Underlying)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
