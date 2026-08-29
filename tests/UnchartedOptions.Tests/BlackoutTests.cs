using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class BlackoutTests
{
    private static BlackoutEvent Earnings(string sym, int m, int d) => new()
    {
        Underlying = sym,
        Date = new DateOnly(2026, m, d),
        Reason = BlackoutReason.Earnings,
        Source = "manual earnings list",
    };

    [Fact]
    public void An_underlying_with_no_events_is_never_blacked_out()
    {
        BlackoutCalendar c = new([Earnings("QQQ", 9, 2)]);

        Assert.False(c.Check("SPY", new DateOnly(2026, 9, 2)).IsBlackedOut);
    }

    [Theory]
    [InlineData(9, 2)]   // the day itself
    [InlineData(9, 1)]   // 1 session before
    [InlineData(8, 28)]  // 3 sessions before, spanning a weekend
    [InlineData(9, 7)]   // 3 sessions after, spanning a weekend
    public void The_window_extends_the_configured_sessions_either_side(int m, int d)
    {
        BlackoutCalendar c = new([Earnings("SPY", 9, 2)], sessionsEitherSide: 3);

        Assert.True(c.Check("SPY", new DateOnly(2026, m, d)).IsBlackedOut);
    }

    [Theory]
    [InlineData(8, 27)]  // 4 sessions before
    [InlineData(9, 8)]   // 4 sessions after
    public void Outside_the_window_the_underlying_is_clear(int m, int d)
    {
        BlackoutCalendar c = new([Earnings("SPY", 9, 2)], sessionsEitherSide: 3);

        Assert.False(c.Check("SPY", new DateOnly(2026, m, d)).IsBlackedOut);
    }

    /// <summary>Weekends are not trading sessions, so the window spans further in calendar days.</summary>
    [Fact]
    public void Sessions_are_counted_as_weekdays_not_calendar_days()
    {
        // Fri 28 Aug to Wed 2 Sep is 5 calendar days but 3 sessions.
        Assert.Equal(3, BlackoutCalendar.SessionsBetween(new DateOnly(2026, 8, 28), new DateOnly(2026, 9, 2)));
        Assert.Equal(-3, BlackoutCalendar.SessionsBetween(new DateOnly(2026, 9, 2), new DateOnly(2026, 8, 28)));
        Assert.Equal(0, BlackoutCalendar.SessionsBetween(new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 2)));
    }

    [Fact]
    public void The_verdict_names_its_source_so_an_unsourced_gate_cannot_hide()
    {
        BlackoutCalendar c = new([Earnings("SPY", 9, 2)]);
        BlackoutVerdict v = c.Check("SPY", new DateOnly(2026, 9, 1));

        Assert.Contains("manual earnings list", v.Explanation, StringComparison.Ordinal);
        Assert.Equal(BlackoutReason.Earnings, v.Cause!.Reason);
    }

    [Fact]
    public void Ex_dividend_events_gate_independently_of_earnings()
    {
        BlackoutCalendar c = new([new BlackoutEvent
        {
            Underlying = "SPY",
            Date = new DateOnly(2026, 9, 2),
            Reason = BlackoutReason.ExDividend,
            Source = "alpaca corporate-actions",
        }]);

        BlackoutVerdict v = c.Check("SPY", new DateOnly(2026, 9, 1));

        Assert.True(v.IsBlackedOut);
        Assert.Equal(BlackoutReason.ExDividend, v.Cause!.Reason);
    }

    [Fact]
    public void Well_formed_entries_parse()
    {
        IReadOnlyList<BlackoutEvent> parsed = BlackoutCalendar.ParseEarnings(["SPY:2026-09-02", "qqq:2026-09-10"]);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("QQQ", parsed[1].Underlying);
        Assert.All(parsed, e => Assert.Equal(BlackoutReason.Earnings, e.Reason));
    }

    /// <summary>
    /// A dropped earnings date is an underlying the agent believes is clear when it is not.
    /// That is the inert-gate failure this class exists to avoid, so it throws.
    /// </summary>
    [Theory]
    [InlineData("SPY-2026-09-02")]
    [InlineData("SPY:02-09-2026")]
    [InlineData("SPY")]
    public void A_malformed_entry_throws_rather_than_being_skipped(string entry)
    {
        Assert.Throws<FormatException>(() => BlackoutCalendar.ParseEarnings([entry]));
    }
}
