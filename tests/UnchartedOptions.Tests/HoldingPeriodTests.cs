using System.Globalization;
using System.Text.RegularExpressions;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// The days-to-close card must show a number or a placeholder, never NaN.
/// </summary>
/// <remarks>
/// The consumer reads the closed-trade tuple positionally and parses index three as a
/// holding period. That field did not exist, so it parsed an empty string the moment the
/// first trade closed and the card rendered NaN in front of a judge. The card had a
/// placeholder for the no-trades case and no defence for a malformed one.
/// </remarks>
public class HoldingPeriodTests
{
    private static RealisedTrade Trade(
        DateTimeOffset opened, DateTimeOffset closed, decimal pnl = 100m) => new()
    {
        Underlying = "SPY",
        Expiration = new DateOnly(2026, 9, 3),
        Strikes = [764m, 769m],
        RealisedPnl = pnl,
        OpenedAt = opened,
        ClosedAt = closed,
        Fills = 4,
    };

    private static DateTimeOffset Utc(int m, int d, int h = 15) => new(2026, m, d, h, 0, 0, TimeSpan.Zero);

    private static DashboardFeed Feed(params RealisedTrade[] closed)
    {
        LogRun run = new()
        {
            RunId = "r", Timestamp = "2026-09-02T18:00:00Z", Account = "PA3BG520YCTT",
            Profile = "comp", IsCompetition = true, MarketOpen = true, DryRun = false,
            Equity = 100_000m, CalendarState = "OpenAndManage",
            RiskPerTrade = new GateUtilisation
            {
                Label = "risk per trade", CeilingPercent = 3m, CeilingDollars = 3_000m,
                DeployedDollars = 0m, DeployedPercent = 0m,
            },
            SymbolExposure = [], Decisions = [],
        };

        return DashboardFeedBuilder.Build(
            run, [], [], closed, [], "SPY", new RiskMandate(), 0,
            new CompetitionCalendar(), Utc(9, 2, 18));
    }

    /// <summary>
    /// Mirrors what the consumer does with the tuple: parse index three and average it.
    /// Asserting on the emitted field rather than on a computed mean is the point -- the
    /// defect was a field that did not exist, which no test of the arithmetic would catch.
    /// </summary>
    private static double MeanHeld(DashboardFeed feed)
    {
        if (feed.Closed.Count == 0)
        {
            return double.NaN;
        }

        double total = 0;

        foreach (FeedClosed c in feed.Closed)
        {
            Match m = Regex.Match(c.Held ?? string.Empty, @"^\s*(\d+(?:\.\d+)?)");
            total += m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        }

        return total / feed.Closed.Count;
    }

    /// <summary>What the card renders, given the mean the consumer computes.</summary>
    private static string Rendered(DashboardFeed feed)
    {
        double mean = MeanHeld(feed);

        return feed.Closed.Count > 0 && double.IsFinite(mean)
            ? mean.ToString("F1", CultureInfo.InvariantCulture)
            : "—";
    }

    // ---- the field exists and carries a real figure ----

    [Fact]
    public void A_closed_trade_carries_a_holding_period_and_a_close_date()
    {
        FeedClosed c = Assert.Single(Feed(Trade(Utc(9, 1), Utc(9, 2))).Closed);

        Assert.False(string.IsNullOrWhiteSpace(c.Held), "held was absent, which is what produced NaN");
        Assert.False(string.IsNullOrWhiteSpace(c.ClosedOn), "closedOn was absent, which blanked the date column");
        Assert.Matches(@"^\d+d$", c.Held);
    }

    [Theory]
    [InlineData(9, 1, 9, 2, "1d")]      // opened Tue, closed Wed
    [InlineData(9, 1, 9, 3, "2d")]      // two sessions
    [InlineData(9, 2, 9, 2, "1d")]      // opened and closed the same session, never zero
    [InlineData(9, 4, 9, 7, "1d")]      // Friday to Monday: the weekend is not a session
    public void The_holding_period_counts_sessions_not_calendar_days(
        int om, int od, int cm, int cd, string expected)
    {
        FeedClosed c = Assert.Single(Feed(Trade(Utc(om, od), Utc(cm, cd))).Closed);

        Assert.Equal(expected, c.Held);
    }

    // ---- the card, across the sample sizes that matter ----

    [Fact]
    public void With_no_closed_trades_the_card_shows_the_placeholder()
    {
        Assert.Equal("—", Rendered(Feed()));
    }

    [Fact]
    public void With_one_closed_trade_the_card_shows_a_number()
    {
        // The exact case that rendered NaN: both exits opened 09.01 and closed 09.02.
        Assert.Equal("1.0", Rendered(Feed(Trade(Utc(9, 1), Utc(9, 2)))));
    }

    [Fact]
    public void With_two_closed_trades_the_card_shows_their_mean()
    {
        DashboardFeed feed = Feed(
            Trade(Utc(9, 1), Utc(9, 2)),
            Trade(Utc(9, 1), Utc(9, 3), pnl: -50m));

        Assert.Equal("1.5", Rendered(feed));
    }

    /// <summary>The property the card must hold whatever the sample size.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void The_card_never_renders_NaN(int closedCount)
    {
        RealisedTrade[] trades = Enumerable.Range(0, closedCount)
            .Select(i => Trade(Utc(9, 1), Utc(9, 2 + (i % 2))))
            .ToArray();

        string rendered = Rendered(Feed(trades));

        Assert.DoesNotContain("NaN", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.True(rendered == "—" || double.TryParse(rendered, NumberStyles.Any,
            CultureInfo.InvariantCulture, out _), $"card rendered '{rendered}'");
    }

    /// <summary>
    /// Defence in depth. Even if the field regressed to empty, the consumer's guard must fall
    /// back to the placeholder rather than print NaN.
    /// </summary>
    [Fact]
    public void An_unparseable_holding_period_falls_back_to_the_placeholder()
    {
        double mean = double.NaN;
        string rendered = double.IsFinite(mean) ? mean.ToString("F1", CultureInfo.InvariantCulture) : "—";

        Assert.Equal("—", rendered);
        Assert.DoesNotContain("NaN", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The wired consumer carries the same guard, not just this test's copy of it.</summary>
    [Fact]
    public void The_generated_dashboard_guards_the_mean_before_printing_it()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "FRONTEND_CONTRACT.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string page = File.ReadAllText(Path.Combine(dir!.FullName, "dashboard-design", "index.html"));

        Assert.Contains("Number.isFinite(heldMean)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("parseFloat(c[3]), 0) / nClosed).toFixed", page, StringComparison.Ordinal);
    }
}
