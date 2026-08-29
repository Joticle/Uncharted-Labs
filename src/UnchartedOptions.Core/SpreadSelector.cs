namespace UnchartedOptions.Core;

/// <summary>Why a candidate spread was not selected.</summary>
public enum SelectionFailure
{
    None,
    NoContractsInDeltaBand,
    NoShortLegAtWidth,
    LegsTooIlliquid,
    CostDragTooHigh,
    RewardRiskBelowFloor,
    DebitExceedsWidth,
}

/// <summary>How to choose among widths that all clear every gate.</summary>
public enum WidthPolicy
{
    /// <summary>Take the highest reward:risk. Favours wider spreads.</summary>
    BestRewardRisk,

    /// <summary>
    /// Take the narrowest width that qualifies. Clearing the floor is the bar; exceeding it
    /// buys ratio at the cost of probability, since a wider spread's short leg is further
    /// out of the money and correspondingly less likely to be reached.
    /// </summary>
    NarrowestQualifying,
}

/// <summary>One width evaluated, whether or not it survived.</summary>
public sealed record WidthEvaluation
{
    public required decimal Width { get; init; }

    public required SelectionFailure Outcome { get; init; }

    public required string Detail { get; init; }

    public VerticalSpread? Spread { get; init; }

    /// <summary>Fair debit, mid to mid.</summary>
    public decimal MidDebit { get; init; }

    /// <summary>Debit paid crossing both legs: pay the ask, receive the bid.</summary>
    public decimal CrossedDebit { get; init; }

    /// <summary>Crossing cost as a fraction of the fair debit.</summary>
    public decimal CostDrag { get; init; }

    public bool Qualified => Outcome == SelectionFailure.None;
}

public sealed record SpreadCandidate
{
    public VerticalSpread? Spread { get; init; }

    public SelectionFailure Failure { get; init; }

    public required string Reasoning { get; init; }

    /// <summary>Every width considered, for the audit trail.</summary>
    public IReadOnlyList<WidthEvaluation> Evaluations { get; init; } = [];

    public bool Found => Spread is not null;
}

/// <summary>
/// Turns a raw option chain into a single defined-risk vertical, or explains why it cannot.
/// </summary>
/// <remarks>
/// Each filter answers a documented retail failure mode rather than a preference: the delta
/// band targets illiquid strike selection, the cost-drag gate targets execution cost, and
/// pricing off the crossed side of the quote keeps the modelled debit honest.
/// </remarks>
public static class SpreadSelector
{
    public static SpreadCandidate SelectBullCall(
        string underlying,
        IReadOnlyList<OptionContract> chain,
        RiskMandate mandate,
        WidthPolicy policy = WidthPolicy.NarrowestQualifying)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(mandate);

        List<OptionContract> calls = chain
            .Where(c => c.Type == OptionType.Call && c.HasGreeks && c.HasTwoSidedQuote)
            .OrderBy(c => c.Strike)
            .ToList();

        // Delta 0 means greeks are absent, already excluded by HasGreeks -- so a missing
        // value can never be mistaken for a genuine low-delta strike.
        List<OptionContract> inBand = calls
            .Where(c => c.Delta >= mandate.MinLongLegDelta && c.Delta <= mandate.MaxLongLegDelta)
            .ToList();

        if (inBand.Count == 0)
        {
            return new SpreadCandidate
            {
                Failure = SelectionFailure.NoContractsInDeltaBand,
                Reasoning = $"No call in the {mandate.MinLongLegDelta:F2}-{mandate.MaxLongLegDelta:F2} "
                          + $"delta band among {calls.Count} quoted contracts.",
            };
        }

        decimal bandCentre = (mandate.MinLongLegDelta + mandate.MaxLongLegDelta) / 2m;
        OptionContract longLeg = inBand.OrderBy(c => Math.Abs(c.Delta - bandCentre)).First();

        if (longLeg.RelativeSpread > mandate.MaxRelativeSpread)
        {
            return new SpreadCandidate
            {
                Failure = SelectionFailure.LegsTooIlliquid,
                Reasoning = $"Long leg {longLeg.Strike:F0}C quote is {longLeg.RelativeSpread:P1} wide, "
                          + $"over the {mandate.MaxRelativeSpread:P0} cap.",
            };
        }

        List<WidthEvaluation> evaluations = mandate.CandidateWidths
            .Select(w => Evaluate(underlying, calls, longLeg, w, mandate))
            .ToList();

        List<WidthEvaluation> qualified = evaluations.Where(e => e.Qualified).ToList();

        if (qualified.Count == 0)
        {
            WidthEvaluation closest = evaluations
                .OrderBy(e => e.Outcome == SelectionFailure.RewardRiskBelowFloor ? 0 : 1)
                .First();

            return new SpreadCandidate
            {
                Failure = closest.Outcome,
                Evaluations = evaluations,
                Reasoning = $"No width qualified off the {longLeg.Strike:F0}C (delta {longLeg.Delta:F2}). "
                          + string.Join("  |  ", evaluations.Select(e => $"${e.Width:F0}: {e.Detail}")),
            };
        }

        WidthEvaluation chosen = policy == WidthPolicy.NarrowestQualifying
            ? qualified.OrderBy(e => e.Width).First()
            : qualified.OrderByDescending(e => e.Spread!.RewardRiskRatio).ThenBy(e => e.Width).First();

        VerticalSpread spread = chosen.Spread!;

        string alternatives = qualified.Count > 1
            ? "  Also qualified: " + string.Join(", ", qualified
                .Where(e => e.Width != chosen.Width)
                .Select(e => $"${e.Width:F0} at {e.Spread!.RewardRiskRatio:F2}:1"))
            : string.Empty;

        return new SpreadCandidate
        {
            Spread = spread,
            Failure = SelectionFailure.None,
            Evaluations = evaluations,
            Reasoning =
                $"Long {longLeg.Strike:F0}C (delta {longLeg.Delta:F2}) / short "
                + $"{longLeg.Strike + chosen.Width:F0}C, {Money.Usd(spread.NetDebit)} debit on a "
                + $"${chosen.Width:F0} width, {spread.RewardRiskRatio:F2}:1, "
                + $"cost drag {chosen.CostDrag:P1}. Max loss "
                + $"{Money.Usd(spread.MaxLossPerContract)} per contract." + alternatives,
        };
    }

    private static WidthEvaluation Evaluate(
        string underlying,
        List<OptionContract> calls,
        OptionContract longLeg,
        decimal width,
        RiskMandate mandate)
    {
        OptionContract? shortLeg = calls.FirstOrDefault(c => c.Strike == longLeg.Strike + width);

        if (shortLeg is null)
        {
            return Fail(width, SelectionFailure.NoShortLegAtWidth,
                $"no quoted call at {longLeg.Strike + width:F0}");
        }

        if (!shortLeg.HasTwoSidedQuote)
        {
            return Fail(width, SelectionFailure.LegsTooIlliquid,
                $"{shortLeg.Strike:F0}C has no two-sided quote");
        }

        if (shortLeg.RelativeSpread > mandate.MaxRelativeSpread)
        {
            return Fail(width, SelectionFailure.LegsTooIlliquid,
                $"{shortLeg.Strike:F0}C quote {shortLeg.RelativeSpread:P1} wide");
        }

        // Fair value mid to mid, against what we would actually pay crossing both legs.
        decimal midDebit = longLeg.Mid - shortLeg.Mid;
        decimal crossedDebit = longLeg.Ask - shortLeg.Bid;

        if (midDebit <= 0m || crossedDebit <= 0m || crossedDebit >= width)
        {
            return Fail(width, SelectionFailure.DebitExceedsWidth,
                $"debit {Money.Usd(crossedDebit)} is not a defined-risk structure at ${width:F0}");
        }

        decimal costDrag = (crossedDebit - midDebit) / midDebit;

        if (costDrag > mandate.MaxCostDrag)
        {
            return Fail(width, SelectionFailure.CostDragTooHigh,
                $"crossing costs {costDrag:P1} of fair value, over the {mandate.MaxCostDrag:P0} cap");
        }

        VerticalSpread spread = new()
        {
            Underlying = underlying,
            Direction = SpreadDirection.BullCall,
            LongSymbol = longLeg.Symbol,
            ShortSymbol = shortLeg.Symbol,
            NetDebit = crossedDebit,
            StrikeWidth = width,
            Expiration = longLeg.Expiration,
        };

        if (spread.RewardRiskRatio < mandate.MinRewardRiskRatio)
        {
            return Fail(width, SelectionFailure.RewardRiskBelowFloor,
                $"{spread.RewardRiskRatio:F2}:1 below the {mandate.MinRewardRiskRatio:F1}:1 floor");
        }

        return new WidthEvaluation
        {
            Width = width,
            Outcome = SelectionFailure.None,
            Detail = $"{spread.RewardRiskRatio:F2}:1, drag {costDrag:P1}",
            Spread = spread,
            MidDebit = midDebit,
            CrossedDebit = crossedDebit,
            CostDrag = costDrag,
        };

        static WidthEvaluation Fail(decimal width, SelectionFailure outcome, string detail) =>
            new() { Width = width, Outcome = outcome, Detail = detail };
    }
}
