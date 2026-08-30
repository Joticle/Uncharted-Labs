namespace UnchartedOptions.Core;

/// <summary>Which side of an individual option leg.</summary>
public enum LegSide
{
    Buy,
    Sell,
}

/// <summary>
/// Alpaca <c>position_intent</c>. Required on every leg of a multi-leg order.
/// </summary>
public enum PositionIntent
{
    BuyToOpen,
    SellToOpen,
    BuyToClose,
    SellToClose,
}

/// <summary>
/// Structure of a defined-risk vertical.
/// </summary>
/// <remarks>
/// One member, deliberately. A bear put variant would be a straightforward addition, but
/// nothing constructs one today and an enum member the selector can never produce advertises
/// a capability that does not exist. It gets added when the code to build it does.
/// </remarks>
public enum SpreadDirection
{
    /// <summary>Bull call debit spread. Long the lower strike, short the higher.</summary>
    BullCall,
}

/// <summary>One leg of a multi-leg order, in the exact shape Alpaca's CLI expects.</summary>
public sealed record SpreadLeg
{
    /// <summary>OCC option symbol, e.g. <c>SPY260826C00500000</c>.</summary>
    public required string Symbol { get; init; }

    public required LegSide Side { get; init; }

    /// <summary>Leg ratio. Always 1 for the verticals Uncharted Options trades.</summary>
    public required int RatioQty { get; init; }

    public required PositionIntent Intent { get; init; }
}

/// <summary>
/// A defined-risk vertical spread: the instrument that carries Uncharted Options' risk limit.
/// </summary>
/// <remarks>
/// <para>
/// This type is the whole thesis in one place. A stop-loss is an instruction the broker
/// may or may not be able to honour -- it gaps through on a bad open, and it does not
/// exist at all if the agent that was supposed to place it has crashed. A debit vertical
/// has no such dependency: the most it can lose is the debit paid, that figure is fixed
/// at the moment the order is constructed, and it is enforced by the broker's position
/// accounting rather than by this agent's control flow.
/// </para>
/// <para>
/// The practical consequence for sizing is that <see cref="MaxLoss"/> is exact rather than
/// estimated. A share-based sizer must approximate risk as <c>entry - stop</c> and then
/// hope the stop fills near that price. Here the denominator is known with certainty
/// before the order is sent, so the 3% gate is a fact about the position rather than a
/// hope about execution.
/// </para>
/// </remarks>
public sealed record VerticalSpread
{
    /// <summary>Underlying ticker, e.g. <c>SPY</c>.</summary>
    public required string Underlying { get; init; }

    public required SpreadDirection Direction { get; init; }

    /// <summary>The leg being bought to open.</summary>
    public required string LongSymbol { get; init; }

    /// <summary>The leg being sold to open, which caps both the cost and the payoff.</summary>
    public required string ShortSymbol { get; init; }

    /// <summary>Net debit per spread, quoted per share.</summary>
    public required decimal NetDebit { get; init; }

    /// <summary>Absolute distance between the two strikes, in dollars.</summary>
    public required decimal StrikeWidth { get; init; }

    public required DateOnly Expiration { get; init; }

    /// <summary>Standard US equity option multiplier.</summary>
    public const int ContractMultiplier = 100;

    /// <summary>
    /// Maximum loss for a single spread. Fixed at construction and enforced by the broker.
    /// </summary>
    public decimal MaxLossPerContract => NetDebit * ContractMultiplier;

    /// <summary>
    /// Maximum profit for a single spread: the strike width less the debit paid.
    /// </summary>
    public decimal MaxProfitPerContract => (StrikeWidth - NetDebit) * ContractMultiplier;

    /// <summary>
    /// Reward-to-risk ratio at maximum profit. Compared against the entry floor.
    /// </summary>
    public decimal RewardRiskRatio =>
        MaxLossPerContract <= 0m ? 0m : MaxProfitPerContract / MaxLossPerContract;

    /// <summary>
    /// Total capital genuinely at risk for <paramref name="contracts"/> spreads.
    /// </summary>
    public decimal MaxLoss(int contracts) => MaxLossPerContract * contracts;

    /// <summary>
    /// The legs that unwind this spread: each side reversed, each intent a close.
    /// </summary>
    /// <remarks>
    /// Closing must go out as one multi-leg order, never as two single-leg closes. If the
    /// long leg filled and the short did not, what remains is a naked short call -- an
    /// unbounded liability created at the exact moment the position was supposed to be
    /// wound down safely. The atomicity that makes the risk defined on entry is what makes
    /// it defined on exit.
    /// </remarks>
    public IReadOnlyList<SpreadLeg> ToClosingLegs() =>
    [
        new SpreadLeg
        {
            Symbol = LongSymbol,
            Side = LegSide.Sell,
            RatioQty = 1,
            Intent = PositionIntent.SellToClose,
        },
        new SpreadLeg
        {
            Symbol = ShortSymbol,
            Side = LegSide.Buy,
            RatioQty = 1,
            Intent = PositionIntent.BuyToClose,
        },
    ];

    /// <summary>
    /// Renders the two legs in the order and shape Alpaca's <c>--legs</c> argument expects.
    /// </summary>
    public IReadOnlyList<SpreadLeg> ToLegs() =>
    [
        new SpreadLeg
        {
            Symbol = LongSymbol,
            Side = LegSide.Buy,
            RatioQty = 1,
            Intent = PositionIntent.BuyToOpen,
        },
        new SpreadLeg
        {
            Symbol = ShortSymbol,
            Side = LegSide.Sell,
            RatioQty = 1,
            Intent = PositionIntent.SellToOpen,
        },
    ];
}
