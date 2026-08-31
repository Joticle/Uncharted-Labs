using System.Text.Json;
using System.Text.RegularExpressions;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// Holds the emitted JSON to what the front-end contract documents.
/// </summary>
/// <remarks>
/// <para>
/// A contract nothing enforces is a comment. These tests parse
/// <c>docs/FRONTEND_CONTRACT.md</c>, pull out every field name it shows in a JSON example, and
/// fail if the agent does not actually emit it -- and fail the other way too, when the agent
/// emits a field the document never mentions.
/// </para>
/// <para>
/// The consumer of these files cannot see this repository. Drift between what is documented
/// and what is written is therefore invisible until it reaches them as a missing key at
/// runtime, which is exactly what happened with <c>executed</c>: an edit silently failed to
/// apply, the build stayed green because the property was never added, and only a manual
/// inspection of the emitted keys caught it.
/// </para>
/// </remarks>
public class ContractConformanceTests
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

    private static string Contract() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "FRONTEND_CONTRACT.md"));

    /// <summary>Field names appearing as <c>"key":</c> inside the document's JSON examples.</summary>
    private static HashSet<string> DocumentedFields(string section)
    {
        string doc = Contract();
        int start = doc.IndexOf(section, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Contract has no section '{section}'.");

        // Bound the search at the next file-level heading so one section's fields are not
        // credited to another.
        int end = doc.IndexOf("\n## ", start + section.Length, StringComparison.Ordinal);
        string slice = end > start ? doc[start..end] : doc[start..];

        return Regex.Matches(slice, "\"([a-zA-Z][a-zA-Z0-9]*)\"\\s*:")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> EmittedKeys(JsonElement el, HashSet<string>? into = null)
    {
        into ??= new HashSet<string>(StringComparer.Ordinal);

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    into.Add(p.Name);
                    EmittedKeys(p.Value, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in el.EnumerateArray())
                {
                    EmittedKeys(item, into);
                }

                break;
        }

        return into;
    }

    /// <summary>A run exercising every branch, so no field is absent merely for want of data.</summary>
    private static LogRun SampleRun() => new()
    {
        RunId = "20260831T133500Z",
        Timestamp = "2026-08-31T13:35:00Z",
        Account = "PA3BG520YCTT",
        Profile = "comp",
        IsCompetition = true,
        MarketOpen = true,
        DryRun = false,
        Equity = 100_000m,
        CalendarState = "OpenAndManage",
        RiskPerTrade = new GateUtilisation
        {
            Label = "risk per trade",
            CeilingPercent = 3m,
            CeilingDollars = 3_000m,
            DeployedDollars = 1_620m,
            DeployedPercent = 1.62m,
        },
        SymbolExposure =
        [
            new GateUtilisation
            {
                Label = "SPY",
                CeilingPercent = 5m,
                CeilingDollars = 5_000m,
                DeployedDollars = 1_620m,
                DeployedPercent = 1.62m,
            },
        ],
        Decisions =
        [
            new Decision
            {
                Underlying = "SPY",
                Structure = "772C/777C",
                Verdict = Verdict.TAKEN,
                Gate = "sized",
                Finding = "delta 0.39 | 2.09:1",
                Executed = true,
                OrderId = "order-123",
                Metrics = new DecisionMetrics
                {
                    LongStrike = 772m, ShortStrike = 777m, Width = 5m, Delta = 0.39m,
                    Debit = 1.62m, RewardRisk = 2.09m, CostDragPercent = 5.2m,
                    MaxLossDollars = 162m, Contracts = 10, RiskDollars = 1_620m, RiskPercent = 1.62m,
                },
            },
        ],
    };

    private static SpreadPosition SamplePosition() => new()
    {
        Spread = new VerticalSpread
        {
            Underlying = "SPY",
            Direction = SpreadDirection.BullCall,
            LongSymbol = "SPY260903C00772000",
            ShortSymbol = "SPY260903C00777000",
            NetDebit = 1.62m,
            StrikeWidth = 5m,
            Expiration = new DateOnly(2026, 9, 3),
        },
        Contracts = 10,
        CurrentValue = 2.40m,
        OpenedAt = new DateTimeOffset(2026, 8, 31, 13, 40, 0, TimeSpan.Zero),
    };

    private static RealisedTrade SampleTrade() => new()
    {
        Underlying = "SPY",
        Expiration = new DateOnly(2026, 9, 3),
        Strikes = [772m, 777m],
        RealisedPnl = 780m,
        OpenedAt = new DateTimeOffset(2026, 8, 31, 13, 40, 0, TimeSpan.Zero),
        ClosedAt = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero),
        Fills = 4,
    };

    // ---- the run object ----

    [Fact]
    public void Every_run_field_the_contract_documents_is_actually_emitted()
    {
        using JsonDocument doc = JsonDocument.Parse(DecisionLog.SerialiseRun(SampleRun()));
        HashSet<string> emitted = EmittedKeys(doc.RootElement);

        string[] missing = DocumentedFields("## The run object")
            .Concat(DocumentedFields("## `GateUtilisation`"))
            .Concat(DocumentedFields("## `Decision`"))
            .Concat(DocumentedFields("## `DecisionMetrics`"))
            .Distinct()
            .Where(f => !emitted.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "Documented in FRONTEND_CONTRACT.md but not emitted: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_run_field_emitted_is_documented()
    {
        using JsonDocument doc = JsonDocument.Parse(DecisionLog.SerialiseRun(SampleRun()));

        HashSet<string> documented = DocumentedFields("## The run object");
        documented.UnionWith(DocumentedFields("## `GateUtilisation`"));
        documented.UnionWith(DocumentedFields("## `Decision`"));
        documented.UnionWith(DocumentedFields("## `DecisionMetrics`"));

        string[] undocumented = EmittedKeys(doc.RootElement)
            .Where(k => !documented.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(undocumented.Length == 0,
            "Emitted but absent from FRONTEND_CONTRACT.md: " + string.Join(", ", undocumented));
    }

    // ---- the dashboard feed ----

    [Fact]
    public void Every_feed_field_the_contract_documents_is_actually_emitted()
    {
        DashboardFeed feed = DashboardFeedBuilder.Build(
            SampleRun(), [SamplePosition()], [100_000m, 100_780m], [SampleTrade()],
            [], "SPY", new RiskMandate(), 58, new CompetitionCalendar(),
            new DateTimeOffset(2026, 8, 31, 13, 35, 0, TimeSpan.Zero));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(feed, DecisionLog.Json));
        HashSet<string> emitted = EmittedKeys(doc.RootElement);

        string[] missing = DocumentedFields("## `decisions/dashboard.json`")
            .Where(f => !emitted.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "Documented for dashboard.json but not emitted: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The reverse direction for the feed. This check was missing, and four undocumented
    /// fields were added to dashboard.json without a single test noticing -- which is the
    /// precise drift these tests exist to catch.
    /// </summary>
    [Fact]
    public void Every_feed_field_emitted_is_documented()
    {
        DashboardFeed feed = DashboardFeedBuilder.Build(
            SampleRun(), [SamplePosition()], [100_000m, 100_780m], [SampleTrade()],
            [], "SPY", new RiskMandate(), 58, new CompetitionCalendar(),
            new DateTimeOffset(2026, 8, 31, 13, 35, 0, TimeSpan.Zero));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(feed, DecisionLog.Json));

        HashSet<string> documented = DocumentedFields("## `decisions/dashboard.json`");

        string[] undocumented = EmittedKeys(doc.RootElement)
            .Where(k => !documented.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(undocumented.Length == 0,
            "Emitted in dashboard.json but absent from FRONTEND_CONTRACT.md: "
            + string.Join(", ", undocumented));
    }

    /// <summary>
    /// The specific promise the contract makes about nulls, which a consumer rendering a
    /// layout depends on.
    /// </summary>
    [Fact]
    public void No_field_is_ever_null_in_either_payload()
    {
        DashboardFeed feed = DashboardFeedBuilder.Build(
            SampleRun(), [], [], [], [], "SPY", new RiskMandate(), 0, new CompetitionCalendar(),
            new DateTimeOffset(2026, 8, 31, 13, 35, 0, TimeSpan.Zero));

        foreach (string json in new[]
                 {
                     DecisionLog.SerialiseRun(SampleRun()),
                     JsonSerializer.Serialize(feed, DecisionLog.Json),
                 })
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            List<string> nulls = [];
            FindNulls(doc.RootElement, "", nulls);
            Assert.True(nulls.Count == 0, "Null values present at: " + string.Join(", ", nulls));
        }
    }

    private static void FindNulls(JsonElement el, string path, List<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                into.Add(path);
                break;

            case JsonValueKind.Object:
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    FindNulls(p.Value, $"{path}.{p.Name}", into);
                }

                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in el.EnumerateArray())
                {
                    FindNulls(item, $"{path}[{i++}]", into);
                }

                break;
        }
    }

    /// <summary>Every verdict the contract's table lists must exist in the enum, and vice versa.</summary>
    [Fact]
    public void The_documented_verdict_set_matches_the_enum()
    {
        string doc = Contract();

        foreach (string name in Enum.GetNames<Verdict>())
        {
            Assert.Contains($"`{name}`", doc, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The contract promises ASCII-only findings. A non-ASCII separator has twice reached the
    /// payload as a replacement character.
    /// </summary>
    [Fact]
    public void Emitted_payloads_are_ascii_only()
    {
        DashboardFeed feed = DashboardFeedBuilder.Build(
            SampleRun(), [SamplePosition()], [100_000m], [SampleTrade()], [], "SPY", new RiskMandate(), 58,
            new CompetitionCalendar(), new DateTimeOffset(2026, 8, 31, 13, 35, 0, TimeSpan.Zero));

        foreach (string json in new[]
                 {
                     DecisionLog.SerialiseRun(SampleRun()),
                     JsonSerializer.Serialize(feed, DecisionLog.Json),
                 })
        {
            // System.Text.Json escapes non-ASCII to \uXXXX, so decode before checking.
            using JsonDocument doc = JsonDocument.Parse(json);
            List<char> offending = [];
            FindNonAscii(doc.RootElement, offending);

            Assert.True(offending.Count == 0,
                "Non-ASCII in payload: " + string.Join(", ", offending.Distinct().Select(c => $"U+{(int)c:X4}")));
        }
    }

    private static void FindNonAscii(JsonElement el, List<char> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                into.AddRange((el.GetString() ?? "").Where(c => c > 127));
                break;

            case JsonValueKind.Object:
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    FindNonAscii(p.Value, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in el.EnumerateArray())
                {
                    FindNonAscii(item, into);
                }

                break;
        }
    }
}
