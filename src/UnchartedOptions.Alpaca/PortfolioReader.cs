using System.Text.Json;

namespace UnchartedOptions.Alpaca;

/// <summary>Reads account equity history for the dashboard's curve.</summary>
public sealed class PortfolioReader
{
    private readonly CliRunner _runner;

    public PortfolioReader(CliRunner? runner = null) => _runner = runner ?? new CliRunner();

    /// <summary>
    /// Equity values oldest first. Returns an empty list rather than throwing when the
    /// account has no history: a brand-new account legitimately has none, and the dashboard
    /// renders that as a flat inception line rather than an error.
    /// </summary>
    public async Task<IReadOnlyList<decimal>> GetEquityCurveAsync(
        string period = "1W", string timeframe = "1D", CancellationToken ct = default)
    {
        try
        {
            using JsonDocument doc = await _runner
                .RunAsync(["account", "portfolio", "--period", period, "--timeframe", timeframe], ct)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("equity", out JsonElement equity)
                || equity.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return equity.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => Math.Round(e.GetDecimal(), 2))
                .ToList();
        }
        catch (AlpacaCliException)
        {
            return [];
        }
    }
}
