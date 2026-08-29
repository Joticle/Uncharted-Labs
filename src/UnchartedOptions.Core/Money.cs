using System.Globalization;

namespace UnchartedOptions.Core;

/// <summary>Explicit USD formatting.</summary>
/// <remarks>
/// The build sets <c>InvariantGlobalization</c>, so the standard <c>:C</c> specifier renders
/// the invariant currency sign <c>&#164;</c> rather than a dollar sign. Relying on ambient
/// culture to format money is the wrong default for a trading agent regardless -- the same
/// binary runs on a developer laptop and a Linux CI runner, and the numbers must read
/// identically in both. Money is therefore always formatted explicitly.
/// </remarks>
public static class Money
{
    public static string Usd(decimal amount) =>
        amount < 0m
            ? $"-${Math.Abs(amount).ToString("N2", CultureInfo.InvariantCulture)}"
            : $"${amount.ToString("N2", CultureInfo.InvariantCulture)}";

    public static string Percent(decimal fraction) =>
        $"{(fraction * 100m).ToString("N2", CultureInfo.InvariantCulture)}%";
}
