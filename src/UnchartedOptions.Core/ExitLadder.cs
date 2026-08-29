namespace UnchartedOptions.Core;

/// <summary>Why a position was closed. Ordered by the precedence the ladder applies.</summary>
public enum ExitReason
{
    /// <summary>No stage fired. Hold.</summary>
    None,

    /// <summary>Contest calendar demands everything be flat.</summary>
    CompetitionFlatten,

    /// <summary>Underlying is near a strike into expiry; settlement outcome is unpredictable.</summary>
    PinRisk,

    /// <summary>Loss has reached the configured fraction of the debit.</summary>
    StopLoss,

    /// <summary>Target fraction of maximum profit reached.</summary>
    TakeProfit,

    /// <summary>Held too long without working.</summary>
    TimeStop,
}

/// <summary>An open defined-risk spread, marked to market.</summary>
public sealed record SpreadPosition
{
    public required VerticalSpread Spread { get; init; }

    public required int Contracts { get; init; }

    /// <summary>Current per-contract mark of the spread.</summary>
    public required decimal CurrentValue { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>Per-contract debit paid, which is also the maximum loss.</summary>
    public decimal DebitPaid => Spread.MaxLossPerContract / VerticalSpread.ContractMultiplier;

    /// <summary>Profit or loss per contract, in dollars.</summary>
    public decimal UnrealizedPerContract =>
        (CurrentValue - DebitPaid) * VerticalSpread.ContractMultiplier;

    /// <summary>Gain as a fraction of the debit risked. -1.0 is a total loss.</summary>
    public decimal ReturnOnRisk => DebitPaid <= 0m ? 0m : (CurrentValue - DebitPaid) / DebitPaid;

    /// <summary>How much of the theoretical maximum profit has been captured.</summary>
    public decimal FractionOfMaxProfit
    {
        get
        {
            decimal maxProfit = Spread.StrikeWidth - DebitPaid;
            return maxProfit <= 0m ? 0m : (CurrentValue - DebitPaid) / maxProfit;
        }
    }
}

public sealed record ExitPolicy
{
    /// <summary>Close once this fraction of the debit has been lost.</summary>
    public decimal StopLossFractionOfDebit { get; init; } = 0.50m;

    /// <summary>Close once this fraction of maximum profit is captured.</summary>
    public decimal TakeProfitFractionOfMax { get; init; } = 0.65m;

    /// <summary>Close a position that has not worked after this long.</summary>
    public int TimeStopDays { get; init; } = 5;

    /// <summary>Below this return, the time stop treats the position as not working.</summary>
    public decimal TimeStopMinReturn { get; init; } = 0.10m;

    /// <summary>
    /// Distance from either strike, in dollars, inside which expiry is treated as pin risk.
    /// </summary>
    public decimal PinRiskBuffer { get; init; } = 1.50m;

    /// <summary>Days before expiry at which pin risk starts being enforced.</summary>
    public int PinRiskDaysBeforeExpiry { get; init; } = 1;
}

public sealed record ExitDecision
{
    public required ExitReason Reason { get; init; }

    public required string Explanation { get; init; }

    public bool ShouldClose => Reason != ExitReason.None;
}

/// <summary>
/// The exit hierarchy, adapted from a share-based ladder to defined-risk spreads.
/// </summary>
/// <remarks>
/// <para>
/// Stages are evaluated in strict precedence and the first to fire wins, so a position
/// facing both an obligation to flatten and a profit target flattens.
/// </para>
/// <para>
/// The predecessor's first stage was a gap-risk hard exit -- an emergency check for an
/// overnight move blowing through a stop. It is deliberately absent here, because it is
/// exactly what the instrument already handles: a debit vertical cannot lose more than the
/// debit no matter how violently the underlying gaps, and no exit needs to fire for that
/// bound to hold. The remaining stages exist to do better than the bound, not to enforce it.
/// </para>
/// <para>
/// The predecessor's trailing stop is also absent, and its removal was a design decision
/// rather than an omission. A trailing stop needs a high-water mark carried between runs --
/// state the agent would own, that could be lost, corrupted, or drift out of agreement with
/// the broker. This agent's claim is that the risk limit does not live in software state,
/// and a single persisted file would be the one exception in a design asserting there are
/// none. The take-profit and stop-loss stages already bracket the outcome, so the stage was
/// removed rather than the claim weakened.
/// </para>
/// <para>
/// The predecessor's final stage was a MACD-based refinement. It is omitted until signal
/// generation exists, rather than stubbed to always-true.
/// </para>
/// </remarks>
public static class ExitLadder
{
    public static ExitDecision Evaluate(
        SpreadPosition position,
        ExitPolicy policy,
        decimal underlyingPrice,
        DateTimeOffset now,
        CompetitionCalendar? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(policy);

        // Stage 1 -- contest obligation. Overrides every trading consideration.
        if (calendar is not null)
        {
            TradingPermission permission = calendar.PermissionAt(now);

            if (permission is TradingPermission.FlattenAll or TradingPermission.Closed
                && !calendar.MayHoldToExpiry(position.Spread.Expiration))
            {
                return Close(ExitReason.CompetitionFlatten,
                    $"Competition requires flat by {calendar.FlatBy:yyyy-MM-dd HH:mm} UTC and this "
                    + $"position expires {position.Spread.Expiration:yyyy-MM-dd}.");
            }
        }

        // Stage 2 -- pin risk. Explicit, not emergent.
        //
        // A vertical held through expiry settles cleanly only when the underlying finishes
        // clear of both strikes: above both, everything exercises and offsets; below both,
        // everything expires worthless. Between them the long leg is exercised and the short
        // is not, leaving an unhedged stock position far larger than the account can carry.
        // Being near a strike at the bell is therefore not a payoff to optimise -- it is an
        // outcome to refuse.
        DateOnly expiry = position.Spread.Expiration;
        int daysToExpiry = expiry.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;

        if (daysToExpiry <= policy.PinRiskDaysBeforeExpiry && daysToExpiry >= 0)
        {
            decimal lower = LongStrike(position) - policy.PinRiskBuffer;
            decimal upper = ShortStrike(position) + policy.PinRiskBuffer;

            if (underlyingPrice >= lower && underlyingPrice <= upper)
            {
                return Close(ExitReason.PinRisk,
                    $"{position.Spread.Underlying} at {Money.Usd(underlyingPrice)} is inside the "
                    + $"{Money.Usd(lower)}-{Money.Usd(upper)} pin zone with {daysToExpiry}d to expiry. "
                    + "Closing rather than gambling on where it settles.");
            }
        }

        // Stage 3 -- stop loss, as a fraction of the debit.
        if (position.ReturnOnRisk <= -policy.StopLossFractionOfDebit)
        {
            return Close(ExitReason.StopLoss,
                $"Down {Money.Percent(-position.ReturnOnRisk)} of the debit, past the "
                + $"{Money.Percent(policy.StopLossFractionOfDebit)} stop.");
        }

        // Stage 4 -- take profit.
        if (position.FractionOfMaxProfit >= policy.TakeProfitFractionOfMax)
        {
            return Close(ExitReason.TakeProfit,
                $"Captured {Money.Percent(position.FractionOfMaxProfit)} of maximum profit, "
                + $"past the {Money.Percent(policy.TakeProfitFractionOfMax)} target.");
        }

        // Stage 5 -- time stop. Capital that is not working should be freed.
        int daysHeld = (int)(now - position.OpenedAt).TotalDays;

        if (daysHeld >= policy.TimeStopDays && position.ReturnOnRisk < policy.TimeStopMinReturn)
        {
            return Close(ExitReason.TimeStop,
                $"Held {daysHeld}d at {Money.Percent(position.ReturnOnRisk)} return, below the "
                + $"{Money.Percent(policy.TimeStopMinReturn)} the time stop requires.");
        }

        return new ExitDecision
        {
            Reason = ExitReason.None,
            Explanation =
                $"Hold. {Money.Percent(position.FractionOfMaxProfit)} of max profit, "
                + $"{Money.Percent(position.ReturnOnRisk)} on risk, {daysHeld}d held, "
                + $"{daysToExpiry}d to expiry.",
        };
    }

    private static decimal LongStrike(SpreadPosition p) =>
        OccSymbol.Strike(p.Spread.LongSymbol) ?? 0m;

    private static decimal ShortStrike(SpreadPosition p) =>
        OccSymbol.Strike(p.Spread.ShortSymbol) ?? decimal.MaxValue;

    private static ExitDecision Close(ExitReason reason, string why) =>
        new() { Reason = reason, Explanation = why };
}
