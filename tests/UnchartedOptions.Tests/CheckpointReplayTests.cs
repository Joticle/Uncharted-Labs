using System.Text.RegularExpressions;

namespace UnchartedOptions.Tests;

/// <summary>
/// Every panel must describe the selected day, or say it cannot.
/// </summary>
/// <remarks>
/// The stage buttons replay the decision log, and most of the page followed them: the header
/// clock, the calendar state, the rejection stream and the gate ledger were all correct on an
/// earlier slice. The panels that did not follow read the live feed instead, so the 08.29 view
/// showed $100,000 equity -- right for that day -- beside "-$240 since funding", one closed
/// trade that did not exist until 09.02, a mean holding period derived from it, and a 766/771
/// spread at 0 DTE that was opened on 09.02 and expires on 09.03. The demo video clicks
/// through three of these days saying "same account, different days".
/// </remarks>
public class CheckpointReplayTests
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

    private static string Generator() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tools", "wire-design.py"));

    // ---- the four panels that were reading today ----

    [Fact]
    public void A_replayed_day_shows_no_book_rather_than_the_live_one()
    {
        string page = Page();

        Assert.Contains("const positions = asOf ? [] : spreadsFromBroker();", page, StringComparison.Ordinal);

        // The mapping that put the live book under a past date.
        Assert.DoesNotContain("open: num(p.unrealised),", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_closed_table_is_cut_at_the_checkpoint()
    {
        string page = Page();

        Assert.Contains("const closed = closedUpTo.map((c) => [", page, StringComparison.Ordinal);
        Assert.Contains("const closedUpTo = (feed.closed || []).filter((c) => {", page, StringComparison.Ordinal);

        // Whole-feed reads of the closed list are what produced a 09.02 exit on the 08.29 tab.
        Assert.DoesNotContain("const closed = (feed.closed || []).map", page, StringComparison.Ordinal);
    }

    /// <summary>Days to close is a mean over the closed table, so cutting that fixes both.</summary>
    [Fact]
    public void Wins_and_losses_are_recounted_from_the_trades_that_had_closed_by_then()
    {
        string page = Page();

        Assert.Contains("wins: asOf ? closedUpTo.filter((c) => c.win).length", page, StringComparison.Ordinal);
        Assert.Contains("losses: asOf ? closedUpTo.filter((c) => !c.win).length", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Equity_and_the_line_beneath_it_are_measured_from_the_same_day()
    {
        string page = Page();

        // eq = inception + realised + openPl, and the delta under the headline is eq minus
        // inception. Anchoring inception at funding and deriving unrealised from the day's
        // own equity is what stops $100,000 appearing above a change of -$240.
        Assert.Contains("const openPl = allDays.length", page, StringComparison.Ordinal);
        Assert.Contains("? equity - funding - realised", page, StringComparison.Ordinal);
        Assert.Contains("const inception = allDays.length ? funding : equity - realised - openPl;",
            page, StringComparison.Ordinal);

        // Solving for inception made the delta the sum of whatever the pairing recognised,
        // not the distance from the starting balance. On the final afternoon that put
        // $103,312 above "-$240 since funding": a partial close banks nothing FromFills can
        // see, because the spread's legs have not netted to zero, and an expired book carries
        // nothing either, so $3,552 of the account's own gain was invisible to both terms.
        Assert.DoesNotContain("const inception = asOf ? funding", page, StringComparison.Ordinal);
        Assert.DoesNotContain("openPl: asOf ? openPl : null,", page, StringComparison.Ordinal);
        Assert.Contains("const funding = allDays.length ? num(allDays[0].runs[0].equity) : 0;",
            page, StringComparison.Ordinal);

        // The funding note is a live string; on a replay it has to state that day's figure.
        Assert.Contains("fundingNote: asOf", page, StringComparison.Ordinal);
    }

    // ---- what replays exactly, and what cannot ----

    /// <summary>
    /// LogRun carries equity and both ceilings, so risk and exposure are exact for a past
    /// day even though the legs behind them are gone.
    /// </summary>
    [Fact]
    public void Risk_and_exposure_replay_from_the_logs_own_ceiling_ledger()
    {
        string page = Page();

        Assert.Contains("riskDeployed: asOf ? num((asOf.riskPerTrade || {}).deployedDollars) : null,",
            page, StringComparison.Ordinal);
        Assert.Contains("const totalRisk = d.riskDeployed != null", page, StringComparison.Ordinal);
        Assert.Contains("(asOf.symbolExposure || []).find((x) => x.label === s.n)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A day that deployed nothing held nothing -- exact, not inferred. A day that deployed
    /// something carries no count in the log, and the card must say so rather than borrow
    /// the live one.
    /// </summary>
    [Fact]
    public void An_unreconstructable_position_count_renders_the_placeholder()
    {
        string page = Page();

        Assert.Contains("? (num((asOf.riskPerTrade || {}).deployedDollars) === 0 ? 0 : null)",
            page, StringComparison.Ordinal);
        Assert.Contains("value: nPos == null ? '—' : String(nPos)", page, StringComparison.Ordinal);
        Assert.Contains("posHeading: nPos == null ? 'Open positions · —'", page, StringComparison.Ordinal);
        Assert.Contains("stateLine: (nPos == null ? '—'", page, StringComparison.Ordinal);

        // Every string that interpolates the count has to survive it being unknown. This one
        // did not, and rendered "3.02% of equity, across null spreads" on the 09.01 tab.
        Assert.Contains("+ (nPos == null ? '' : ', across ' + nPos + ' spreads')", page, StringComparison.Ordinal);
        Assert.DoesNotContain("' of equity, across ' + nPos + ' spreads',", page, StringComparison.Ordinal);
    }

    /// <summary>Nothing reaches the page by pasting a possibly-null count into a string.</summary>
    [Fact]
    public void No_rendered_string_interpolates_the_count_without_guarding_it()
    {
        string page = Page();

        foreach (Match m in Regex.Matches(page, @"\+ nPos\b"))
        {
            // The guard often sits a line above the interpolation, so look back over the
            // whole expression rather than the single line it happens to end on.
            int from = Math.Max(0, m.Index - 220);

            Assert.True(page[from..m.Index].Contains("nPos == null", StringComparison.Ordinal),
                $"nPos is interpolated unguarded: {page[from..(m.Index + 40)].Trim()}");
        }
    }

    [Fact]
    public void The_equity_curve_ends_at_the_checkpoint()
    {
        string page = Page();

        Assert.Contains("curve: asOf ? [funding, ...allDays.map((dd) => num(dayEnd(dd).equity))] : curve,",
            page, StringComparison.Ordinal);
        Assert.Contains("curveTo: asOf ? asOfDay : feed.curveTo,", page, StringComparison.Ordinal);
    }

    /// <summary>No live figure may reach a replayed panel through a default.</summary>
    [Theory]
    [InlineData("feed.wins")]
    [InlineData("feed.losses")]
    [InlineData("feed.preGate")]
    [InlineData("feed.curveTo")]
    [InlineData("feed.fundingNote")]
    [InlineData("feed.symbols")]
    public void Every_live_read_left_in_the_replay_path_is_guarded_by_asOf(string read)
    {
        string page = Page();

        foreach (Match m in Regex.Matches(page, Regex.Escape(read) + @"[^\n]*"))
        {
            // Each surviving read of the live feed must sit on the far side of an asOf test.
            int at = page.LastIndexOf("asOf", m.Index, StringComparison.Ordinal);

            Assert.True(at > 0 && m.Index - at < 400,
                $"'{read}' is read without a checkpoint guard in reach: {m.Value.Trim()}");
        }
    }

    // ---- the page and the generator that produces it ----

    /// <summary>
    /// index.html is generated. A hand edit that the generator does not also carry is undone
    /// the next time anyone regenerates the page, silently and in full.
    /// </summary>
    [Theory]
    [InlineData("const positions = asOf ? [] : spreadsFromBroker();")]
    [InlineData("const closed = closedUpTo.map((c) => [")]
    [InlineData("const inception = allDays.length ? funding : equity - realised - openPl;")]
    [InlineData("positionCount: asOf")]
    [InlineData("curveTo: asOf ? asOfDay : feed.curveTo,")]
    [InlineData("const totalRisk = d.riskDeployed != null")]
    [InlineData("value: nPos == null ? '—' : String(nPos)")]
    public void The_generator_carries_every_change_the_page_carries(string fragment)
    {
        Assert.Contains(fragment, Page(), StringComparison.Ordinal);
        Assert.Contains(fragment, Generator(), StringComparison.Ordinal);
    }
}
