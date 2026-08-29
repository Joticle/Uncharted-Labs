using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class SpreadSelectorTests
{
    private static OptionContract Call(decimal strike, decimal delta, decimal bid, decimal ask) => new()
    {
        Symbol = $"SPY260918C{strike * 1000m:00000000}",
        Strike = strike,
        Expiration = new DateOnly(2026, 9, 18),
        Type = OptionType.Call,
        Delta = delta,
        Bid = bid,
        Ask = ask,
        BidSize = 50,
        AskSize = 50,
    };

    /// <summary>
    /// A liquid chain shaped like the observed SPY Sep-18 series: only the 776 sits in the
    /// delta band, and premium decays across strikes at a realistic rate. Ratios come out at
    /// 1.43 / 1.98 / 2.53 for the three widths, matching live data.
    /// </summary>
    private static List<OptionContract> LiquidChain() =>
    [
        Call(776m, 0.40m, 16.80m, 17.00m),
        Call(781m, 0.34m, 14.94m, 15.14m),
        Call(786m, 0.28m, 13.64m, 13.84m),
        Call(791m, 0.23m, 12.75m, 12.95m),
    ];

    [Fact]
    public void Best_reward_risk_policy_takes_the_widest_qualifying_width()
    {
        SpreadCandidate c = SpreadSelector.SelectBullCall(
            "SPY", LiquidChain(), new RiskMandate(), WidthPolicy.BestRewardRisk);

        Assert.True(c.Found);
        Assert.Equal(15m, c.Spread!.StrikeWidth);
        Assert.Equal(3, c.Evaluations.Count);
    }

    [Fact]
    public void The_default_policy_takes_the_narrowest_qualifying_width()
    {
        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", LiquidChain(), new RiskMandate());

        Assert.True(c.Found);
        Assert.Equal(10m, c.Spread!.StrikeWidth);
        Assert.True(c.Spread.RewardRiskRatio >= 1.5m);
    }

    [Fact]
    public void Every_width_is_evaluated_and_recorded_even_when_it_fails()
    {
        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", LiquidChain(), new RiskMandate());

        Assert.Equal([5m, 10m, 15m], c.Evaluations.Select(e => e.Width));

        WidthEvaluation five = c.Evaluations.Single(e => e.Width == 5m);
        Assert.Equal(SelectionFailure.RewardRiskBelowFloor, five.Outcome);
    }

    /// <summary>
    /// The guard that stops the width search buying ratio with bad fills. The 791 leg here
    /// passes the per-leg quote check at 9.4% of mid, so only the aggregate cost-drag gate
    /// catches it -- and it would otherwise have won on ratio at 2.09:1.
    /// </summary>
    [Fact]
    public void A_wide_short_leg_is_rejected_for_cost_drag_despite_the_best_headline_ratio()
    {
        List<OptionContract> chain =
        [
            Call(776m, 0.40m, 16.80m, 17.00m),
            Call(781m, 0.34m, 14.94m, 15.14m),
            Call(786m, 0.28m, 13.64m, 13.84m),
            Call(791m, 0.23m, 12.15m, 13.35m),   // 9.4% of mid: passes per-leg, fails aggregate
        ];

        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", chain, new RiskMandate());

        WidthEvaluation fifteen = c.Evaluations.Single(e => e.Width == 15m);
        Assert.Equal(SelectionFailure.CostDragTooHigh, fifteen.Outcome);

        // Rejected on drag despite a 2.09:1 headline ratio.
        Assert.True(c.Found);
        Assert.Equal(10m, c.Spread!.StrikeWidth);
    }

    [Fact]
    public void Cost_drag_is_measured_against_fair_value_not_the_crossed_price()
    {
        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", LiquidChain(), new RiskMandate());
        WidthEvaluation ten = c.Evaluations.Single(e => e.Width == 10m);

        // Mid to mid: 16.90 - 13.74 = 3.16. Crossed: 17.00 - 13.64 = 3.36.
        Assert.Equal(3.16m, ten.MidDebit);
        Assert.Equal(3.36m, ten.CrossedDebit);
        Assert.Equal(0.20m / 3.16m, ten.CostDrag);
    }

    [Fact]
    public void The_debit_is_priced_off_the_side_actually_crossed()
    {
        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", LiquidChain(), new RiskMandate());

        // The $10 width is the narrowest that qualifies. Long 776 paid at ask 17.00, short
        // 786 sold at bid 13.64 -> 3.36, not the 3.16 that mid-to-mid would suggest.
        Assert.Equal(3.36m, c.Spread!.NetDebit);
        Assert.NotEqual(3.16m, c.Spread.NetDebit);
    }

    [Fact]
    public void A_chain_with_nothing_in_the_delta_band_selects_nothing()
    {
        List<OptionContract> deepItm = [Call(700m, 0.95m, 70m, 70.20m), Call(705m, 0.94m, 65m, 65.20m)];

        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", deepItm, new RiskMandate());

        Assert.False(c.Found);
        Assert.Equal(SelectionFailure.NoContractsInDeltaBand, c.Failure);
    }

    [Fact]
    public void Contracts_without_greeks_are_excluded_rather_than_read_as_zero_delta()
    {
        List<OptionContract> chain =
        [
            Call(776m, 0m, 16.80m, 17.00m),
            Call(781m, 0m, 14.94m, 15.14m),
        ];

        SpreadCandidate c = SpreadSelector.SelectBullCall("SPY", chain, new RiskMandate());

        Assert.False(c.Found);
        Assert.Equal(SelectionFailure.NoContractsInDeltaBand, c.Failure);
    }
}
