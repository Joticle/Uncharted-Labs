using System.Globalization;
using System.Text.Json;
using UnchartedOptions.Core;

namespace UnchartedOptions.Alpaca;

/// <summary>
/// Reads ex-dividend dates from Alpaca's corporate-actions endpoint.
/// </summary>
/// <remarks>
/// The endpoint publishes cash dividends, splits, mergers and spin-offs. It does
/// <b>not</b> publish earnings dates, so nothing here can populate the earnings half of the
/// blackout calendar -- that comes from an explicit list. This reader covers the half Alpaca
/// can actually answer.
/// </remarks>
public sealed class CorporateActionsReader
{
    private readonly CliRunner _runner;

    public CorporateActionsReader(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    public async Task<IReadOnlyList<BlackoutEvent>> GetExDividendsAsync(
        IReadOnlyList<string> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        if (symbols.Count == 0)
        {
            return [];
        }

        string[] args =
        [
            "data", "corporate-actions",
            "--symbols", string.Join(',', symbols),
            "--start", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "--end", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ];

        using JsonDocument doc = await _runner.RunAsync(args, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("corporate_actions", out JsonElement actions)
            || actions.ValueKind != JsonValueKind.Object
            || !actions.TryGetProperty("cash_dividends", out JsonElement dividends)
            || dividends.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<BlackoutEvent> events = [];

        foreach (JsonElement d in dividends.EnumerateArray())
        {
            string? symbol = Str(d, "symbol");
            string? exDate = Str(d, "ex_date");

            if (string.IsNullOrWhiteSpace(symbol)
                || !DateOnly.TryParseExact(exDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly parsed))
            {
                continue;
            }

            events.Add(new BlackoutEvent
            {
                Underlying = symbol.ToUpperInvariant(),
                Date = parsed,
                Reason = BlackoutReason.ExDividend,
                Source = "alpaca corporate-actions",
            });
        }

        return events;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
