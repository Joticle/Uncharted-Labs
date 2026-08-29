namespace UnchartedOptions.Core;

/// <summary>Which gate bound the final size. Surfaced for the audit trail and the dashboard.</summary>
public enum LimitingFactor
{
    /// <summary>Nothing bound; the requested size was affordable and within every gate.</summary>
    None,

    /// <summary>The 3. Risk per trade capped the size.</summary>
    RiskPerTrade,

    /// <summary>The 5. Existing plus proposed exposure to this underlying capped the size.</summary>
    SymbolExposure,

    /// <summary>Options buying power could not cover the debit.</summary>
    Affordability,

    /// <summary>The 7. Reward-to-risk was too poor to justify the trade at any size.</summary>
    RewardRiskRatio,

    /// <summary>A single spread already breaches a gate. No tradeable size exists.</summary>
    BelowMinimumSize,

    /// <summary>The hard per-order contract ceiling bound, ahead of any percentage gate.</summary>
    OrderCeiling,
}

/// <summary>The mandate. Percentages are of <see cref="Account.Equity"/>.</summary>
public sealed record RiskMandate
{
    /// <summary>The 3. Maximum fraction of equity riskable on any single position.</summary>
    public decimal MaxRiskPerTradePct { get; init; } = 0.03m;

    /// <summary>The 5. Maximum fraction of equity exposed to any one underlying.</summary>
    public decimal MaxSymbolExposurePct { get; init; } = 0.05m;

    /// <summary>
    /// The 7. Minimum reward-to-risk ratio required to open at all.
    /// </summary>
    /// <remarks>
    /// Inherited from the predecessor as 7.0, which was wrong for this instrument. A debit
    /// vertical's payoff is structurally capped at <c>width - debit</c>, so 7:1 requires
    /// paying an eighth of the width -- a far-OTM, low-probability spread. That figure came
    /// from a share-based strategy with unbounded upside. The market research characterises
    /// this product as "a 1.5:1 payoff with a 3% cap", so 1.5 is the researched figure.
    /// </remarks>
    public decimal MinRewardRiskRatio { get; init; } = 1.5m;

    /// <summary>Hard ceiling on contracts per order, independent of the percentage gates.</summary>
    public int MaxContractsPerOrder { get; init; } = 10;

    /// <summary>Lower bound of the delta band for the long leg.</summary>
    /// <remarks>
    /// The 35-45 delta constraint keeps selection in the liquid part of the chain. Retail
    /// multi-leg losses concentrate in wide bid-ask on illiquid strikes; observed spreads
    /// bear this out -- $0.25 wide near the money against $8.77 on a deep-ITM strike.
    /// </remarks>
    public decimal MinLongLegDelta { get; init; } = 0.35m;

    /// <summary>Upper bound of the delta band for the long leg.</summary>
    public decimal MaxLongLegDelta { get; init; } = 0.45m;

    /// <summary>Reject any leg whose bid-ask spread exceeds this fraction of mid.</summary>
    /// <remarks>A per-leg sanity filter, which catches a single garbage quote.</remarks>
    public decimal MaxRelativeSpread { get; init; } = 0.10m;

    /// <summary>
    /// Maximum crossing cost, as a fraction of the spread's fair (mid-to-mid) debit.
    /// </summary>
    /// <remarks>
    /// The per-leg filter above is not sufficient once widths vary. Widening a spread pushes
    /// the short leg further out of the money, where quotes are typically wider -- so a naive
    /// search for a better reward:risk ratio systematically selects spreads that are more
    /// expensive to execute. This gate measures what actually matters: the gap between the
    /// mid-to-mid debit and the debit paid crossing both legs, relative to the debit itself.
    /// The 2025 University of Florida study attributes retail multi-leg losses largely to
    /// exactly this drag, so widening without measuring it would walk into the finding the
    /// strategy claims to avoid.
    /// </remarks>
    public decimal MaxCostDrag { get; init; } = 0.15m;

    /// <summary>Strike widths to evaluate, cheapest structure first.</summary>
    public IReadOnlyList<decimal> CandidateWidths { get; init; } = [5m, 10m, 15m];
}

/// <summary>Everything the sizer needs. No broker, no database, no clock.</summary>
public sealed record SizingRequest
{
    public required Account Account { get; init; }

    public required VerticalSpread Spread { get; init; }

    /// <summary>
    /// Capital already at risk in this underlying, from open positions.
    /// </summary>
    /// <remarks>
    /// Netting existing exposure off the budget is the one idea worth taking from the
    /// predecessor's orchestrator. Without it the same symbol is re-entered on every
    /// signal and concentration compounds silently.
    /// </remarks>
    public decimal ExistingSymbolExposure { get; init; }

    public RiskMandate Mandate { get; init; } = new();
}

/// <summary>The sizing decision, with its full reasoning attached.</summary>
public sealed record SizingResult
{
    public required int Contracts { get; init; }

    public required LimitingFactor LimitedBy { get; init; }

    /// <summary>Capital genuinely at risk if this order fills. Exact, not estimated.</summary>
    public required decimal CapitalAtRisk { get; init; }

    /// <summary>The 3, in dollars.</summary>
    public required decimal MaxRiskBudget { get; init; }

    /// <summary>The 5, in dollars, after netting existing exposure.</summary>
    public required decimal RemainingSymbolBudget { get; init; }

    public required string Explanation { get; init; }

    public bool ShouldTrade => Contracts > 0;
}

/// <summary>
/// The 3-5-7 gates, applied to defined-risk verticals.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic over an <see cref="Account"/> and a <see cref="VerticalSpread"/>. No
/// dependencies, no I/O, no clock -- which is what makes the mandate exhaustively testable.
/// </para>
/// <para>
/// Deliberately omitted: <c>FixedDollars</c> and <c>FixedShares</c> sizing. Both bypassed
/// the gates entirely in the predecessor and sized from config constants, which is a hole
/// straight through the mandate (H4). There is exactly one way to size a position here.
/// </para>
/// </remarks>
public static class PositionSizer
{
    public static SizingResult Size(SizingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Account account = request.Account;
        VerticalSpread spread = request.Spread;
        RiskMandate mandate = request.Mandate;

        // The 3 -- risk per trade, as a dollar budget off equity. Never off buying power.
        decimal maxRiskBudget = account.Equity * mandate.MaxRiskPerTradePct;

        // The 5 -- symbol exposure, netting what this underlying already costs us.
        decimal maxSymbolExposure = account.Equity * mandate.MaxSymbolExposurePct;
        decimal remainingSymbolBudget = Math.Max(0m, maxSymbolExposure - request.ExistingSymbolExposure);

        decimal riskPerContract = spread.MaxLossPerContract;

        if (riskPerContract <= 0m)
        {
            return Reject(
                LimitingFactor.BelowMinimumSize,
                maxRiskBudget,
                remainingSymbolBudget,
                "Spread has no defined risk (net debit is zero or negative); refusing to size it.");
        }

        // The 7 -- reward-to-risk. A structural property of the spread, so it gates
        // entry outright rather than scaling the size.
        if (spread.RewardRiskRatio < mandate.MinRewardRiskRatio)
        {
            return Reject(
                LimitingFactor.RewardRiskRatio,
                maxRiskBudget,
                remainingSymbolBudget,
                $"Reward:risk {spread.RewardRiskRatio:F2}:1 is below the {mandate.MinRewardRiskRatio:F1}:1 floor. Not worth the debit.");
        }

        int contractsFromRisk = (int)Math.Floor(maxRiskBudget / riskPerContract);
        int contractsFromExposure = (int)Math.Floor(remainingSymbolBudget / riskPerContract);

        // Affordability. Options buying power is unleveraged, so it is a safe ceiling --
        // but it is only ever a Math.Min, never the base of the calculation above.
        int contractsFromBuyingPower = (int)Math.Floor(account.OptionsBuyingPower / riskPerContract);

        int contracts = Math.Min(
            Math.Min(contractsFromRisk, contractsFromExposure),
            Math.Min(contractsFromBuyingPower, mandate.MaxContractsPerOrder));

        if (contracts <= 0)
        {
            LimitingFactor blocker =
                contractsFromExposure <= 0 ? LimitingFactor.SymbolExposure
                : contractsFromBuyingPower <= 0 ? LimitingFactor.Affordability
                : LimitingFactor.RiskPerTrade;

            return Reject(
                blocker,
                maxRiskBudget,
                remainingSymbolBudget,
                $"A single spread risks {Money.Usd(riskPerContract)}, which no gate can accommodate. Blocked by {Describe(blocker)}.");
        }

        // Order matters: report the tightest *percentage* gate first, and fall back to the
        // hard ceiling only when no mandate gate was what actually bound. Reporting "no gate"
        // when the ceiling bound would quietly misstate the audit trail.
        LimitingFactor limitedBy =
            contracts == contractsFromRisk ? LimitingFactor.RiskPerTrade
            : contracts == contractsFromExposure ? LimitingFactor.SymbolExposure
            : contracts == contractsFromBuyingPower ? LimitingFactor.Affordability
            : contracts == mandate.MaxContractsPerOrder ? LimitingFactor.OrderCeiling
            : LimitingFactor.None;

        decimal capitalAtRisk = spread.MaxLoss(contracts);

        return new SizingResult
        {
            Contracts = contracts,
            LimitedBy = limitedBy,
            CapitalAtRisk = capitalAtRisk,
            MaxRiskBudget = maxRiskBudget,
            RemainingSymbolBudget = remainingSymbolBudget,
            Explanation =
                $"{contracts} x {spread.Underlying} {spread.Direction} risking {Money.Usd(capitalAtRisk)} " +
                $"({Money.Percent(capitalAtRisk / account.Equity)} of {Money.Usd(account.Equity)} equity). " +
                $"Bound by {Describe(limitedBy)}. Max loss is fixed at construction.",
        };

        SizingResult Reject(LimitingFactor factor, decimal risk, decimal symbol, string why) => new()
        {
            Contracts = 0,
            LimitedBy = factor,
            CapitalAtRisk = 0m,
            MaxRiskBudget = risk,
            RemainingSymbolBudget = symbol,
            Explanation = why,
        };
    }

    private static string Describe(LimitingFactor factor) => factor switch
    {
        LimitingFactor.RiskPerTrade => "the 3 (risk per trade)",
        LimitingFactor.SymbolExposure => "the 5 (symbol exposure)",
        LimitingFactor.RewardRiskRatio => "the 7 (reward:risk)",
        LimitingFactor.Affordability => "options buying power",
        LimitingFactor.BelowMinimumSize => "minimum size",
        LimitingFactor.OrderCeiling => "the per-order contract ceiling",
        _ => "no gate",
    };
}
