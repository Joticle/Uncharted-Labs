namespace UnchartedOptions.Core;

/// <summary>
/// Rebuilds spread positions from the individual legs the broker reports.
/// </summary>
/// <remarks>
/// Alpaca has no concept of a spread: a vertical is two independent option positions that
/// happen to have been opened together. The agent's risk model is expressed in spreads, so
/// the legs have to be paired back up before the exit ladder can say anything about them.
/// </remarks>
public static class SpreadReconstruction
{
    /// <summary>
    /// Pairs option legs into spreads by underlying and expiry.
    /// </summary>
    /// <param name="positions">Open positions as reported by the broker.</param>
    /// <param name="fillTimes">
    /// Fill time per option symbol, from the broker's own order history. The time stop
    /// depends on this, so it is read rather than assumed -- a guessed entry time would
    /// make the stage fire against a number nothing in the system actually knows.
    /// </param>
    /// <param name="fallbackOpenedAt">Used only when the broker has no fill record for a leg.</param>
    public static IReadOnlyList<SpreadPosition> FromLegs(
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyDictionary<string, DateTimeOffset> fillTimes,
        DateTimeOffset fallbackOpenedAt)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fillTimes);

        List<SpreadPosition> spreads = [];

        var groups = positions
            .Where(p => p.IsOption && p.Expiration is not null)
            .GroupBy(p => (p.Underlying, p.Expiration!.Value));

        foreach (var group in groups)
        {
            OpenPosition? longLeg = group.FirstOrDefault(p => p.Quantity > 0);
            OpenPosition? shortLeg = group.FirstOrDefault(p => p.Quantity < 0);

            // A group that is not a matched pair is left alone rather than guessed at. A
            // lone long leg is a valid position, but it is not a defined-risk spread and
            // the ladder's stages do not describe it.
            if (longLeg is null || shortLeg is null)
            {
                continue;
            }

            decimal? longStrike = OccSymbol.Strike(longLeg.Symbol);
            decimal? shortStrike = OccSymbol.Strike(shortLeg.Symbol);

            if (longStrike is null || shortStrike is null || shortStrike <= longStrike)
            {
                continue;
            }

            int contracts = (int)Math.Abs(longLeg.Quantity);
            if (contracts <= 0)
            {
                continue;
            }

            decimal shares = contracts * VerticalSpread.ContractMultiplier;

            // Signed sums net correctly: the long leg carries positive basis and value, the
            // short leg negative. Their sum is the debit paid and the current mark.
            decimal debitPerShare = (longLeg.CostBasis + shortLeg.CostBasis) / shares;
            decimal valuePerShare = (longLeg.MarketValue + shortLeg.MarketValue) / shares;

            if (debitPerShare <= 0m)
            {
                continue;
            }

            spreads.Add(new SpreadPosition
            {
                Spread = new VerticalSpread
                {
                    Underlying = group.Key.Underlying,
                    Direction = SpreadDirection.BullCall,
                    LongSymbol = longLeg.Symbol,
                    ShortSymbol = shortLeg.Symbol,
                    NetDebit = debitPerShare,
                    StrikeWidth = shortStrike.Value - longStrike.Value,
                    Expiration = group.Key.Item2,
                },
                Contracts = contracts,
                CurrentValue = valuePerShare,
                OpenedAt = EarliestFill(fillTimes, longLeg.Symbol, shortLeg.Symbol) ?? fallbackOpenedAt,
            });
        }

        return spreads;
    }

    /// <summary>
    /// The earlier of the two legs' fills. They go out as one order, so these should agree,
    /// but taking the earlier is the conservative reading of how long capital has been tied up.
    /// </summary>
    private static DateTimeOffset? EarliestFill(
        IReadOnlyDictionary<string, DateTimeOffset> fills, string longSymbol, string shortSymbol)
    {
        bool hasLong = fills.TryGetValue(longSymbol, out DateTimeOffset l);
        bool hasShort = fills.TryGetValue(shortSymbol, out DateTimeOffset s);

        return (hasLong, hasShort) switch
        {
            (true, true) => l < s ? l : s,
            (true, false) => l,
            (false, true) => s,
            _ => null,
        };
    }
}
