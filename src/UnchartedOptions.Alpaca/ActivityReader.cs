using System.Globalization;
using System.Text.Json;
using UnchartedOptions.Core;

namespace UnchartedOptions.Alpaca;

/// <summary>Reads execution fills, the only record of what a position actually cost.</summary>
/// <remarks>
/// Alpaca does not publish realised profit per trade. It publishes fills, and realised profit
/// is what falls out of pairing them -- which is why this exists rather than a P&amp;L endpoint.
/// </remarks>
public sealed class ActivityReader
{
    private readonly CliRunner _runner;

    public ActivityReader(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    /// <summary>Alpaca rejects a page size above 100 with a 422.</summary>
    private const int MaxPageSize = 100;

    /// <summary>
    /// Every fill, following pagination to the end.
    /// </summary>
    /// <remarks>
    /// The endpoint caps a page at 100 entries, so a single request would silently truncate
    /// once the account has traded more than that -- and a truncated fill history reports
    /// closed positions as still open, because their closing fills fall off the end. Paging
    /// continues until a short page arrives, using the last activity id as the cursor.
    /// </remarks>
    public async Task<IReadOnlyList<Fill>> GetFillsAsync(CancellationToken ct = default)
    {
        List<Fill> fills = [];
        string? cursor = null;

        // A page cannot exceed 100, so this bounds the walk at 10,000 fills -- far beyond
        // anything this agent produces, while still refusing to loop forever.
        for (int page = 0; page < 100; page++)
        {
            List<string> args =
            [
                "account", "activity", "list",
                "--activity-types", "FILL",
                "--page-size", MaxPageSize.ToString(CultureInfo.InvariantCulture),
            ];

            if (cursor is not null)
            {
                args.Add("--page-token");
                args.Add(cursor);
            }

            using JsonDocument doc = await _runner.RunAsync(args, ct).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int count = 0;
            string? lastId = null;

            foreach (JsonElement a in doc.RootElement.EnumerateArray())
            {
                count++;
                lastId = Str(a, "id") ?? lastId;
                Add(fills, a);
            }

            if (count < MaxPageSize || lastId is null)
            {
                break;
            }

            cursor = lastId;
        }

        return fills;
    }

    private static void Add(List<Fill> fills, JsonElement a)
    {
        {
            string? symbol = Str(a, "symbol");
            string? side = Str(a, "side");

            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(side))
            {
                return;
            }

            fills.Add(new Fill
            {
                Symbol = symbol,
                IsBuy = side.StartsWith("buy", StringComparison.OrdinalIgnoreCase),
                Quantity = Dec(a, "qty"),
                Price = Dec(a, "price"),
                At = DateTimeOffset.TryParse(Str(a, "transaction_time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : DateTimeOffset.MinValue,
            });
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Dec(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v))
        {
            return 0m;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) => d,
            _ => 0m,
        };
    }
}
