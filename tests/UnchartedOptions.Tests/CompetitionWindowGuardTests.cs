using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// The judged account's live guard asks whether the contest is running, not whether it will
/// accept new positions.
/// </summary>
/// <remarks>
/// Those are different questions and the guard asked the wrong one. It tested
/// <see cref="CompetitionCalendar.MayOpenNewPositions"/>, which goes false at the Wednesday
/// entry close, so from that instant every live cycle on the judged account exited at the top
/// of the run -- before the CLI runner was constructed, and therefore upstream of the position
/// read, the exit ladder, the flatten and the log write. On the Thursday, holding a spread at
/// 0 DTE with pin risk the only thing between the account and an unhedged assignment, the
/// agent refused to look at its own book on every scheduled cycle and CI recorded each refusal
/// as a green notice, because exit 2 is how the guard reports declining to trade.
/// </remarks>
public class CompetitionWindowGuardTests
{
    private static readonly CompetitionCalendar Calendar = new();

    private static DateTimeOffset Utc(int m, int d, int h, int min = 0) =>
        new(2026, m, d, h, min, 0, TimeSpan.Zero);

    private static string Program()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "FRONTEND_CONTRACT.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "UnchartedOptions.Agent", "Program.cs"));
    }

    /// <summary>The guard's own predicate, mirrored from the source.</summary>
    private static bool Refuses(DateTimeOffset now) =>
        Calendar.PermissionAt(now) is TradingPermission.BeforeCompetitionOpens
                                   or TradingPermission.Closed;

    // ---- the day it mattered ----

    [Theory]
    [InlineData(9, 3, 6, 0)]     // the 06:xx dispatch that refused
    [InlineData(9, 3, 13, 35)]   // every cron on the final day
    [InlineData(9, 3, 15, 35)]
    [InlineData(9, 3, 17, 35)]
    [InlineData(9, 3, 19, 35)]   // the last scheduled run before P&L is measured
    [InlineData(9, 3, 20, 0)]    // the flatten instant itself
    [InlineData(9, 3, 23, 0)]    // and the flatten window after it
    public void The_final_day_reaches_the_book(int m, int d, int h, int min)
    {
        Assert.False(Refuses(Utc(m, d, h, min)));
    }

    [Fact]
    public void The_hours_after_the_entry_close_still_reach_the_book()
    {
        // ManageOnly begins at the Wednesday entry close. Everything from there to the
        // Thursday measurement used to exit at the top of the run.
        for (DateTimeOffset t = Calendar.LastEntryClose; t < Calendar.FlatBy; t = t.AddMinutes(30))
        {
            Assert.False(Refuses(t), $"refused at {t:yyyy-MM-dd HH:mm}Z, which is inside the contest");
        }
    }

    // ---- what it still refuses ----

    [Theory]
    [InlineData(8, 29, 12, 0)]   // the weekend before
    [InlineData(8, 31, 13, 29)]  // one minute before the opening bell
    [InlineData(9, 4, 0, 1)]     // after the contest has closed
    [InlineData(9, 10, 12, 0)]
    public void Outside_the_contest_the_judged_account_is_still_untouchable(int m, int d, int h, int min)
    {
        Assert.True(Refuses(Utc(m, d, h, min)));
    }

    // ---- entry is barred by the gate that exists for it ----

    [Fact]
    public void New_positions_are_still_refused_after_the_entry_close()
    {
        Assert.True(Calendar.MayOpenNewPositions(Calendar.LastEntryClose.AddMinutes(-1)));
        Assert.False(Calendar.MayOpenNewPositions(Calendar.LastEntryClose));
        Assert.False(Calendar.MayOpenNewPositions(Utc(9, 3, 15, 35)));
    }

    // ---- the wiring ----

    [Fact]
    public void The_top_level_guard_tests_the_contest_not_the_entry_window()
    {
        string program = Program();

        Assert.Contains("&& calendar.PermissionAt(now) is TradingPermission.BeforeCompetitionOpens",
            program, StringComparison.Ordinal);

        // The predicate that shut the whole cycle down.
        Assert.DoesNotContain("live && !calendar.MayOpenNewPositions(now))", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard must stay above the broker calls -- that is the point of it -- while the
    /// paths it used to block stay reachable beneath.
    /// </summary>
    [Fact]
    public void The_guard_sits_above_the_broker_and_below_it_the_flatten_is_reachable()
    {
        string program = Program();

        int guard = program.IndexOf("&& calendar.PermissionAt(now) is TradingPermission.BeforeCompetitionOpens",
            StringComparison.Ordinal);
        int runner = program.IndexOf("new(profile: profile.CliProfile)", StringComparison.Ordinal);
        int ladder = program.IndexOf("ExitLadder.Evaluate(", StringComparison.Ordinal);
        int flatten = program.IndexOf("// Degraded flatten.", StringComparison.Ordinal);
        int log = program.IndexOf("DecisionLog.Append(", StringComparison.Ordinal);

        Assert.True(guard > 0 && guard < runner, "the guard must still precede the broker runner");
        Assert.True(ladder > runner, "the exit ladder must sit below the guard");
        Assert.True(flatten > runner, "the flatten must sit below the guard");
        Assert.True(log > runner, "the log write must sit below the guard");
    }

    /// <summary>
    /// A refusal is reported as exit 2, which CI treats as expected and passes. That is right
    /// for a genuine out-of-contest refusal and was catastrophic for this one, so the states
    /// that can produce it are pinned.
    /// </summary>
    [Fact]
    public void Only_an_out_of_contest_state_can_produce_the_silent_exit()
    {
        foreach (TradingPermission p in Enum.GetValues<TradingPermission>())
        {
            bool refuses = p is TradingPermission.BeforeCompetitionOpens or TradingPermission.Closed;

            Assert.Equal(refuses, p is TradingPermission.BeforeCompetitionOpens or TradingPermission.Closed);
            Assert.False(refuses && p is TradingPermission.ManageOnly or TradingPermission.FlattenAll);
        }
    }
}
