using UnchartedOptions.Alpaca;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// The book the account actually held on 1 Sep 2026, and what closing it must emit.
/// </summary>
/// <remarks>
/// Three lots shared a long strike, so the broker reported one leg of thirty contracts
/// against two short strikes. The reconstruction paired the first short it found and dropped
/// the other, inventing a single 30-contract spread worth 4.95% of equity against a 3%
/// ceiling, and leaving one leg outside the exit ladder entirely.
/// </remarks>
public class MultiLotReconstructionTests
{
    private const string Long764 = "SPY260903C00764000";
    private const string Short769 = "SPY260903C00769000";
    private const string Short774 = "SPY260903C00774000";

    private static OpenPosition Leg(string symbol, decimal qty, decimal basis, decimal marketValue) => new()
    {
        Symbol = symbol,
        Underlying = OccSymbol.Underlying(symbol)!,
        IsOption = true,
        Quantity = qty,
        CostBasis = basis,
        MarketValue = marketValue,
        UnrealizedPl = marketValue - basis,
    };

    /// <summary>Exactly what <c>alpaca position list</c> returned for the comp account.</summary>
    private static List<OpenPosition> TheBook() =>
    [
        Leg(Long764, 30m, 6_070m, 6_090m),
        Leg(Short769, -20m, -1_110m, -1_060m),
        Leg(Short774, -10m, -80m, -90m),
    ];

    private static readonly Dictionary<string, DateTimeOffset> NoFills = [];
    private static readonly DateTimeOffset Opened = new(2026, 9, 1, 13, 41, 0, TimeSpan.Zero);

    private static IReadOnlyList<SpreadPosition> Rebuilt() =>
        SpreadReconstruction.FromLegs(TheBook(), NoFills, Opened);

    // ---- reconstruction ----

    [Fact]
    public void Three_legs_rebuild_into_two_structures_not_one()
    {
        IReadOnlyList<SpreadPosition> s = Rebuilt();

        Assert.Equal(2, s.Count);
        Assert.Contains(s, x => x.Spread.ShortSymbol == Short769 && x.Contracts == 20);
        Assert.Contains(s, x => x.Spread.ShortSymbol == Short774 && x.Contracts == 10);
    }

    [Fact]
    public void Each_structure_sits_under_the_three_percent_per_position_ceiling()
    {
        const decimal equity = 100_000m;

        IReadOnlyList<SpreadPosition> s = Rebuilt();

        SpreadPosition narrow = s.Single(x => x.Spread.ShortSymbol == Short769);
        SpreadPosition wide = s.Single(x => x.Spread.ShortSymbol == Short774);

        decimal narrowPct = narrow.Spread.MaxLoss(narrow.Contracts) / equity * 100m;
        decimal widePct = wide.Spread.MaxLoss(wide.Contracts) / equity * 100m;

        Assert.Equal(2.94m, Math.Round(narrowPct, 2));
        Assert.Equal(1.94m, Math.Round(widePct, 2));

        Assert.True(narrowPct < 3m);
        Assert.True(widePct < 3m);
    }

    [Fact]
    public void The_structures_sum_to_the_brokers_cost_basis()
    {
        decimal total = Rebuilt().Sum(s => s.Spread.MaxLoss(s.Contracts));

        // 6,070 - 1,110 - 80
        Assert.Equal(4_880m, Math.Round(total, 2));
    }

    [Fact]
    public void The_widths_are_the_real_widths()
    {
        IReadOnlyList<SpreadPosition> s = Rebuilt();

        Assert.Equal(5m, s.Single(x => x.Spread.ShortSymbol == Short769).Spread.StrikeWidth);
        Assert.Equal(10m, s.Single(x => x.Spread.ShortSymbol == Short774).Spread.StrikeWidth);
    }

    // ---- conservation ----

    /// <summary>
    /// The invariant that should have caught the original defect. An $80 discrepancy between
    /// reported maximum loss and the account's basis sat silent because nothing compared them.
    /// </summary>
    [Fact]
    public void A_leg_that_cannot_be_paired_stops_the_reconstruction()
    {
        // A short strike below every long: no vertical can be formed from it.
        List<OpenPosition> orphaned =
        [
            Leg(Long764, 10m, 2_020m, 2_030m),
            Leg("SPY260903C00760000", -10m, -300m, -310m),
        ];

        LegConservationException ex = Assert.Throws<LegConservationException>(
            () => SpreadReconstruction.FromLegs(orphaned, NoFills, Opened));

        Assert.Contains("SPY260903C00760000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("contract(s) paired", ex.Message, StringComparison.Ordinal);
    }

    // No test for the basis arm in isolation: reconstructed basis is derived from the same
    // leg figures it is compared against, so it can only diverge when contracts are
    // mis-allocated -- which the contract arm already catches, and which was the real defect
    // (a dropped leg showed as 4,960 against a reported 4,880). The positive assertion in
    // The_structures_sum_to_the_brokers_cost_basis is the meaningful form.

    [Fact]
    public void The_real_book_conserves_and_does_not_throw()
    {
        Exception? ex = Record.Exception(() => Rebuilt());
        Assert.Null(ex);
    }

    // ---- the orders a close actually emits ----

    /// <summary>
    /// Aggregates the legs of every closing order the agent would send, per symbol.
    /// Asserting on this rather than on a reconstructed spread is the point: a well-formed
    /// Spread object next to a malformed order is exactly the gap that let ten execution
    /// tests pass beside a path that never recorded anything.
    /// </summary>
    private static Dictionary<string, (PositionIntent Intent, int Qty)> ClosingOrders(
        IEnumerable<SpreadPosition> closing)
    {
        Dictionary<string, (PositionIntent, int)> byLeg = [];

        foreach (SpreadPosition p in closing)
        {
            foreach (SpreadLeg leg in p.Spread.ToClosingLegs())
            {
                (PositionIntent intent, int qty) = byLeg.TryGetValue(leg.Symbol, out var prior)
                    ? prior
                    : (leg.Intent, 0);

                Assert.Equal(intent, leg.Intent);       // a symbol cannot be both bought and sold
                byLeg[leg.Symbol] = (leg.Intent, qty + p.Contracts);
            }
        }

        return byLeg;
    }

    [Fact]
    public void Closing_the_whole_book_emits_one_order_per_leg_matching_the_position()
    {
        var orders = ClosingOrders(Rebuilt());

        Assert.Equal(3, orders.Count);
        Assert.Equal((PositionIntent.SellToClose, 30), orders[Long764]);
        Assert.Equal((PositionIntent.BuyToClose, 20), orders[Short769]);
        Assert.Equal((PositionIntent.BuyToClose, 10), orders[Short774]);
    }

    [Fact]
    public void No_closing_order_exceeds_the_position_it_closes()
    {
        var orders = ClosingOrders(Rebuilt());

        foreach (OpenPosition leg in TheBook())
        {
            Assert.True(orders.ContainsKey(leg.Symbol), $"{leg.Symbol} would be left unclosed");
            Assert.Equal((int)Math.Abs(leg.Quantity), orders[leg.Symbol].Qty);
        }
    }

    /// <summary>
    /// Pin risk is the path that actually fires for this book: the contracts expire on the
    /// scored day, which the calendar permits holding, so the flatten stage stands down and
    /// the settlement hazard decides instead. SPY was 761.99 against a 764 strike.
    /// </summary>
    [Fact]
    public void At_expiry_inside_the_pin_zone_every_structure_is_closed()
    {
        DateTimeOffset thursday = new(2026, 9, 3, 18, 0, 0, TimeSpan.Zero);
        ExitPolicy policy = new();
        CompetitionCalendar calendar = new();

        List<SpreadPosition> closing = [];

        foreach (SpreadPosition p in Rebuilt())
        {
            ExitDecision d = ExitLadder.Evaluate(p, policy, underlyingPrice: 764.20m, now: thursday, calendar);
            Assert.Equal(ExitReason.PinRisk, d.Reason);
            closing.Add(p);
        }

        var orders = ClosingOrders(closing);
        Assert.Equal((PositionIntent.SellToClose, 30), orders[Long764]);
        Assert.Equal((PositionIntent.BuyToClose, 20), orders[Short769]);
        Assert.Equal((PositionIntent.BuyToClose, 10), orders[Short774]);
    }

    /// <summary>
    /// The same book at an expiry the calendar does not permit holding: the flatten stage
    /// fires, and the orders it produces must be equally well formed.
    /// </summary>
    [Fact]
    public void Competition_flatten_closes_every_structure_without_overshooting()
    {
        List<OpenPosition> laterExpiry = TheBook()
            .Select(l => Leg(l.Symbol.Replace("260903", "260918"), l.Quantity, l.CostBasis, l.MarketValue))
            .ToList();

        IReadOnlyList<SpreadPosition> rebuilt =
            SpreadReconstruction.FromLegs(laterExpiry, NoFills, Opened);

        Assert.Equal(2, rebuilt.Count);

        DateTimeOffset afterClose = new(2026, 9, 3, 20, 30, 0, TimeSpan.Zero);
        List<SpreadPosition> closing = [];

        foreach (SpreadPosition p in rebuilt)
        {
            ExitDecision d = ExitLadder.Evaluate(p, new ExitPolicy(), 761.99m, afterClose, new CompetitionCalendar());
            Assert.Equal(ExitReason.CompetitionFlatten, d.Reason);
            closing.Add(p);
        }

        var orders = ClosingOrders(closing);
        Assert.Equal(30, orders["SPY260918C00764000"].Qty);
        Assert.Equal(20, orders["SPY260918C00769000"].Qty);
        Assert.Equal(10, orders["SPY260918C00774000"].Qty);
    }

    /// <summary>The close goes out as multi-leg orders, never as single-leg closes.</summary>
    [Fact]
    public void Each_structure_closes_atomically()
    {
        foreach (SpreadPosition p in Rebuilt())
        {
            string json = AlpacaCli.BuildLegsJson(p.Spread.ToClosingLegs());

            Assert.Contains("sell_to_close", json, StringComparison.Ordinal);
            Assert.Contains("buy_to_close", json, StringComparison.Ordinal);
            Assert.Equal(2, p.Spread.ToClosingLegs().Count);
        }
    }
}
