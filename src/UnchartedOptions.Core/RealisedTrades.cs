namespace UnchartedOptions.Core;

/// <summary>One execution, as the broker reports it.</summary>
public sealed record Fill
{
    public required string Symbol { get; init; }

    public required bool IsBuy { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>Price per share. Multiply by the contract multiplier for cash.</summary>
    public required decimal Price { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Cash effect of this fill. Buying spends, selling receives.
    /// </summary>
    public decimal CashFlow =>
        (IsBuy ? -1m : 1m) * Price * Quantity * VerticalSpread.ContractMultiplier;

    /// <summary>Signed contract count: buys add, sells subtract.</summary>
    public decimal SignedQuantity => (IsBuy ? 1m : -1m) * Quantity;
}

/// <summary>A spread that has been fully unwound, with what it actually made or lost.</summary>
public sealed record RealisedTrade
{
    public required string Underlying { get; init; }

    public required DateOnly Expiration { get; init; }

    /// <summary>Strikes involved, lowest first.</summary>
    public required IReadOnlyList<decimal> Strikes { get; init; }

    /// <summary>Realised profit or loss in dollars. Negative is a loss.</summary>
    public required decimal RealisedPnl { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public required DateTimeOffset ClosedAt { get; init; }

    public required int Fills { get; init; }

    public bool IsWin => RealisedPnl > 0m;

    public string Structure => Strikes.Count == 2
        ? $"{Strikes[0]:F0}C/{Strikes[1]:F0}C"
        : string.Join("/", Strikes.Select(s => $"{s:F0}"));
}

/// <summary>
/// Derives closed trades and their realised profit from execution fills.
/// </summary>
/// <remarks>
/// <para>
/// Alpaca publishes no realised-profit figure per trade. It publishes fills, and realised
/// profit is what falls out of pairing them: sum the signed cash of every fill touching a
/// spread, and once the net contract count returns to zero that sum is what the position made.
/// </para>
/// <para>
/// Grouping is by underlying and expiry rather than by individual option symbol, because a
/// vertical is two symbols that are only meaningful together -- netting one leg alone would
/// report the long leg's loss and the short leg's gain as two separate trades.
/// </para>
/// </remarks>
public static class RealisedTrades
{
    public static IReadOnlyList<RealisedTrade> FromFills(IReadOnlyList<Fill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        List<RealisedTrade> closed = [];

        var groups = fills
            .Where(f => OccSymbol.IsWellFormed(f.Symbol))
            .Select(f => new
            {
                Fill = f,
                Underlying = OccSymbol.Underlying(f.Symbol)!,
                Expiry = OccSymbol.Expiration(f.Symbol),
                Strike = OccSymbol.Strike(f.Symbol),
            })
            .Where(x => x.Expiry is not null && x.Strike is not null)
            .GroupBy(x => (x.Underlying, x.Expiry!.Value));

        foreach (var group in groups)
        {
            // Netting must be per symbol, not across the group. A vertical's legs carry
            // opposite signs by construction -- long +10, short -10 -- so the group's net
            // quantity is zero the moment it is opened. Only when every individual leg has
            // returned to zero is the spread actually closed.
            bool everyLegFlat = group
                .GroupBy(x => x.Fill.Symbol, StringComparer.OrdinalIgnoreCase)
                .All(leg => leg.Sum(x => x.Fill.SignedQuantity) == 0m);

            if (!everyLegFlat)
            {
                continue;
            }

            List<decimal> strikes = group
                .Select(x => x.Strike!.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            closed.Add(new RealisedTrade
            {
                Underlying = group.Key.Underlying,
                Expiration = group.Key.Item2,
                Strikes = strikes,
                RealisedPnl = Math.Round(group.Sum(x => x.Fill.CashFlow), 2),
                OpenedAt = group.Min(x => x.Fill.At),
                ClosedAt = group.Max(x => x.Fill.At),
                Fills = group.Count(),
            });
        }

        return closed.OrderByDescending(t => t.ClosedAt).ToList();
    }

    public static int Wins(IReadOnlyList<RealisedTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        return trades.Count(t => t.IsWin);
    }

    public static int Losses(IReadOnlyList<RealisedTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        return trades.Count(t => !t.IsWin);
    }
}
