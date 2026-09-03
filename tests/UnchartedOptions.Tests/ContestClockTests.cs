using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// The contest clock counts Eastern days, because that is what the page states.
/// </summary>
/// <remarks>
/// The phase was rewired to come from <see cref="CompetitionCalendar.PermissionAt"/> so the
/// header could not disagree with the gates, and it does not. The number beside it was left
/// as arithmetic on <c>now.Date</c>, which is the UTC date -- and the UTC date rolls over at
/// 20:00 ET. A cycle at 20:33 ET on Wednesday is 00:33 UTC on Thursday, so the header read
/// "Day 4 of 4" on a Wednesday evening: Thursday announced four hours early, on the one
/// counter a judge reads as the contest clock. Same seam put the day tabs a date ahead of
/// their own contents.
/// </remarks>
public class ContestClockTests
{
    private static readonly CompetitionCalendar Calendar = new();

    private static DateTimeOffset Utc(int m, int d, int h, int min = 0) =>
        new(2026, m, d, h, min, 0, TimeSpan.Zero);

    private static string Day(DateTimeOffset now)
    {
        LogRun run = new()
        {
            RunId = "r", Timestamp = now.ToString("O"), Account = "PA3BG520YCTT",
            Profile = "comp", IsCompetition = true, MarketOpen = true, DryRun = true,
            Equity = 100_000m, CalendarState = Calendar.PermissionAt(now).ToString(),
            RiskPerTrade = new GateUtilisation
            {
                Label = "risk per trade", CeilingPercent = 3m, CeilingDollars = 3_000m,
                DeployedDollars = 0m, DeployedPercent = 0m,
            },
            SymbolExposure = [], Decisions = [],
        };

        return DashboardFeedBuilder.Build(
            run, [], [], [], [], "SPY", new RiskMandate(), 0, Calendar, now).Day;
    }

    // ---- the seam ----

    /// <summary>
    /// 20:33 ET Wednesday is 00:33 UTC Thursday. The contest is on its third day.
    /// </summary>
    [Fact]
    public void A_Wednesday_evening_cycle_does_not_announce_Thursday()
    {
        Assert.Equal("Day 3 of 4", Day(Utc(9, 3, 0, 33)));
    }

    [Theory]
    // Eastern date -> contest day. Trading opened Monday 31 Aug.
    [InlineData(8, 31, 14, "Day 1 of 4")]   // Mon 10:00 ET
    [InlineData(9, 1, 3, "Day 1 of 4")]     // Mon 23:00 ET, still Monday in Eastern
    [InlineData(9, 1, 14, "Day 2 of 4")]    // Tue 10:00 ET
    [InlineData(9, 2, 2, "Day 2 of 4")]     // Tue 22:00 ET
    [InlineData(9, 2, 14, "Day 3 of 4")]    // Wed 10:00 ET
    [InlineData(9, 3, 0, "Day 3 of 4")]     // Wed 20:00 ET -- the reported case
    [InlineData(9, 3, 3, "Day 3 of 4")]     // Wed 23:00 ET
    [InlineData(9, 3, 14, "Day 4 of 4")]    // Thu 10:00 ET, and now it really is Thursday
    public void The_counter_follows_the_Eastern_date(int m, int d, int h, string expected)
    {
        Assert.Equal(expected, Day(Utc(m, d, h)));
    }

    /// <summary>
    /// Every UTC hour of the contest, checked against the Eastern day it belongs to. Guards
    /// the whole seam rather than the two instants that happened to be caught by hand.
    /// </summary>
    [Fact]
    public void No_hour_of_the_contest_reports_a_day_the_Eastern_calendar_has_not_reached()
    {
        for (DateTimeOffset t = Utc(8, 31, 14); t < Utc(9, 3, 20); t = t.AddHours(1))
        {
            DateOnly eastern = DateOnly.FromDateTime(t.ToOffset(TimeSpan.FromHours(-4)).DateTime);
            int expected = eastern.DayNumber - new DateOnly(2026, 8, 31).DayNumber + 1;

            Assert.Equal($"Day {expected} of 4", Day(t));
        }
    }

    // ---- the phase still comes from the calendar, not from the count ----

    [Fact]
    public void Thursdays_close_replaces_the_count_rather_than_advancing_it()
    {
        // 16:00 ET Thursday: P&L is measured, so there is no session left to number.
        Assert.Equal("P&L measured", Day(Utc(9, 3, 20)));
        Assert.Equal("Closed", Day(Utc(9, 4, 0, 1)));
        Assert.Equal("Pre-open", Day(Utc(8, 29, 12)));
    }

    /// <summary>The count never runs past the contest it is counting.</summary>
    [Fact]
    public void The_counter_is_bounded_by_the_contest_length()
    {
        for (DateTimeOffset t = Utc(8, 31, 14); t < Utc(9, 4, 0); t = t.AddHours(1))
        {
            string day = Day(t);

            Assert.DoesNotContain("Day 5", day, StringComparison.Ordinal);
            Assert.DoesNotContain("Day 0", day, StringComparison.Ordinal);
        }
    }

    // ---- days to expiry sits on the same seam ----

    [Fact]
    public void Days_to_expiry_counts_from_the_Eastern_date()
    {
        SpreadPosition held = new()
        {
            Spread = new VerticalSpread
            {
                Underlying = "SPY", Direction = SpreadDirection.BullCall,
                LongSymbol = "SPY260903C00766000", ShortSymbol = "SPY260903C00771000",
                NetDebit = 1.37m, StrikeWidth = 5m, Expiration = new DateOnly(2026, 9, 3),
            },
            Contracts = 10, CurrentValue = 1.37m, OpenedAt = Utc(9, 2, 17),
        };

        LogRun run = new()
        {
            RunId = "r", Timestamp = "2026-09-03T00:33:00Z", Account = "PA3BG520YCTT",
            Profile = "comp", IsCompetition = true, MarketOpen = false, DryRun = true,
            Equity = 99_755m, CalendarState = "ManageOnly",
            RiskPerTrade = new GateUtilisation
            {
                Label = "risk per trade", CeilingPercent = 3m, CeilingDollars = 2_992m,
                DeployedDollars = 1_370m, DeployedPercent = 1.37m,
            },
            SymbolExposure = [], Decisions = [],
        };

        // 20:33 ET Wednesday against a Thursday expiry: one day, not none.
        FeedPosition p = Assert.Single(DashboardFeedBuilder.Build(
            run, [held], [], [], [], "SPY", new RiskMandate(), 0, Calendar, Utc(9, 3, 0, 33)).Positions);

        Assert.Equal(1, p.Dte);
    }
}
