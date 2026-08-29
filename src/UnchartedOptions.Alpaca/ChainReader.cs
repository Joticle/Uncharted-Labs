using System.Globalization;
using System.Text.Json;
using UnchartedOptions.Core;

namespace UnchartedOptions.Alpaca;

/// <summary>Reads option chains with greeks and quotes via <c>alpaca data option chain</c>.</summary>
public sealed class ChainReader
{
    private readonly CliRunner _runner;

    public ChainReader(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    /// <summary>
    /// Fetches quoted contracts for one underlying and expiration.
    /// </summary>
    /// <remarks>
    /// Strike bounds matter for more than efficiency. An unbounded SPY chain is thousands of
    /// contracts, and the far tails are exactly the illiquid strikes the mandate rejects --
    /// bounding the request avoids paying to page through them.
    /// </remarks>
    public async Task<IReadOnlyList<OptionContract>> GetChainAsync(
        string underlying,
        DateOnly expiration,
        OptionType type,
        decimal strikeFrom,
        decimal strikeTo,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);

        string[] args =
        [
            "data", "option", "chain",
            "--underlying-symbol", underlying,
            "--expiration-date", expiration.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "--type", type == OptionType.Call ? "call" : "put",
            "--strike-price-gte", strikeFrom.ToString("0.##", CultureInfo.InvariantCulture),
            "--strike-price-lte", strikeTo.ToString("0.##", CultureInfo.InvariantCulture),
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
        ];

        using JsonDocument doc = await _runner.RunAsync(args, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("snapshots", out JsonElement snapshots)
            || snapshots.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<OptionContract> contracts = [];

        foreach (JsonProperty snapshot in snapshots.EnumerateObject())
        {
            OptionContract? parsed = Parse(snapshot.Name, snapshot.Value, expiration, type);
            if (parsed is not null)
            {
                contracts.Add(parsed);
            }
        }

        return contracts;
    }

    private static OptionContract? Parse(
        string symbol,
        JsonElement snapshot,
        DateOnly expiration,
        OptionType type)
    {
        decimal? strike = StrikeFromOccSymbol(symbol);
        if (strike is null)
        {
            return null;
        }

        decimal delta = 0m;
        if (snapshot.TryGetProperty("greeks", out JsonElement greeks)
            && greeks.ValueKind == JsonValueKind.Object
            && greeks.TryGetProperty("delta", out JsonElement deltaEl)
            && deltaEl.ValueKind == JsonValueKind.Number)
        {
            delta = deltaEl.GetDecimal();
        }

        decimal bid = 0m, ask = 0m;
        int bidSize = 0, askSize = 0;

        if (snapshot.TryGetProperty("latestQuote", out JsonElement quote)
            && quote.ValueKind == JsonValueKind.Object)
        {
            bid = Num(quote, "bp");
            ask = Num(quote, "ap");
            bidSize = (int)Num(quote, "bs");
            askSize = (int)Num(quote, "as");
        }

        return new OptionContract
        {
            Symbol = symbol,
            Strike = strike.Value,
            Expiration = expiration,
            Type = type,
            Delta = delta,
            Bid = bid,
            Ask = ask,
            BidSize = bidSize,
            AskSize = askSize,
        };
    }

    /// <summary>
    /// Extracts the strike from an OCC symbol: the last 8 digits are the strike in
    /// thousandths, e.g. <c>SPY260828C00500000</c> -&gt; 500.000.
    /// </summary>
    internal static decimal? StrikeFromOccSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length < 9)
        {
            return null;
        }

        ReadOnlySpan<char> tail = symbol.AsSpan(symbol.Length - 8);

        foreach (char c in tail)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return long.Parse(tail, CultureInfo.InvariantCulture) / 1000m;
    }

    private static decimal Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : 0m;
}
