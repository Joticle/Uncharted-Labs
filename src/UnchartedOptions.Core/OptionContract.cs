namespace UnchartedOptions.Core;

public enum OptionType
{
    Call,
    Put,
}

/// <summary>A single option contract with the quote and greeks needed to select it.</summary>
public sealed record OptionContract
{
    public required string Symbol { get; init; }

    public required decimal Strike { get; init; }

    public required DateOnly Expiration { get; init; }

    public required OptionType Type { get; init; }

    /// <summary>
    /// Alpaca reports 0 when greeks are not computed (deep ITM, illiquid, or near expiry).
    /// Treat 0 as absent rather than as a real delta.
    /// </summary>
    public required decimal Delta { get; init; }

    public required decimal Bid { get; init; }

    public required decimal Ask { get; init; }

    public required int BidSize { get; init; }

    public required int AskSize { get; init; }

    public bool HasGreeks => Delta != 0m;

    public bool HasTwoSidedQuote => Bid > 0m && Ask > 0m && Ask >= Bid;

    public decimal Mid => HasTwoSidedQuote ? (Bid + Ask) / 2m : 0m;

    public decimal SpreadWidth => HasTwoSidedQuote ? Ask - Bid : decimal.MaxValue;

    /// <summary>
    /// Bid-ask spread as a fraction of mid. The cost-drag proxy: the Florida study attributes
    /// a large share of retail multi-leg losses to crossing wide spreads on illiquid strikes.
    /// </summary>
    public decimal RelativeSpread => Mid <= 0m ? decimal.MaxValue : SpreadWidth / Mid;
}
