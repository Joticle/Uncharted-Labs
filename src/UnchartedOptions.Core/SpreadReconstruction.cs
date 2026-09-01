namespace UnchartedOptions.Core;

/// <summary>
/// Raised when reconstructed spreads do not account for the legs the broker reports.
/// </summary>
/// <remarks>
/// Loud on purpose. The first version of this reconstruction silently dropped a leg: three
/// legs on one underlying and expiry collapsed into a single pair, the third was discarded,
/// and the only visible trace was an $80 discrepancy between the reported maximum loss and
/// the account's own cost basis. Everything downstream believed it -- the panel rendered a
/// position that never existed at 4.95% against a 3% ceiling, and the exit ladder would have
/// sent a close order for more contracts than were held. A reconstruction that cannot account
/// for every leg must stop, not render.
/// </remarks>
public sealed class LegConservationException : Exception
{
    public LegConservationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Rebuilds spread positions from the individual legs the broker reports.
/// </summary>
/// <remarks>
/// Alpaca has no concept of a spread: a vertical is two independent option positions that
/// happen to have been opened together, and it aggregates them by symbol. Three lots sharing
/// a long strike arrive as one leg of thirty contracts, so the structures have to be
/// recovered before the risk model can say anything about them.
/// </remarks>
public static class SpreadReconstruction
{
    private sealed class Remaining
    {
        public required OpenPosition Leg { get; init; }
        public required decimal Strike { get; init; }
        public decimal Contracts { get; set; }
        public decimal BasisPerContract { get; init; }
        public decimal ValuePerContract { get; init; }
        public decimal Allocated { get; set; }
    }

    /// <summary>
    /// Pairs option legs into spreads by underlying and expiry.
    /// </summary>
    /// <param name="positions">Open positions as reported by the broker.</param>
    /// <param name="fillTimes">Fill time per option symbol, from the broker's order history.</param>
    /// <param name="fallbackOpenedAt">Used only when the broker has no fill record for a leg.</param>
    /// <exception cref="LegConservationException">
    /// Thrown when the reconstructed spreads do not account for every contract and every
    /// dollar of basis the broker reports.
    /// </exception>
    public static IReadOnlyList<SpreadPosition> FromLegs(
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyDictionary<string, DateTimeOffset> fillTimes,
        DateTimeOffset fallbackOpenedAt)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(fillTimes);

        List<SpreadPosition> spreads = [];

        var groups = positions
            .Where(p => p.IsOption && p.Expiration is not null && OccSymbol.Strike(p.Symbol) is not null)
            .GroupBy(p => (p.Underlying, p.Expiration!.Value));

        foreach (var group in groups)
        {
            spreads.AddRange(PairGroup(group.Key.Underlying, group.Key.Item2, [.. group], fillTimes, fallbackOpenedAt));
        }

        return spreads;
    }

    private static List<SpreadPosition> PairGroup(
        string underlying,
        DateOnly expiration,
        IReadOnlyList<OpenPosition> legs,
        IReadOnlyDictionary<string, DateTimeOffset> fillTimes,
        DateTimeOffset fallbackOpenedAt)
    {
        static Remaining Track(OpenPosition p)
        {
            decimal qty = Math.Abs(p.Quantity);
            return new Remaining
            {
                Leg = p,
                Strike = OccSymbol.Strike(p.Symbol)!.Value,
                Contracts = qty,
                BasisPerContract = qty == 0m ? 0m : p.CostBasis / qty,
                ValuePerContract = qty == 0m ? 0m : p.MarketValue / qty,
            };
        }

        List<Remaining> longs = legs.Where(l => l.Quantity > 0).Select(Track).OrderBy(l => l.Strike).ToList();
        List<Remaining> shorts = legs.Where(l => l.Quantity < 0).Select(Track).OrderBy(l => l.Strike).ToList();

        List<SpreadPosition> built = [];

        // Narrowest short first, paired against the highest long strike beneath it.
        //
        // This is a chosen convention, not a recovered fact. The broker aggregates by symbol
        // and keeps no record of which long contracts were bought alongside which short, so
        // once three lots share a long strike the original pairing is genuinely unrecoverable
        // from position data. Narrowest-first reproduces the orders actually placed here --
        // 764/769 before 764/774 -- because the tighter spread was opened first. A different
        // fill sequence could make it wrong, and it would then describe structures the account
        // holds economically but never traded as such. The conservation check below still
        // holds in that case: every contract and every dollar is accounted for, and the risk
        // totals stay correct even where the attribution to individual structures does not.
        foreach (Remaining shortLeg in shorts)
        {
            while (shortLeg.Contracts > 0m)
            {
                Remaining? longLeg = longs
                    .Where(l => l.Contracts > 0m && l.Strike < shortLeg.Strike)
                    .OrderByDescending(l => l.Strike)
                    .FirstOrDefault();

                if (longLeg is null)
                {
                    break;
                }

                decimal take = Math.Min(longLeg.Contracts, shortLeg.Contracts);
                longLeg.Contracts -= take;
                shortLeg.Contracts -= take;
                longLeg.Allocated += take;
                shortLeg.Allocated += take;

                built.Add(Build(underlying, expiration, longLeg, shortLeg, (int)take, fillTimes, fallbackOpenedAt));
            }
        }

        Conserve(underlying, expiration, legs, longs, shorts, built);
        return built;
    }

    private static SpreadPosition Build(
        string underlying,
        DateOnly expiration,
        Remaining longLeg,
        Remaining shortLeg,
        int contracts,
        IReadOnlyDictionary<string, DateTimeOffset> fillTimes,
        DateTimeOffset fallbackOpenedAt)
    {
        // Basis and value follow the contracts, so splitting a leg across two structures
        // splits its cost with it.
        decimal debitPerShare =
            (longLeg.BasisPerContract + shortLeg.BasisPerContract) / VerticalSpread.ContractMultiplier;
        decimal valuePerShare =
            (longLeg.ValuePerContract + shortLeg.ValuePerContract) / VerticalSpread.ContractMultiplier;

        return new SpreadPosition
        {
            Spread = new VerticalSpread
            {
                Underlying = underlying,
                Direction = SpreadDirection.BullCall,
                LongSymbol = longLeg.Leg.Symbol,
                ShortSymbol = shortLeg.Leg.Symbol,
                NetDebit = debitPerShare,
                StrikeWidth = shortLeg.Strike - longLeg.Strike,
                Expiration = expiration,
            },
            Contracts = contracts,
            CurrentValue = valuePerShare,
            OpenedAt = EarliestFill(fillTimes, longLeg.Leg.Symbol, shortLeg.Leg.Symbol) ?? fallbackOpenedAt,
        };
    }

    /// <summary>
    /// Every contract and every dollar the broker reports must appear in exactly one spread.
    /// </summary>
    private static void Conserve(
        string underlying,
        DateOnly expiration,
        IReadOnlyList<OpenPosition> legs,
        List<Remaining> longs,
        List<Remaining> shorts,
        List<SpreadPosition> built)
    {
        List<string> faults = [];

        foreach (Remaining r in longs.Concat(shorts))
        {
            decimal expected = Math.Abs(r.Leg.Quantity);

            if (r.Allocated != expected)
            {
                faults.Add($"{r.Leg.Symbol}: {r.Allocated} of {expected} contract(s) paired");
            }
        }

        decimal brokerBasis = legs.Sum(l => l.CostBasis);
        decimal builtBasis = built.Sum(s => s.Spread.NetDebit * s.Contracts * VerticalSpread.ContractMultiplier);

        // A cent of tolerance: per-contract basis is a division, and the broker reports
        // fractional cents on aggregated legs.
        if (Math.Abs(brokerBasis - builtBasis) > 0.01m)
        {
            faults.Add($"basis {builtBasis:F2} reconstructed against {brokerBasis:F2} reported "
                     + $"(difference {brokerBasis - builtBasis:F2})");
        }

        if (faults.Count > 0)
        {
            throw new LegConservationException(
                $"Reconstruction of {underlying} {expiration:yyyy-MM-dd} does not account for the broker's "
                + $"legs: {string.Join("; ", faults)}. Refusing to report a book that differs from the account.");
        }
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
