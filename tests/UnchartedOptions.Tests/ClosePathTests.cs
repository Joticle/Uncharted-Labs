using UnchartedOptions.Alpaca;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class ClosePathTests
{
    private static VerticalSpread Spread => new()
    {
        Underlying = "SPY",
        Direction = SpreadDirection.BullCall,
        LongSymbol = "SPY260903C00772000",
        ShortSymbol = "SPY260903C00777000",
        NetDebit = 1.62m,
        StrikeWidth = 5.00m,
        Expiration = new DateOnly(2026, 9, 3),
    };

    /// <summary>
    /// Pins the closing wire format, verified against the live broker with --dry-run.
    /// </summary>
    [Fact]
    public void Closing_legs_reverse_both_the_side_and_the_intent()
    {
        string json = AlpacaCli.BuildLegsJson(Spread.ToClosingLegs());

        Assert.Equal(
            "[{\"symbol\":\"SPY260903C00772000\",\"side\":\"sell\",\"ratio_qty\":\"1\",\"position_intent\":\"sell_to_close\"}," +
            "{\"symbol\":\"SPY260903C00777000\",\"side\":\"buy\",\"ratio_qty\":\"1\",\"position_intent\":\"buy_to_close\"}]",
            json);
    }

    /// <summary>
    /// The long leg is sold and the short bought back in one order. Doing this as two
    /// single-leg closes would leave a naked short call if only the first filled.
    /// </summary>
    [Fact]
    public void A_close_is_one_order_covering_both_legs()
    {
        IReadOnlyList<SpreadLeg> legs = Spread.ToClosingLegs();

        Assert.Equal(2, legs.Count);
        Assert.Equal(LegSide.Sell, legs[0].Side);
        Assert.Equal(PositionIntent.SellToClose, legs[0].Intent);
        Assert.Equal(LegSide.Buy, legs[1].Side);
        Assert.Equal(PositionIntent.BuyToClose, legs[1].Intent);

        // The legs closed must be exactly the legs opened.
        Assert.Equal(Spread.ToLegs().Select(l => l.Symbol), legs.Select(l => l.Symbol));
    }

    [Fact]
    public async Task Closing_a_non_positive_quantity_is_refused_before_any_process_starts()
    {
        AlpacaCli cli = new(new CliRunner(executable: "definitely-not-a-real-binary"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cli.CloseSpreadAsync(Spread, contracts: 0, limitPrice: 2.00m, dryRun: true));
    }
}

public class SpreadReconstructionTests
{
    private static OpenPosition Leg(string symbol, decimal qty, decimal costBasis, decimal marketValue) => new()
    {
        Symbol = symbol,
        Underlying = OccSymbol.Underlying(symbol) ?? symbol,
        IsOption = true,
        Quantity = qty,
        CostBasis = costBasis,
        MarketValue = marketValue,
        UnrealizedPl = marketValue - costBasis,
    };

    private static readonly DateTimeOffset Opened = new(2026, 8, 31, 14, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, DateTimeOffset> NoFills = [];

    [Fact]
    public void Two_legs_rebuild_into_one_spread_with_the_debit_and_mark_netted()
    {
        // 7 contracts: paid 1.62, now marked at 2.40.
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260903C00777000", -7m, -7_266m, -8_820m),
        ];

        IReadOnlyList<SpreadPosition> spreads = SpreadReconstruction.FromLegs(legs, NoFills, Opened);

        SpreadPosition s = Assert.Single(spreads);
        Assert.Equal(7, s.Contracts);
        Assert.Equal(1.62m, s.Spread.NetDebit);
        Assert.Equal(2.40m, s.CurrentValue);
        Assert.Equal(5.00m, s.Spread.StrikeWidth);
        Assert.Equal("SPY", s.Spread.Underlying);
    }

    [Fact]
    public void An_unmatched_long_leg_is_left_alone_rather_than_treated_as_a_spread()
    {
        List<OpenPosition> legs = [Leg("SPY260903C00772000", 7m, 8_400m, 10_500m)];

        Assert.Empty(SpreadReconstruction.FromLegs(legs, NoFills, Opened));
    }

    [Fact]
    public void Legs_of_different_expiries_are_not_paired_together()
    {
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260918C00777000", -7m, -7_266m, -8_820m),
        ];

        Assert.Empty(SpreadReconstruction.FromLegs(legs, NoFills, Opened));
    }

    [Fact]
    public void Separate_underlyings_rebuild_into_separate_spreads()
    {
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260903C00777000", -7m, -7_266m, -8_820m),
            Leg("QQQ260903C00600000", 3m, 3_000m, 3_300m),
            Leg("QQQ260903C00605000", -3m, -2_400m, -2_550m),
        ];

        IReadOnlyList<SpreadPosition> spreads = SpreadReconstruction.FromLegs(legs, NoFills, Opened);

        Assert.Equal(2, spreads.Count);
        Assert.Contains(spreads, s => s.Spread.Underlying == "SPY");
        Assert.Contains(spreads, s => s.Spread.Underlying == "QQQ");
    }

    /// <summary>
    /// Entry time comes from the broker's fill record, not from a caller's guess. The time
    /// stop depends on it, so a fabricated value would make that stage fire against a number
    /// nothing in the system actually knows.
    /// </summary>
    [Fact]
    public void Open_time_is_taken_from_the_brokers_fill_record()
    {
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260903C00777000", -7m, -7_266m, -8_820m),
        ];

        DateTimeOffset realFill = new(2026, 8, 31, 13, 45, 0, TimeSpan.Zero);
        Dictionary<string, DateTimeOffset> fills = new()
        {
            ["SPY260903C00772000"] = realFill,
            ["SPY260903C00777000"] = realFill.AddSeconds(2),
        };

        SpreadPosition s = SpreadReconstruction.FromLegs(legs, fills, Opened).Single();

        // The earlier of the two legs, not the fallback.
        Assert.Equal(realFill, s.OpenedAt);
        Assert.NotEqual(Opened, s.OpenedAt);
    }

    [Fact]
    public void The_fallback_is_used_only_when_the_broker_has_no_fill_record()
    {
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260903C00777000", -7m, -7_266m, -8_820m),
        ];

        SpreadPosition s = SpreadReconstruction.FromLegs(legs, NoFills, Opened).Single();

        Assert.Equal(Opened, s.OpenedAt);
    }

    /// <summary>The reconstructed position must drive the ladder end to end.</summary>
    [Fact]
    public void A_rebuilt_spread_feeds_the_ladder_and_triggers_pin_risk()
    {
        List<OpenPosition> legs =
        [
            Leg("SPY260903C00772000", 7m, 8_400m, 10_500m),
            Leg("SPY260903C00777000", -7m, -7_266m, -8_820m),
        ];

        SpreadPosition s = SpreadReconstruction.FromLegs(legs, NoFills, Opened).Single();

        ExitDecision d = ExitLadder.Evaluate(
            s, new ExitPolicy(), underlyingPrice: 774.50m,
            now: new DateTimeOffset(2026, 9, 2, 19, 0, 0, TimeSpan.Zero));

        Assert.Equal(ExitReason.PinRisk, d.Reason);
    }
}
