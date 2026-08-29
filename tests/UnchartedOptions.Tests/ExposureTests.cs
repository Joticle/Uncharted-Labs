using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class OccSymbolTests
{
    [Theory]
    [InlineData("SPY260918C00778000", "SPY", 778.0, 2026, 9, 18, OptionType.Call)]
    [InlineData("SPY260903P00750000", "SPY", 750.0, 2026, 9, 3, OptionType.Put)]
    [InlineData("AAPL261016C00250500", "AAPL", 250.5, 2026, 10, 16, OptionType.Call)]
    public void Occ_symbols_decompose_correctly(
        string symbol, string underlying, double strike, int y, int m, int d, OptionType type)
    {
        Assert.Equal(underlying, OccSymbol.Underlying(symbol));
        Assert.Equal((decimal)strike, OccSymbol.Strike(symbol));
        Assert.Equal(new DateOnly(y, m, d), OccSymbol.Expiration(symbol));
        Assert.Equal(type, OccSymbol.Type(symbol));
    }

    [Theory]
    [InlineData("SPY")]
    [InlineData("")]
    [InlineData("SPY260918X00778000")]   // bad type character
    [InlineData("SPY2609L8C00778000")]   // non-digit in the date
    public void Malformed_symbols_are_rejected_rather_than_guessed(string symbol)
    {
        Assert.False(OccSymbol.IsWellFormed(symbol));
        Assert.Null(OccSymbol.Underlying(symbol));
    }
}

public class PortfolioExposureTests
{
    private static OpenPosition Leg(string symbol, decimal qty, decimal costBasis) => new()
    {
        Symbol = symbol,
        Underlying = OccSymbol.Underlying(symbol) ?? symbol,
        IsOption = OccSymbol.IsWellFormed(symbol),
        Quantity = qty,
        CostBasis = costBasis,
        MarketValue = costBasis,
        UnrealizedPl = 0m,
    };

    /// <summary>
    /// The reason this class exists. A debit spread reports as two positions; only their
    /// sum is the capital actually at risk.
    /// </summary>
    [Fact]
    public void A_debit_spread_nets_to_the_debit_paid_not_the_long_leg_cost()
    {
        List<OpenPosition> spread =
        [
            Leg("SPY260918C00778000", 10m, 8_500m),    // long leg paid
            Leg("SPY260918C00783000", -10m, -6_710m),  // short leg received
        ];

        // Net debit 1,790 -- which for a defined-risk vertical is exactly the max loss.
        Assert.Equal(1_790m, PortfolioExposure.ForUnderlying(spread, "SPY"));

        // Counting the long leg alone would overstate exposure by nearly 5x.
        Assert.NotEqual(8_500m, PortfolioExposure.ForUnderlying(spread, "SPY"));
    }

    [Fact]
    public void Exposure_is_tracked_per_underlying()
    {
        List<OpenPosition> positions =
        [
            Leg("SPY260918C00778000", 10m, 8_500m),
            Leg("SPY260918C00783000", -10m, -6_710m),
            Leg("AAPL261016C00250000", 5m, 3_000m),
            Leg("AAPL261016C00255000", -5m, -2_100m),
        ];

        Assert.Equal(1_790m, PortfolioExposure.ForUnderlying(positions, "SPY"));
        Assert.Equal(900m, PortfolioExposure.ForUnderlying(positions, "AAPL"));
        Assert.Equal(2_690m, PortfolioExposure.Total(positions));
        Assert.Equal(["AAPL", "SPY"], PortfolioExposure.Underlyings(positions));
    }

    [Fact]
    public void An_underlying_with_no_position_has_no_exposure()
    {
        Assert.Equal(0m, PortfolioExposure.ForUnderlying([], "SPY"));
    }

    [Fact]
    public void A_net_credit_is_not_treated_as_negative_risk()
    {
        List<OpenPosition> credit = [Leg("SPY260918C00778000", -10m, -500m)];

        Assert.Equal(0m, PortfolioExposure.ForUnderlying(credit, "SPY"));
    }

    /// <summary>
    /// The gate is only real once it is fed. This asserts the wiring, not the arithmetic:
    /// an underlying at its ceiling must refuse a further position.
    /// </summary>
    [Fact]
    public void Live_exposure_makes_the_5_gate_bind()
    {
        Account account = new()
        {
            AccountNumber = "TEST",
            Equity = 100_000m,
            OptionsBuyingPower = 100_000m,
            OptionsTradingLevel = 3,
        };

        VerticalSpread spread = new()
        {
            Underlying = "SPY",
            Direction = SpreadDirection.BullCall,
            LongSymbol = "SPY260918C00778000",
            ShortSymbol = "SPY260918C00783000",
            NetDebit = 1.79m,
            StrikeWidth = 5.00m,
            Expiration = new DateOnly(2026, 9, 18),
        };

        // 5% of 100k is 5,000, and SPY already carries 4,900.
        List<OpenPosition> held =
        [
            Leg("SPY260918C00778000", 30m, 25_000m),
            Leg("SPY260918C00783000", -30m, -20_100m),
        ];

        decimal exposure = PortfolioExposure.ForUnderlying(held, "SPY");
        Assert.Equal(4_900m, exposure);

        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = account,
            Spread = spread,
            ExistingSymbolExposure = exposure,
            Mandate = new RiskMandate(),
        });

        Assert.Equal(LimitingFactor.SymbolExposure, result.LimitedBy);
        Assert.Equal(0, result.Contracts);
    }
}
