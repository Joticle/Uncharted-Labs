using System.Globalization;
using System.Text;
using System.Text.Json;
using UnchartedOptions.Core;

namespace UnchartedOptions.Alpaca;

/// <summary>Result of submitting a spread. In dry-run mode this is the validated order body.</summary>
public sealed record OrderSubmission
{
    public required bool WasDryRun { get; init; }

    /// <summary>Broker order id. Null on a dry run, since nothing was created.</summary>
    public string? OrderId { get; init; }

    /// <summary>The order body as the broker echoed it back.</summary>
    public required string RawJson { get; init; }
}

/// <summary>Current market clock, straight from the broker rather than a local calendar.</summary>
public sealed record MarketClock
{
    public required bool IsOpen { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required DateTimeOffset NextOpen { get; init; }

    public required DateTimeOffset NextClose { get; init; }
}

/// <summary>
/// Typed adapter over the Alpaca CLI (<c>v0.0.13</c>). Process plumbing lives in
/// <see cref="CliRunner"/>.
/// </summary>
public sealed class AlpacaCli
{
    private readonly CliRunner _runner;

    public AlpacaCli(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    /// <summary>Reads account state, exposing only equity and options buying power.</summary>
    public async Task<Account> GetAccountAsync(CancellationToken ct = default)
    {
        using JsonDocument doc = await _runner.RunAsync(["account", "get"], ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        return new Account
        {
            AccountNumber = ReadString(root, "account_number") ?? "unknown",
            Equity = ReadDecimal(root, "equity"),
            OptionsBuyingPower = ReadDecimal(root, "options_buying_power"),
            OptionsTradingLevel = ReadOptionsLevel(root),
        };
    }

    /// <summary>Reads the broker's market clock. Authoritative on holidays and half-days.</summary>
    public async Task<MarketClock> GetClockAsync(CancellationToken ct = default)
    {
        using JsonDocument doc = await _runner.RunAsync(["clock"], ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        return new MarketClock
        {
            IsOpen = root.TryGetProperty("is_open", out JsonElement open) && open.ValueKind == JsonValueKind.True,
            Timestamp = ReadTimestamp(root, "timestamp"),
            NextOpen = ReadTimestamp(root, "next_open"),
            NextClose = ReadTimestamp(root, "next_close"),
        };
    }

    /// <summary>Mid price of the underlying, used to bound the strike search.</summary>
    public async Task<decimal> GetUnderlyingMidAsync(string symbol, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        using JsonDocument doc = await _runner
            // --feed iex, explicitly. A paper account has no SIP entitlement: asking for it
            // returns "subscription does not permit querying recent SIP data" outright. The
            // server default resolves to iex today, but relying on that makes the underlying
            // price a function of Alpaca's default rather than of what this account may read.
            .RunAsync(["data", "latest-quote", "--symbol", symbol, "--feed", "iex"], ct)
            .ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("quote", out JsonElement quote))
        {
            throw new AlpacaCliException($"No quote returned for {symbol}.");
        }

        decimal bid = ReadDecimal(quote, "bp");
        decimal ask = ReadDecimal(quote, "ap");

        if (bid <= 0m || ask <= 0m)
        {
            throw new AlpacaCliException($"{symbol} has no two-sided quote (bid {bid}, ask {ask}).");
        }

        return (bid + ask) / 2m;
    }

    /// <summary>
    /// Submits a defined-risk vertical as a single multi-leg order.
    /// </summary>
    /// <param name="spread">The spread to open.</param>
    /// <param name="contracts">Quantity, as decided by <see cref="PositionSizer"/>.</param>
    /// <param name="limitPrice">Net debit to pay. Never submit a spread at market.</param>
    /// <param name="dryRun">
    /// When true the broker validates and echoes the order without creating it. This is the
    /// integration test: the full order path can be exercised against a live account
    /// without placing a trade.
    /// </param>
    public async Task<OrderSubmission> SubmitSpreadAsync(
        VerticalSpread spread,
        int contracts,
        decimal limitPrice,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spread);
        ArgumentOutOfRangeException.ThrowIfLessThan(contracts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limitPrice, 0m);

        string legsJson = BuildLegsJson(spread.ToLegs());

        List<string> args =
        [
            "order", "submit",
            "--order-class", "mleg",
            "--qty", contracts.ToString(CultureInfo.InvariantCulture),
            "--type", "limit",
            "--limit-price", limitPrice.ToString("0.00", CultureInfo.InvariantCulture),
            "--time-in-force", "day",
            "--legs", legsJson,
        ];

        if (dryRun)
        {
            args.Add("--dry-run");
        }

        using JsonDocument doc = await _runner.RunAsync(args, ct).ConfigureAwait(false);

        return new OrderSubmission
        {
            WasDryRun = dryRun,
            OrderId = dryRun ? null : ReadString(doc.RootElement, "id"),
            RawJson = doc.RootElement.GetRawText(),
        };
    }

    /// <summary>
    /// Fill time per option symbol, from the broker's own order history.
    /// </summary>
    /// <remarks>
    /// The time stop needs to know when a position was actually established. Alpaca does not
    /// report an entry time on the position itself, but it does on the order that created
    /// it -- so the fact is recovered from the broker rather than remembered by the agent.
    /// <c>--nested</c> rolls multi-leg orders up so each leg's own fill is visible.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetFillTimesAsync(
        CancellationToken ct = default)
    {
        using JsonDocument doc = await _runner
            .RunAsync(["order", "list", "--status", "closed", "--nested", "--limit", "500"], ct)
            .ConfigureAwait(false);

        Dictionary<string, DateTimeOffset> fills = new(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return fills;
        }

        foreach (JsonElement order in doc.RootElement.EnumerateArray())
        {
            Record(order, fills);

            if (order.TryGetProperty("legs", out JsonElement legs) && legs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement leg in legs.EnumerateArray())
                {
                    Record(leg, fills);
                }
            }
        }

        return fills;

        static void Record(JsonElement el, Dictionary<string, DateTimeOffset> into)
        {
            string? symbol = ReadString(el, "symbol");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            DateTimeOffset filled = ReadTimestamp(el, "filled_at");
            if (filled == DateTimeOffset.MinValue)
            {
                return;
            }

            // Keep the earliest fill: a symbol re-entered later was still first held then.
            if (!into.TryGetValue(symbol, out DateTimeOffset existing) || filled < existing)
            {
                into[symbol] = filled;
            }
        }
    }

    /// <summary>
    /// Unwinds a spread as a single multi-leg order.
    /// </summary>
    /// <param name="limitPrice">
    /// Credit to receive for closing. Never close a spread at market: both legs would cross
    /// their own quote, and on a short-dated series that is where the drag lives.
    /// </param>
    /// <remarks>
    /// Deliberately not implemented as two <c>position close</c> calls. That command takes a
    /// single symbol, so unwinding leg by leg leaves a window in which the long leg has been
    /// sold and the short has not -- a naked short call. One order, or none.
    /// </remarks>
    public async Task<OrderSubmission> CloseSpreadAsync(
        VerticalSpread spread,
        int contracts,
        decimal limitPrice,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spread);
        ArgumentOutOfRangeException.ThrowIfLessThan(contracts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limitPrice, 0m);

        List<string> args =
        [
            "order", "submit",
            "--order-class", "mleg",
            "--qty", contracts.ToString(CultureInfo.InvariantCulture),
            "--type", "limit",
            "--limit-price", limitPrice.ToString("0.00", CultureInfo.InvariantCulture),
            "--time-in-force", "day",
            "--legs", BuildLegsJson(spread.ToClosingLegs()),
        ];

        if (dryRun)
        {
            args.Add("--dry-run");
        }

        using JsonDocument doc = await _runner.RunAsync(args, ct).ConfigureAwait(false);

        return new OrderSubmission
        {
            WasDryRun = dryRun,
            OrderId = dryRun ? null : ReadString(doc.RootElement, "id"),
            RawJson = doc.RootElement.GetRawText(),
        };
    }

    /// <summary>
    /// Serialises the legs exactly as the CLI's <c>--legs</c> flag expects.
    /// </summary>
    /// <remarks>
    /// Verified shape: <c>symbol</c>, <c>side</c>, <c>ratio_qty</c>, <c>position_intent</c>.
    /// There is no top-level <c>--symbol</c> or <c>--side</c> for an <c>mleg</c> order --
    /// direction lives entirely in the legs.
    /// </remarks>
    internal static string BuildLegsJson(VerticalSpread spread) => BuildLegsJson(spread.ToLegs());

    internal static string BuildLegsJson(IReadOnlyList<SpreadLeg> legs)
    {
        StringBuilder sb = new();
        sb.Append('[');

        for (int i = 0; i < legs.Count; i++)
        {
            SpreadLeg leg = legs[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"symbol\":\"").Append(leg.Symbol)
              .Append("\",\"side\":\"").Append(leg.Side == LegSide.Buy ? "buy" : "sell")
              .Append("\",\"ratio_qty\":\"").Append(leg.RatioQty.ToString(CultureInfo.InvariantCulture))
              .Append("\",\"position_intent\":\"").Append(ToWireIntent(leg.Intent))
              .Append("\"}");
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string ToWireIntent(PositionIntent intent) => intent switch
    {
        PositionIntent.BuyToOpen => "buy_to_open",
        PositionIntent.SellToOpen => "sell_to_open",
        PositionIntent.BuyToClose => "buy_to_close",
        PositionIntent.SellToClose => "sell_to_close",
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unmapped position intent."),
    };

    /// <summary>Alpaca returns the level as a quoted string, e.g. "3".</summary>
    private static int ReadOptionsLevel(JsonElement root)
    {
        if (!root.TryGetProperty("options_trading_level", out JsonElement el))
        {
            return 0;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt32(),
            JsonValueKind.String when int.TryParse(
                el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lvl) => lvl,
            _ => 0,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static decimal ReadDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            throw new AlpacaCliException($"Expected field '{name}' was absent from the CLI response.");
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => throw new AlpacaCliException($"Field '{name}' was not a number: {el.ValueKind}."),
        };
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el)
        && el.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset ts)
            ? ts
            : DateTimeOffset.MinValue;
}
