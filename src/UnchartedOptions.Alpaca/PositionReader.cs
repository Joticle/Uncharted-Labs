using System.Globalization;
using System.Text.Json;
using UnchartedOptions.Core;

namespace UnchartedOptions.Alpaca;

/// <summary>Reads open positions via <c>alpaca position list</c>.</summary>
public sealed class PositionReader
{
    private readonly CliRunner _runner;

    public PositionReader(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    public async Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        using JsonDocument doc = await _runner
            .RunAsync(["position", "list"], ct)
            .ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<OpenPosition> positions = [];

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string? symbol = Str(el, "symbol");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            bool isOption = string.Equals(Str(el, "asset_class"), "us_option", StringComparison.OrdinalIgnoreCase);

            // For options the underlying comes from the OCC symbol; for equities the symbol
            // is the underlying. An option symbol that will not parse is skipped rather than
            // silently attributed to the wrong underlying, which would corrupt the 5 gate.
            string? underlying = isOption ? OccSymbol.Underlying(symbol) : symbol;
            if (string.IsNullOrWhiteSpace(underlying))
            {
                continue;
            }

            positions.Add(new OpenPosition
            {
                Symbol = symbol,
                Underlying = underlying,
                IsOption = isOption,
                Quantity = Dec(el, "qty"),
                CostBasis = Dec(el, "cost_basis"),
                MarketValue = Dec(el, "market_value"),
                UnrealizedPl = Dec(el, "unrealized_pl"),
            });
        }

        return positions;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Alpaca returns every numeric position field as a JSON string.</summary>
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
