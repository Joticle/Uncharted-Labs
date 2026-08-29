using System.Globalization;

namespace UnchartedOptions.Core;

/// <summary>
/// Parses OCC option symbols, e.g. <c>SPY260918C00778000</c>.
/// </summary>
/// <remarks>
/// Layout is a variable-length root followed by a fixed 15-character suffix: six digits of
/// expiry (<c>YYMMDD</c>), one character of type (<c>C</c>/<c>P</c>), and eight digits of
/// strike in thousandths. The root is whatever precedes that, which is why it is taken from
/// the end rather than by assuming a length.
/// </remarks>
public static class OccSymbol
{
    private const int SuffixLength = 15;

    /// <summary>Underlying ticker, or null if the symbol is not a valid OCC option symbol.</summary>
    public static string? Underlying(string symbol) =>
        IsWellFormed(symbol) ? symbol[..^SuffixLength] : null;

    public static decimal? Strike(string symbol)
    {
        if (!IsWellFormed(symbol))
        {
            return null;
        }

        return long.Parse(symbol.AsSpan(symbol.Length - 8), CultureInfo.InvariantCulture) / 1000m;
    }

    public static DateOnly? Expiration(string symbol)
    {
        if (!IsWellFormed(symbol))
        {
            return null;
        }

        ReadOnlySpan<char> d = symbol.AsSpan(symbol.Length - SuffixLength, 6);

        return DateOnly.TryParseExact(d, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : null;
    }

    public static OptionType? Type(string symbol)
    {
        if (!IsWellFormed(symbol))
        {
            return null;
        }

        return char.ToUpperInvariant(symbol[^9]) switch
        {
            'C' => OptionType.Call,
            'P' => OptionType.Put,
            _ => null,
        };
    }

    public static bool IsWellFormed(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length <= SuffixLength)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = symbol.AsSpan(symbol.Length - SuffixLength);

        for (int i = 0; i < 6; i++)
        {
            if (!char.IsAsciiDigit(suffix[i]))
            {
                return false;
            }
        }

        char type = char.ToUpperInvariant(suffix[6]);
        if (type is not ('C' or 'P'))
        {
            return false;
        }

        for (int i = 7; i < SuffixLength; i++)
        {
            if (!char.IsAsciiDigit(suffix[i]))
            {
                return false;
            }
        }

        return true;
    }
}
