using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// An argument the agent does not recognise must stop it, not redirect it.
/// </summary>
/// <remarks>
/// <c>--profile comp</c> is the case that mattered. It is not a flag this agent has, it reads
/// exactly like a request for the judged account, and the old parser ignored it -- so the run
/// went to the dev book while every line of output said what the operator expected to see.
/// The fill history then disagreed with the positions beside it and sent an investigation
/// after a bug that did not exist. Alongside <c>--live</c> the same silence places real orders
/// on the wrong account.
/// </remarks>
public class AgentArgumentsTests
{
    // ---- the misfire ----

    [Fact]
    public void The_flag_that_aimed_the_agent_at_the_wrong_account_is_refused()
    {
        IReadOnlyList<string> faults = AgentArguments.Faults(["--profile", "comp"]);

        string fault = Assert.Single(faults);
        Assert.Contains("--profile", fault, StringComparison.Ordinal);
        Assert.Contains("--comp", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_fault_does_not_share_an_exit_code_with_the_competition_guard()
    {
        // CI allows exit 2 through, because that is the window guard declining to trade the
        // judged account out of hours. A bad command line hiding inside it would pass unseen.
        Assert.NotEqual(2, AgentArguments.UsageExitCode);
        Assert.NotEqual(0, AgentArguments.UsageExitCode);
    }

    [Fact]
    public void Dry_run_is_named_as_the_default_rather_than_accepted_as_a_flag()
    {
        string fault = Assert.Single(AgentArguments.Faults(["--dry-run"]));

        Assert.Contains("--live", fault, StringComparison.Ordinal);
    }

    // ---- what the agent actually accepts ----

    [Theory]
    [InlineData()]
    [InlineData("--live")]
    [InlineData("--comp")]
    [InlineData("--verify", "--comp")]
    [InlineData("--preflight")]
    [InlineData("--comp", "--live", "--expiry", "2026-09-03")]
    [InlineData("--underlying", "SPY", "--blackout-sessions", "2")]
    [InlineData("--log-dir", "decisions", "--as-of", "2026-09-02T18:00:00Z")]
    [InlineData("--earnings", "2026-09-10,2026-12-11")]
    public void The_documented_command_lines_are_accepted(params string[] args)
    {
        Assert.Empty(AgentArguments.Faults(args));
    }

    /// <summary>Every command line CI builds, exactly as the workflow assembles it.</summary>
    [Theory]
    [InlineData("--verify")]
    [InlineData("--verify", "--comp")]
    [InlineData("--comp", "--live")]
    [InlineData("--comp", "--live", "--expiry", "2026-09-04")]
    public void The_command_lines_CI_builds_stay_accepted(params string[] args)
    {
        Assert.Empty(AgentArguments.Faults(args));
    }

    // ---- values, not just flag names ----

    [Theory]
    [InlineData("--expiry", "09/03/2026")]
    [InlineData("--expiry", "tomorrow")]
    [InlineData("--blackout-sessions", "two")]
    [InlineData("--blackout-sessions", "-1")]
    [InlineData("--as-of", "later")]
    public void A_value_that_does_not_parse_is_refused_rather_than_ignored(string flag, string value)
    {
        // The old parser dropped these silently, so --expiry 09/03/2026 quietly became
        // whatever the default expiry was.
        string fault = Assert.Single(AgentArguments.Faults([flag, value]));

        Assert.Contains(flag, fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valued_flag_with_nothing_after_it_is_refused()
    {
        // Previously invisible: every scan stopped one short of the end to leave room for
        // the value, so a trailing flag was read by nobody.
        string fault = Assert.Single(AgentArguments.Faults(["--comp", "--expiry"]));

        Assert.Contains("needs a value", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_swallowed_as_a_value_is_not_mistaken_for_a_symbol()
    {
        Assert.NotEmpty(AgentArguments.Faults(["--underlying", "--comp"]));
    }

    [Fact]
    public void Every_fault_on_the_line_is_reported_not_only_the_first()
    {
        IReadOnlyList<string> faults = AgentArguments.Faults(["--profile", "comp", "--dry-run"]);

        Assert.Equal(2, faults.Count);
    }

    // ---- the gate is wired, and ahead of anything that touches the broker ----

    [Fact]
    public void The_cycle_refuses_a_bad_command_line_before_it_selects_an_account()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "FRONTEND_CONTRACT.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string program = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "UnchartedOptions.Agent", "Program.cs"));

        int gate = program.IndexOf("AgentArguments.Faults(argv)", StringComparison.Ordinal);
        int profile = program.IndexOf("TradingProfile.FromArgs", StringComparison.Ordinal);
        int runner = program.IndexOf("new(profile: profile.CliProfile)", StringComparison.Ordinal);

        Assert.True(gate > 0, "the usage gate is not wired into the cycle");
        Assert.True(gate < profile, "the account is chosen before the command line is checked");
        Assert.True(gate < runner, "the broker is reachable before the command line is checked");
        Assert.Contains("return AgentArguments.UsageExitCode;", program, StringComparison.Ordinal);
    }
}
