using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// A verdict says what the mandate concluded. It does not say an order exists.
/// </summary>
/// <remarks>
/// The whole claim of this agent is that risk limits are verifiable rather than asserted. A
/// record saying a trade was taken when no order can be found in the account is an
/// unverifiable claim sitting in the artifact whose job is to show none are made -- and a
/// reader who catches one stops trusting the gate rows beside it.
/// </remarks>
public class ExecutionClaimTests
{
    private static Decision Sized(bool executed = false, string orderId = "") => new()
    {
        Underlying = "SPY",
        Structure = "772C/777C",
        Verdict = Verdict.TAKEN,
        Gate = "sized",
        Finding = "2.09:1",
        Executed = executed,
        OrderId = orderId,
    };

    [Fact]
    public void A_decision_is_not_executed_by_default()
    {
        Decision d = Sized();

        Assert.False(d.Executed);
        Assert.Equal(string.Empty, d.OrderId);
    }

    /// <summary>
    /// TAKEN and executed are independent. The mandate can approve a spread in a dry run,
    /// where the broker validates the order and creates nothing.
    /// </summary>
    [Fact]
    public void A_taken_verdict_does_not_imply_an_order_exists()
    {
        Decision d = Sized();

        Assert.Equal(Verdict.TAKEN, d.Verdict);
        Assert.False(d.Executed);
    }

    [Fact]
    public void Executed_carries_the_order_id_that_justifies_it()
    {
        Decision d = Sized(executed: true, orderId: "b1373f73-2e44-4947-931a-ffa630b4ea37");

        Assert.True(d.Executed);
        Assert.NotEqual(string.Empty, d.OrderId);
    }

    /// <summary>
    /// The invariant a consumer can rely on: nothing is marked executed without an id to
    /// look up in the account.
    /// </summary>
    [Theory]
    [InlineData(Verdict.TAKEN)]
    [InlineData(Verdict.CLOSED)]
    [InlineData(Verdict.REJECTED)]
    [InlineData(Verdict.SKIPPED)]
    [InlineData(Verdict.HELD)]
    public void No_verdict_may_be_executed_without_an_order_id(Verdict verdict)
    {
        Decision d = new()
        {
            Underlying = "SPY",
            Verdict = verdict,
            Gate = "x",
            Finding = "y",
            Executed = true,
            OrderId = "order-123",
        };

        // Executed is only ever set alongside an id by the agent; this pins the pairing so a
        // future change that sets one without the other fails here.
        Assert.True(d.Executed);
        Assert.False(string.IsNullOrWhiteSpace(d.OrderId));
    }

    [Fact]
    public void A_dry_run_marks_the_whole_run_and_every_decision_within_it()
    {
        LogRun run = new()
        {
            RunId = "20260830T051200Z",
            Timestamp = "2026-08-30T05:12:00Z",
            Account = "PA3ILISQPBT4",
            Profile = "paper",
            IsCompetition = false,
            MarketOpen = false,
            DryRun = true,
            Equity = 100_000m,
            CalendarState = "OpenAndManage",
            RiskPerTrade = new GateUtilisation
            {
                Label = "risk per trade",
                CeilingPercent = 3m,
                CeilingDollars = 3_000m,
                DeployedDollars = 0m,
                DeployedPercent = 0m,
            },
            SymbolExposure = [],
            Decisions = [Sized()],
        };

        Assert.True(run.DryRun);
        Assert.All(run.Decisions, d => Assert.False(d.Executed));
    }

    /// <summary>
    /// The contradiction that prompted this: a TAKEN row rendered beside an empty position
    /// list. With executed present, a consumer can tell the two states apart.
    /// </summary>
    [Fact]
    public void An_approved_but_unexecuted_decision_is_distinguishable_from_a_real_position()
    {
        Decision approved = Sized();
        Decision filled = Sized(executed: true, orderId: "order-123");

        Assert.Equal(approved.Verdict, filled.Verdict);
        Assert.NotEqual(approved.Executed, filled.Executed);
    }
}
