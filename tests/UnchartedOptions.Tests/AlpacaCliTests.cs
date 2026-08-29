using UnchartedOptions.Alpaca;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class AlpacaCliTests
{
    private static VerticalSpread BullCall => new()
    {
        Underlying = "SPY",
        Direction = SpreadDirection.BullCall,
        LongSymbol = "SPY260826C00500000",
        ShortSymbol = "SPY260826C00505000",
        NetDebit = 1.00m,
        StrikeWidth = 5.00m,
        Expiration = new DateOnly(2026, 8, 26),
    };

    /// <summary>
    /// Pins the exact wire format verified against the live CLI with <c>--dry-run</c>.
    /// </summary>
    /// <remarks>
    /// If this string ever changes, the agent stops being able to place spreads. The order
    /// of legs matters too: long first, then short.
    /// </remarks>
    [Fact]
    public void Legs_serialise_to_the_verified_wire_format()
    {
        string json = AlpacaCli.BuildLegsJson(BullCall);

        Assert.Equal(
            "[{\"symbol\":\"SPY260826C00500000\",\"side\":\"buy\",\"ratio_qty\":\"1\",\"position_intent\":\"buy_to_open\"}," +
            "{\"symbol\":\"SPY260826C00505000\",\"side\":\"sell\",\"ratio_qty\":\"1\",\"position_intent\":\"sell_to_open\"}]",
            json);
    }

    [Fact]
    public void The_long_leg_is_bought_to_open_and_the_short_leg_sold_to_open()
    {
        IReadOnlyList<SpreadLeg> legs = BullCall.ToLegs();

        Assert.Equal(2, legs.Count);

        Assert.Equal(LegSide.Buy, legs[0].Side);
        Assert.Equal(PositionIntent.BuyToOpen, legs[0].Intent);
        Assert.Equal("SPY260826C00500000", legs[0].Symbol);

        Assert.Equal(LegSide.Sell, legs[1].Side);
        Assert.Equal(PositionIntent.SellToOpen, legs[1].Intent);
        Assert.Equal("SPY260826C00505000", legs[1].Symbol);

        Assert.All(legs, leg => Assert.Equal(1, leg.RatioQty));
    }

    [Fact]
    public async Task Submitting_a_non_positive_quantity_is_rejected_before_any_process_starts()
    {
        AlpacaCli cli = new(new CliRunner(executable: "definitely-not-a-real-binary"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cli.SubmitSpreadAsync(BullCall, contracts: 0, limitPrice: 1.00m, dryRun: true));
    }

    [Fact]
    public async Task A_missing_cli_binary_surfaces_as_a_typed_error()
    {
        AlpacaCli cli = new(new CliRunner(executable: "definitely-not-a-real-binary"));

        AlpacaCliException ex = await Assert.ThrowsAsync<AlpacaCliException>(
            () => cli.GetAccountAsync());

        Assert.Contains("Alpaca CLI", ex.Message, StringComparison.Ordinal);
    }
}
