using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class ExitLadderTests
{
    private static readonly ExitPolicy Policy = new();

    /// <summary>Long 772C / short 777C, $5 wide, $1.62 debit. Max profit $3.38 per share.</summary>
    private static VerticalSpread Spread(DateOnly? expiry = null) => new()
    {
        Underlying = "SPY",
        Direction = SpreadDirection.BullCall,
        LongSymbol = "SPY260903C00772000",
        ShortSymbol = "SPY260903C00777000",
        NetDebit = 1.62m,
        StrikeWidth = 5.00m,
        Expiration = expiry ?? new DateOnly(2026, 9, 3),
    };

    private static SpreadPosition Position(
        decimal currentValue, DateTimeOffset? opened = null, DateOnly? expiry = null) => new()
    {
        Spread = Spread(expiry),
        Contracts = 7,
        CurrentValue = currentValue,
        OpenedAt = opened ?? new DateTimeOffset(2026, 8, 31, 14, 0, 0, TimeSpan.Zero),
    };

    private static DateTimeOffset Utc(int m, int d, int h = 15) => new(2026, m, d, h, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_position_that_is_working_normally_is_held()
    {
        ExitDecision d = ExitLadder.Evaluate(Position(2.00m), Policy, 774m, Utc(9, 1));

        Assert.False(d.ShouldClose);
        Assert.Equal(ExitReason.None, d.Reason);
    }

    [Fact]
    public void The_stop_fires_at_half_the_debit()
    {
        // Debit 1.62; half of it lost puts the mark at 0.81.
        ExitDecision d = ExitLadder.Evaluate(Position(0.80m), Policy, 765m, Utc(9, 1));

        Assert.Equal(ExitReason.StopLoss, d.Reason);
    }

    [Fact]
    public void Take_profit_fires_at_the_configured_fraction_of_maximum()
    {
        // Max profit is 5.00 - 1.62 = 3.38. 65% of that is 2.197, so a mark of 1.62 + 2.20.
        ExitDecision d = ExitLadder.Evaluate(Position(3.83m), Policy, 780m, Utc(9, 1));

        Assert.Equal(ExitReason.TakeProfit, d.Reason);
    }

    [Fact]
    public void The_time_stop_frees_capital_that_is_not_working()
    {
        ExitDecision d = ExitLadder.Evaluate(
            Position(1.65m, opened: Utc(8, 24), expiry: new DateOnly(2026, 9, 18)),
            Policy, 770m, Utc(9, 1));

        Assert.Equal(ExitReason.TimeStop, d.Reason);
    }

    [Fact]
    public void A_position_that_is_working_survives_the_time_stop()
    {
        ExitDecision d = ExitLadder.Evaluate(
            Position(2.20m, opened: Utc(8, 24), expiry: new DateOnly(2026, 9, 18)),
            Policy, 776m, Utc(9, 1));

        Assert.NotEqual(ExitReason.TimeStop, d.Reason);
    }

    /// <summary>
    /// The trailing stop was removed rather than implemented, because it is the one stage
    /// that would require the agent to carry state between runs. This asserts the decision:
    /// every input the ladder reads is observable from the broker at the moment of the call,
    /// so two evaluations of the same situation cannot disagree.
    /// </summary>
    [Fact]
    public void The_ladder_is_a_pure_function_of_broker_observable_state()
    {
        Assert.DoesNotContain("Trailing", Enum.GetNames<ExitReason>());

        SpreadPosition p = Position(2.00m);

        ExitDecision first = ExitLadder.Evaluate(p, Policy, 774m, Utc(9, 1));
        ExitDecision second = ExitLadder.Evaluate(p, Policy, 774m, Utc(9, 1));

        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Explanation, second.Explanation);
    }

    // ---- Pin risk ----

    /// <summary>
    /// The failure this rule exists for. Between the strikes the long leg exercises and the
    /// short does not, leaving 700 shares of a $772 underlying against a $100k account.
    /// </summary>
    [Fact]
    public void A_position_pinned_between_its_strikes_at_expiry_is_closed()
    {
        ExitDecision d = ExitLadder.Evaluate(Position(2.50m), Policy, 774.50m, Utc(9, 2));

        Assert.Equal(ExitReason.PinRisk, d.Reason);
        Assert.Contains("pin zone", d.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(771.00)]   // just below the long strike, inside the buffer
    [InlineData(772.00)]   // at the long strike
    [InlineData(777.00)]   // at the short strike
    [InlineData(778.00)]   // just above the short strike, inside the buffer
    public void The_pin_zone_extends_a_buffer_beyond_both_strikes(double spot)
    {
        ExitDecision d = ExitLadder.Evaluate(Position(2.50m), Policy, (decimal)spot, Utc(9, 2));

        Assert.Equal(ExitReason.PinRisk, d.Reason);
    }

    [Theory]
    [InlineData(760.00)]   // clear below: everything expires worthless, max loss, clean
    [InlineData(790.00)]   // clear above: both legs exercise and offset, max profit, clean
    public void A_position_clear_of_both_strikes_settles_cleanly_and_is_not_forced_out(double spot)
    {
        ExitDecision d = ExitLadder.Evaluate(Position(1.70m), Policy, (decimal)spot, Utc(9, 2));

        Assert.NotEqual(ExitReason.PinRisk, d.Reason);
    }

    [Fact]
    public void Pin_risk_is_not_enforced_while_expiry_is_still_far_off()
    {
        ExitDecision d = ExitLadder.Evaluate(
            Position(2.50m, expiry: new DateOnly(2026, 9, 18)), Policy, 774.50m, Utc(9, 1));

        Assert.NotEqual(ExitReason.PinRisk, d.Reason);
    }

    // ---- Precedence ----

    [Fact]
    public void The_competition_obligation_outranks_a_profit_target()
    {
        // Expires later than the scored day, so it cannot be left to settle.
        ExitDecision d = ExitLadder.Evaluate(
            Position(3.83m, expiry: new DateOnly(2026, 9, 18)),
            Policy, 780m, Utc(9, 3, 20), new CompetitionCalendar());

        Assert.Equal(ExitReason.CompetitionFlatten, d.Reason);
    }

    [Fact]
    public void A_contract_expiring_on_the_scored_day_may_be_left_to_settle()
    {
        ExitDecision d = ExitLadder.Evaluate(
            Position(1.70m), Policy, 790m, Utc(9, 3, 20), new CompetitionCalendar());

        Assert.NotEqual(ExitReason.CompetitionFlatten, d.Reason);
    }

    [Fact]
    public void Pin_risk_outranks_the_stop_loss()
    {
        // Down past the stop and inside the pin zone: the settlement hazard is the reason
        // reported, because it is the one that determines how the position must be closed.
        ExitDecision d = ExitLadder.Evaluate(Position(0.70m), Policy, 773m, Utc(9, 2));

        Assert.Equal(ExitReason.PinRisk, d.Reason);
    }
}
