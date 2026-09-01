using System.Text.Json;
using System.Text.RegularExpressions;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// Asserts the agent's execution recording, not the record type's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Ten tests in <c>ExecutionClaimTests</c> passed for three days beside a path that never set
/// <c>executed</c> at all. They construct a <see cref="Decision"/> with the flag already true
/// and assert the type behaves, which says nothing about whether the agent ever sets it. On
/// 1 Sep two live orders filled and both were recorded as hypothetical.
/// </para>
/// <para>
/// These tests target the seam and the call sites instead: the promotion function directly,
/// the source that must invoke it, and the invariant over the log the agent actually wrote.
/// </para>
/// </remarks>
public class ExecutionWiringTests
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

    private static Decision Candidate() => new()
    {
        Underlying = "SPY",
        Structure = "764C/769C",
        Verdict = Verdict.TAKEN,
        Gate = "sized",
        Finding = "delta 0.39 | 2.36:1",
    };

    // ---- the seam ----

    [Fact]
    public void An_order_id_promotes_the_decision_to_executed()
    {
        Decision promoted = DecisionLog.Executed(Candidate(), "7d88a1a8-f87c-4262-a541-164398f870f1");

        Assert.True(promoted.Executed);
        Assert.Equal("7d88a1a8-f87c-4262-a541-164398f870f1", promoted.OrderId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_order_id_the_decision_is_left_alone(string? orderId)
    {
        Decision promoted = DecisionLog.Executed(Candidate(), orderId);

        Assert.False(promoted.Executed);
        Assert.Equal(string.Empty, promoted.OrderId);
    }

    [Fact]
    public void Promotion_changes_nothing_else_about_the_decision()
    {
        Decision before = Candidate();
        Decision after = DecisionLog.Executed(before, "order-1");

        Assert.Equal(before.Verdict, after.Verdict);
        Assert.Equal(before.Gate, after.Gate);
        Assert.Equal(before.Finding, after.Finding);
        Assert.Equal(before.Structure, after.Structure);
    }

    // ---- the call sites ----

    /// <summary>
    /// The guard for the failure that actually happened. The promotion was written into the
    /// agent three times and silently failed to apply every time: the edit did not match, the
    /// build stayed green because nothing referenced what it would have added, and no test
    /// noticed. This asserts the call sites exist in the source.
    /// </summary>
    [Fact]
    public void Both_order_paths_record_the_result_of_submitting()
    {
        string program = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "UnchartedOptions.Agent", "Program.cs"));

        int calls = Regex.Matches(program, @"DecisionLog\.Executed\s*\(").Count;

        Assert.True(calls >= 2,
            $"Program.cs calls DecisionLog.Executed {calls} time(s); the open and close paths each need one. "
            + "A filled order recorded as hypothetical is the failure this guards.");

        Assert.Contains("DecisionLog.Executed(sizedRecord, submission.OrderId)", program, StringComparison.Ordinal);
        Assert.Contains("DecisionLog.Executed(exitRecord, close.OrderId)", program, StringComparison.Ordinal);
    }

    /// <summary>Nothing may set the flag by hand and bypass the seam.</summary>
    [Fact]
    public void The_agent_never_sets_executed_outside_the_seam()
    {
        string program = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "UnchartedOptions.Agent", "Program.cs"));

        Assert.DoesNotContain("Executed = true", program, StringComparison.Ordinal);
    }

    // ---- what the agent actually wrote ----

    /// <summary>
    /// The invariant over the real log: a decision claims execution exactly when it carries
    /// the order id that justifies it. Read from the file the agent emitted, not from a
    /// fixture.
    /// </summary>
    [Fact]
    public void Every_record_the_agent_has_written_pairs_executed_with_an_order_id()
    {
        string path = Path.Combine(RepoRoot(), "decisions", "decisions.jsonl");

        if (!File.Exists(path))
        {
            return; // Nothing written yet; the invariant has nothing to violate.
        }

        List<string> offenders = [];
        int records = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            string runId = doc.RootElement.GetProperty("runId").GetString() ?? "?";

            if (!doc.RootElement.TryGetProperty("decisions", out JsonElement decisions))
            {
                continue;
            }

            foreach (JsonElement d in decisions.EnumerateArray())
            {
                // Records predating the field are historical and carry neither key.
                if (!d.TryGetProperty("executed", out JsonElement ex)) continue;

                records++;
                bool executed = ex.GetBoolean();
                string orderId = d.TryGetProperty("orderId", out JsonElement o) ? (o.GetString() ?? "") : "";
                bool hasId = !string.IsNullOrWhiteSpace(orderId);

                if (executed != hasId)
                {
                    offenders.Add($"{runId} {d.GetProperty("structure").GetString()}: "
                                + $"executed={executed} orderId={(hasId ? "present" : "empty")}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} of {records} records claim execution without an id, or carry an id "
            + "without claiming it: " + string.Join("; ", offenders.Take(5)));
    }
}
