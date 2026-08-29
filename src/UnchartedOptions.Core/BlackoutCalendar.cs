using System.Globalization;

namespace UnchartedOptions.Core;

public enum BlackoutReason
{
    Earnings,
    ExDividend,
}

/// <summary>A dated event that closes an underlying to new positions.</summary>
public sealed record BlackoutEvent
{
    public required string Underlying { get; init; }

    public required DateOnly Date { get; init; }

    public required BlackoutReason Reason { get; init; }

    /// <summary>Where this date came from. Surfaced so an unsourced gate cannot hide.</summary>
    public required string Source { get; init; }
}

public sealed record BlackoutVerdict
{
    public required bool IsBlackedOut { get; init; }

    public BlackoutEvent? Cause { get; init; }

    public required string Explanation { get; init; }
}

/// <summary>
/// Closes an underlying to new positions around events that distort option pricing.
/// </summary>
/// <remarks>
/// <para>
/// Earnings are the primary case. The 2025 University of Florida study found retail losses
/// on complex multi-leg options roughly three times larger around earnings, which makes
/// simply not trading through them the cheapest risk reduction available.
/// </para>
/// <para>
/// <b>Sourcing.</b> Alpaca's corporate-actions endpoint carries cash dividends, splits,
/// mergers and spin-offs -- it does not publish earnings dates. Rather than ship a gate that
/// silently never fires, earnings dates are supplied explicitly and the calendar reports
/// which source each date came from. Ex-dividend dates, by contrast, do come from Alpaca and
/// are populated automatically.
/// </para>
/// <para>
/// Ex-dividend earns its place independently: a short call carrying less time value than the
/// dividend is a candidate for early assignment, which on a vertical means the short leg
/// disappearing and leaving a naked long -- a different position from the one that was sized.
/// </para>
/// </remarks>
public sealed class BlackoutCalendar
{
    private readonly List<BlackoutEvent> _events;

    public BlackoutCalendar(IEnumerable<BlackoutEvent>? events = null, int sessionsEitherSide = 3)
    {
        _events = events?.ToList() ?? [];
        SessionsEitherSide = sessionsEitherSide;
    }

    /// <summary>Trading sessions of clearance required on each side of an event.</summary>
    public int SessionsEitherSide { get; }

    public IReadOnlyList<BlackoutEvent> Events => _events;

    /// <summary>Whether any event is close enough to refuse a new position in this underlying.</summary>
    public BlackoutVerdict Check(string underlying, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);

        foreach (BlackoutEvent e in _events)
        {
            if (!string.Equals(e.Underlying, underlying, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int sessions = SessionsBetween(asOf, e.Date);

            if (Math.Abs(sessions) <= SessionsEitherSide)
            {
                string when = sessions == 0 ? "today"
                    : sessions > 0 ? $"in {sessions} session(s)"
                    : $"{-sessions} session(s) ago";

                return new BlackoutVerdict
                {
                    IsBlackedOut = true,
                    Cause = e,
                    Explanation = $"{e.Reason} {when} ({e.Date:yyyy-MM-dd}, per {e.Source}); "
                                + $"blackout is {SessionsEitherSide} session(s) either side.",
                };
            }
        }

        return new BlackoutVerdict
        {
            IsBlackedOut = false,
            Explanation = _events.Any(e => string.Equals(e.Underlying, underlying, StringComparison.OrdinalIgnoreCase))
                ? "Clear of all known events."
                : $"No events on file for {underlying}.",
        };
    }

    /// <summary>
    /// Trading sessions from <paramref name="from"/> to <paramref name="to"/>, counting
    /// weekdays only.
    /// </summary>
    /// <remarks>
    /// Market holidays are not subtracted. That makes a window very slightly wider than the
    /// literal session count around a holiday, which errs toward refusing a trade rather than
    /// taking one -- the safe direction for a gate whose purpose is to decline.
    /// </remarks>
    internal static int SessionsBetween(DateOnly from, DateOnly to)
    {
        int sign = to >= from ? 1 : -1;
        DateOnly a = sign > 0 ? from : to;
        DateOnly b = sign > 0 ? to : from;

        int sessions = 0;
        for (DateOnly d = a; d < b; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                sessions++;
            }
        }

        return sessions * sign;
    }

    /// <summary>
    /// Parses <c>SYMBOL:YYYY-MM-DD</c> pairs, the manual earnings source.
    /// </summary>
    /// <remarks>
    /// Malformed entries throw rather than being skipped. A silently dropped earnings date
    /// is an underlying the agent believes is clear when it is not, which is precisely the
    /// inert-gate failure this class exists to avoid.
    /// </remarks>
    public static IReadOnlyList<BlackoutEvent> ParseEarnings(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<BlackoutEvent> parsed = [];

        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly date))
            {
                throw new FormatException(
                    $"Earnings entry '{entry}' is not SYMBOL:YYYY-MM-DD. Refusing to skip it silently.");
            }

            parsed.Add(new BlackoutEvent
            {
                Underlying = parts[0].ToUpperInvariant(),
                Date = date,
                Reason = BlackoutReason.Earnings,
                Source = "manual earnings list",
            });
        }

        return parsed;
    }
}
