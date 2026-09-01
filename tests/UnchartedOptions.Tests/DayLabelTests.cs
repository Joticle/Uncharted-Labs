using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// The header must not describe a session that the gates have already closed.
/// </summary>
/// <remarks>
/// Counting calendar days alone read "Day 4 of 4" for four hours after the Thursday close --
/// P&amp;L measured, the calendar reporting FlattenAll, and the header still implying trading
/// was under way. Two clocks disagreeing about the same instant, in front of a judge.
/// </remarks>
public class DayLabelTests
{
    private static readonly CompetitionCalendar Calendar = new();

    private static string LabelAt(DateTimeOffset now)
    {
        LogRun run = new()
        {
            RunId = "r", Timestamp = DecisionLog.Stamp(now), Account = "PA3BG520YCTT",
            Profile = "comp", IsCompetition = true, MarketOpen = true, DryRun = false,
            Equity = 100_000m, CalendarState = Calendar.PermissionAt(now).ToString(),
            RiskPerTrade = new GateUtilisation
            {
                Label = "risk per trade", CeilingPercent = 3m, CeilingDollars = 3_000m,
                DeployedDollars = 0m, DeployedPercent = 0m,
            },
            SymbolExposure = [], Decisions = [],
        };

        DashboardFeed feed = DashboardFeedBuilder.Build(
            run, [], [], [], [], "SPY", new RiskMandate(), 0, Calendar, now);

        return feed.Day;
    }

    private static DateTimeOffset Utc(int m, int d, int h, int min = 0) =>
        new(2026, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void Before_the_opening_bell_it_reads_pre_open()
    {
        Assert.Equal("Pre-open", LabelAt(Utc(8, 30, 12)));
        Assert.Equal("Pre-open", LabelAt(Utc(8, 31, 13, 29)));
    }

    [Theory]
    [InlineData(8, 31, "Day 1 of 4")]
    [InlineData(9, 1, "Day 2 of 4")]
    [InlineData(9, 2, "Day 3 of 4")]
    [InlineData(9, 3, "Day 4 of 4")]
    public void During_the_contest_it_counts_the_session(int m, int d, string expected)
    {
        Assert.Equal(expected, LabelAt(Utc(m, d, 15)));
    }

    /// <summary>
    /// The regression. P&amp;L is measured at the Thursday close; a minute later the header
    /// must not still be describing a session in progress.
    /// </summary>
    [Fact]
    public void After_the_thursday_close_it_stops_claiming_a_session_is_running()
    {
        Assert.Equal("Day 4 of 4", LabelAt(Utc(9, 3, 19, 59)));

        Assert.Equal("P&L measured", LabelAt(Utc(9, 3, 20, 1)));
        Assert.Equal("P&L measured", LabelAt(Utc(9, 3, 23, 30)));
    }

    [Fact]
    public void Once_the_contest_is_over_it_reads_closed()
    {
        Assert.Equal("Closed", LabelAt(Utc(9, 4, 14)));
        Assert.Equal("Closed", LabelAt(Utc(9, 10, 14)));
    }

    /// <summary>
    /// The property that makes the header trustworthy: it is a function of the same calendar
    /// the gates consult, so it cannot contradict them.
    /// </summary>
    [Theory]
    [InlineData(8, 30, 12)]
    [InlineData(8, 31, 15)]
    [InlineData(9, 2, 15)]
    [InlineData(9, 3, 15)]
    [InlineData(9, 3, 20, 1)]
    [InlineData(9, 4, 14)]
    public void The_header_never_says_a_session_is_running_when_the_gates_say_otherwise(
        int m, int d, int h, int min = 0)
    {
        DateTimeOffset now = Utc(m, d, h, min);
        string label = LabelAt(now);
        TradingPermission permission = Calendar.PermissionAt(now);

        bool headerClaimsSession = label.StartsWith("Day ", StringComparison.Ordinal);
        bool gatesAllowTrading = permission
            is TradingPermission.OpenAndManage or TradingPermission.ManageOnly;

        Assert.Equal(gatesAllowTrading, headerClaimsSession);
    }

    /// <summary>The label is ASCII; a non-ASCII glyph has twice reached the payload mangled.</summary>
    [Fact]
    public void The_label_is_ascii_in_every_phase()
    {
        foreach (DateTimeOffset t in new[]
                 { Utc(8, 30, 12), Utc(9, 1, 15), Utc(9, 3, 20, 1), Utc(9, 4, 14) })
        {
            string label = LabelAt(t);
            Assert.All(label, c => Assert.True(c < 128, $"non-ASCII in '{label}'"));
        }
    }
}
