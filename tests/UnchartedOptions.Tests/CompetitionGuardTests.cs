using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class CompetitionGuardTests
{
    private static readonly CompetitionCalendar Calendar = new();

    private static DateTimeOffset Utc(int m, int d, int h, int min) =>
        new(2026, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void The_default_profile_is_the_dev_account()
    {
        Assert.False(TradingProfile.FromArgs([]).IsCompetition);
        Assert.False(TradingProfile.FromArgs(["--live"]).IsCompetition);
        Assert.Equal("paper", TradingProfile.FromArgs([]).CliProfile);
    }

    [Fact]
    public void Only_an_explicit_comp_flag_selects_the_competition_account()
    {
        Assert.True(TradingProfile.FromArgs(["--comp"]).IsCompetition);
        Assert.True(TradingProfile.FromArgs(["--live", "--comp"]).IsCompetition);
        Assert.Equal("comp", TradingProfile.FromArgs(["--comp"]).CliProfile);
    }

    /// <summary>
    /// The competition account must not carry test orders. The whole weekend before the
    /// open is the window in which an accident is most likely.
    /// </summary>
    [Fact]
    public void No_positions_may_be_opened_before_the_competition_starts()
    {
        Assert.False(Calendar.MayOpenNewPositions(Utc(8, 28, 14, 0)));  // Fri, kickoff day
        Assert.False(Calendar.MayOpenNewPositions(Utc(8, 30, 18, 0)));  // Sun
        Assert.False(Calendar.MayOpenNewPositions(Utc(8, 31, 13, 29))); // one minute early

        Assert.Equal(TradingPermission.BeforeCompetitionOpens, Calendar.PermissionAt(Utc(8, 29, 12, 0)));
    }

    [Fact]
    public void Trading_opens_at_the_monday_bell()
    {
        Assert.True(Calendar.MayOpenNewPositions(Utc(8, 31, 13, 30)));
        Assert.Equal(TradingPermission.OpenAndManage, Calendar.PermissionAt(Utc(9, 1, 15, 0)));
    }

    [Fact]
    public void No_new_positions_after_the_wednesday_close()
    {
        Assert.True(Calendar.MayOpenNewPositions(Utc(9, 2, 19, 59)));
        Assert.False(Calendar.MayOpenNewPositions(Utc(9, 2, 20, 0)));

        Assert.Equal(TradingPermission.ManageOnly, Calendar.PermissionAt(Utc(9, 3, 14, 0)));
    }

    [Fact]
    public void Everything_flattens_by_the_thursday_close()
    {
        Assert.Equal(TradingPermission.FlattenAll, Calendar.PermissionAt(Utc(9, 3, 20, 0)));
    }

    /// <summary>
    /// Exercises and assignments for the final expiry count toward the scored equity, so
    /// those contracts may settle rather than being closed out.
    /// </summary>
    [Fact]
    public void Contracts_expiring_on_the_final_day_may_be_held_through_settlement()
    {
        Assert.True(Calendar.MayHoldToExpiry(new DateOnly(2026, 9, 3)));
        Assert.False(Calendar.MayHoldToExpiry(new DateOnly(2026, 9, 4)));
        Assert.False(Calendar.MayHoldToExpiry(new DateOnly(2026, 9, 18)));
    }
}
