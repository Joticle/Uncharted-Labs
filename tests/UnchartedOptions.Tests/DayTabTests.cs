using System.Globalization;
using System.Text.Json;

namespace UnchartedOptions.Tests;

/// <summary>
/// The day tabs are the three most recent Eastern days the agent ran.
/// </summary>
/// <remarks>
/// Two defects sat on the same line. The runs were grouped by the UTC date while every figure
/// beneath them is Eastern, so Wednesday evening's cycles -- 00:17 to 00:33 UTC -- were filed
/// under 09.03 and drew a tab labelled 09.03 above a header reading 09.02. And the three
/// slots were filled first / middle / latest across every day in the log, which reached back
/// into the weekend rehearsals and stepped straight over Tuesday 09.01, a full session
/// carrying three entries and both positions that later closed on pin risk.
/// </remarks>
public class DayTabTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "FRONTEND_CONTRACT.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Page() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dashboard-design", "index.html"));

    /// <summary>Eastern dates of every run in the committed log, in order, deduplicated.</summary>
    private static List<DateOnly> LoggedDays()
    {
        List<DateOnly> days = [];

        foreach (string line in File.ReadAllLines(
            Path.Combine(RepoRoot(), "decisions", "decisions.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            string stamp = doc.RootElement.GetProperty("timestamp").GetString()!;
            DateTimeOffset at = DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            // The same Eastern offset the agent and the page both use.
            DateOnly day = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(-4)).DateTime);

            if (!days.Contains(day))
            {
                days.Add(day);
            }
        }

        days.Sort();
        return days;
    }

    /// <summary>What the three buttons will read, given those days.</summary>
    private static List<string> Tabs() =>
        [.. LoggedDays().TakeLast(3).Select(d => d.ToString("MM.dd", CultureInfo.InvariantCulture))];

    // ---- the tabs the log actually produces ----

    [Fact]
    public void Tuesday_has_a_tab()
    {
        // 09.01 was skipped entirely by first / middle / latest.
        Assert.Contains("09.01", Tabs());
    }

    [Fact]
    public void The_tabs_are_the_three_most_recent_sessions()
    {
        // Thursday's cycles are in the log now, so Monday rolls off the left.
        Assert.Equal(["09.01", "09.02", "09.03"], Tabs());
    }

    /// <summary>
    /// Wednesday's evening cycles are Wednesday. Grouped on the UTC date they became 09.03,
    /// a day the contest had not reached.
    /// </summary>
    [Fact]
    public void No_tab_is_dated_past_the_last_session_the_agent_ran()
    {
        List<DateOnly> days = LoggedDays();

        DateTimeOffset latest = DateTimeOffset.Parse(
            File.ReadAllLines(Path.Combine(RepoRoot(), "decisions", "decisions.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("timestamp").GetString()!)
                .Max()!,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        DateOnly easternLatest = DateOnly.FromDateTime(latest.ToOffset(TimeSpan.FromHours(-4)).DateTime);

        Assert.Equal(easternLatest, days[^1]);
    }

    /// <summary>The cycles a rebase discarded, and the reason this file reads the log at all.</summary>
    [Fact]
    public void Wednesdays_scheduled_cycles_are_in_the_log()
    {
        string log = File.ReadAllText(Path.Combine(RepoRoot(), "decisions", "decisions.jsonl"));

        Assert.Contains("2026-09-02T13:45:00Z", log, StringComparison.Ordinal);
        Assert.Contains("2026-09-02T17:16:41Z", log, StringComparison.Ordinal);
    }

    // ---- the rules, in the page that applies them ----

    [Fact]
    public void The_page_groups_runs_on_the_Eastern_date()
    {
        string page = Page();

        Assert.Contains("const day = this.etDay(r.timestamp);", page, StringComparison.Ordinal);
        Assert.Contains("timeZone: 'America/New_York'", page, StringComparison.Ordinal);

        // Slicing the ISO string is the UTC date, which is what mislabelled the tab.
        Assert.DoesNotContain("const day = String(r.timestamp).slice(0, 10);", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_keeps_the_last_three_days_rather_than_first_middle_latest()
    {
        string page = Page();

        Assert.Contains("const last3 = days.slice(-3);", page, StringComparison.Ordinal);
        Assert.DoesNotContain("days[Math.floor((days.length - 1) / 2)]", page, StringComparison.Ordinal);
    }

    /// <summary>A tab's label and the header beneath it must come from one conversion.</summary>
    [Fact]
    public void The_label_and_the_payload_share_a_calendar()
    {
        string page = Page();

        Assert.Contains("const asOfDay = asOf ? this.etDay(asOf.timestamp)", page, StringComparison.Ordinal);
        Assert.Contains("const etDate = (iso) => this.etDay(iso)", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this.etDay(r.timestamp)")]
    [InlineData("const last3 = days.slice(-3);")]
    [InlineData("timeZone: 'America/New_York'")]
    public void The_generator_carries_every_change_the_page_carries(string fragment)
    {
        Assert.Contains(fragment, Page(), StringComparison.Ordinal);
        Assert.Contains(fragment, File.ReadAllText(Path.Combine(RepoRoot(), "tools", "wire-design.py")),
            StringComparison.Ordinal);
    }
}
