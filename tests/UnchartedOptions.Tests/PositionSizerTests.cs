using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class PositionSizerTests
{
    /// <summary>A $100k account with 4x margin, exactly as Alpaca reports it.</summary>
    private static Account MarginAccount(decimal equity = 100_000m) => new()
    {
        AccountNumber = "PA3XXXXXXXXX",
        Equity = equity,
        // Unleveraged, as options always are. The 400k buying_power figure is deliberately
        // absent from the model -- see Account's remarks.
        OptionsBuyingPower = equity,
        OptionsTradingLevel = 3,
    };

    /// <summary>A $5-wide bull call for a $1.00 debit. Max loss $100, max profit $400, 4:1.</summary>
    private static VerticalSpread Spread(decimal debit = 1.00m, decimal width = 5.00m) => new()
    {
        Underlying = "SPY",
        Direction = SpreadDirection.BullCall,
        LongSymbol = "SPY260828C00500000",
        ShortSymbol = "SPY260828C00505000",
        NetDebit = debit,
        StrikeWidth = width,
        Expiration = new DateOnly(2026, 8, 28),
    };

    private static RiskMandate Permissive => new() { MinRewardRiskRatio = 1.0m, MaxContractsPerOrder = 1_000 };

    /// <summary>
    /// The regression test this whole codebase exists to pass.
    /// </summary>
    /// <remarks>
    /// Alpaca reports buying_power of 400,000 against equity of 100,000. If sizing ever
    /// reads the wrong field, risk quadruples silently and nothing throws. The 3% of equity
    /// is $3,000, which at $100 of risk per spread is 30 contracts. Sizing off buying power
    /// would give 120.
    /// </remarks>
    [Fact]
    public void A_4x_margin_account_still_sizes_off_equity()
    {
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(),
            Mandate = Permissive,
        });

        Assert.Equal(30, result.Contracts);
        Assert.Equal(3_000m, result.CapitalAtRisk);
        Assert.NotEqual(120, result.Contracts);
    }

    [Fact]
    public void The_3_caps_risk_at_three_percent_of_equity()
    {
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(),
            Mandate = Permissive,
        });

        Assert.Equal(LimitingFactor.RiskPerTrade, result.LimitedBy);
        Assert.Equal(3_000m, result.MaxRiskBudget);
        Assert.True(result.CapitalAtRisk <= result.MaxRiskBudget);
    }

    [Fact]
    public void The_5_binds_once_the_symbol_is_already_carrying_exposure()
    {
        // 5% of 100k is 5,000. With 4,800 already committed, only 200 remains -- 2 spreads.
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(),
            ExistingSymbolExposure = 4_800m,
            Mandate = Permissive,
        });

        Assert.Equal(LimitingFactor.SymbolExposure, result.LimitedBy);
        Assert.Equal(2, result.Contracts);
        Assert.Equal(200m, result.RemainingSymbolBudget);
    }

    [Fact]
    public void A_symbol_at_its_exposure_ceiling_is_refused_entirely()
    {
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(),
            ExistingSymbolExposure = 5_000m,
            Mandate = Permissive,
        });

        Assert.False(result.ShouldTrade);
        Assert.Equal(0, result.Contracts);
        Assert.Equal(LimitingFactor.SymbolExposure, result.LimitedBy);
    }

    [Fact]
    public void The_7_rejects_a_spread_whose_payoff_does_not_justify_the_debit()
    {
        // $4.00 debit on a $5.00 width is 0.25:1 -- the debit is not worth the payoff.
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(debit: 4.00m, width: 5.00m),
            Mandate = new RiskMandate { MinRewardRiskRatio = 7.0m },
        });

        Assert.False(result.ShouldTrade);
        Assert.Equal(LimitingFactor.RewardRiskRatio, result.LimitedBy);
    }

    /// <summary>
    /// Documents a live strategy risk rather than asserting desired behaviour.
    /// </summary>
    /// <remarks>
    /// A debit vertical's payoff is structurally capped at <c>width - debit</c>, so hitting
    /// 7:1 requires paying no more than one eighth of the width. That is a real, tradeable
    /// spread but a far-OTM and low-probability one. Inheriting 7.0 unexamined would have
    /// the agent decline nearly every candidate and finish the competition flat.
    /// </remarks>
    [Fact]
    public void A_seven_to_one_vertical_would_require_a_debit_of_one_eighth_the_width()
    {
        VerticalSpread cheap = Spread(debit: 0.625m, width: 5.00m);
        Assert.Equal(7.0m, cheap.RewardRiskRatio);

        VerticalSpread typical = Spread(debit: 2.00m, width: 5.00m);
        Assert.Equal(1.5m, typical.RewardRiskRatio);
    }

    [Fact]
    public void Max_loss_is_fixed_at_construction_and_scales_linearly()
    {
        VerticalSpread spread = Spread();

        Assert.Equal(100m, spread.MaxLossPerContract);
        Assert.Equal(400m, spread.MaxProfitPerContract);
        Assert.Equal(1_000m, spread.MaxLoss(10));
    }

    [Fact]
    public void The_hard_contract_ceiling_applies_even_when_every_gate_allows_more()
    {
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(),
            Mandate = new RiskMandate { MinRewardRiskRatio = 1.0m, MaxContractsPerOrder = 5 },
        });

        Assert.Equal(5, result.Contracts);
        Assert.Equal(LimitingFactor.OrderCeiling, result.LimitedBy);
    }

    [Fact]
    public void Money_renders_as_usd_regardless_of_ambient_culture()
    {
        // The build sets InvariantGlobalization, so the :C specifier would emit the
        // invariant currency sign. Money.Usd must not depend on that.
        Assert.Equal("$1,000.00", Money.Usd(1_000m));
        Assert.Equal("-$250.50", Money.Usd(-250.50m));
        Assert.Equal("3.00%", Money.Percent(0.03m));
    }

    [Fact]
    public void A_spread_with_no_defined_risk_is_refused()
    {
        SizingResult result = PositionSizer.Size(new SizingRequest
        {
            Account = MarginAccount(),
            Spread = Spread(debit: 0m),
            Mandate = Permissive,
        });

        Assert.False(result.ShouldTrade);
        Assert.Equal(LimitingFactor.BelowMinimumSize, result.LimitedBy);
    }
}
