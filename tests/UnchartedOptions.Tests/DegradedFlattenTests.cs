using System.Text.RegularExpressions;
using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

/// <summary>
/// A book the agent cannot reconstruct must still be closable.
/// </summary>
/// <remarks>
/// Reconstruction refusing an unaccountable book is the intent: a stray leg means something
/// happened that nothing evaluated. But the throw originally sat upstream of the exit ladder,
/// the close and the log write, so an unpairable leg on the Thursday would have blocked the
/// flatten and left the dashboard showing the previous cycle with no sign of failure. Stopping
/// new entries is the point; stopping the close is the opposite of it.
/// </remarks>
public class DegradedFlattenTests
{
    private static string Program() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "UnchartedOptions.Agent", "Program.cs"));

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

    // ---- the halt is caught, not fatal to the cycle ----

    [Fact]
    public void The_cycle_catches_a_refused_reconstruction_rather_than_dying_on_it()
    {
        string program = Program();

        Assert.Contains("catch (LegConservationException", program, StringComparison.Ordinal);

        // The catch must sit around the reconstruction, not at the very bottom of the cycle:
        // a top-level handler would still skip the ladder, the close and the log write.
        int tryAt = program.IndexOf("heldSpreads = SpreadReconstruction.FromLegs", StringComparison.Ordinal);
        int catchAt = program.IndexOf("catch (LegConservationException", StringComparison.Ordinal);
        int appendAt = program.IndexOf("DecisionLog.Append(", StringComparison.Ordinal);

        Assert.True(tryAt > 0 && catchAt > tryAt, "the catch must follow the reconstruction it guards");
        Assert.True(catchAt < appendAt, "the log write must still be reachable after a halt");
    }

    /// <summary>The halt has to be visible on the dashboard, not only in a CI log.</summary>
    [Fact]
    public void A_halt_emits_a_decision_record_so_the_stream_shows_it()
    {
        string program = Program();

        Assert.Contains("reconstruction-halt", program, StringComparison.Ordinal);

        // Between catching and writing the log there must be a decision added, or a halted
        // cycle renders identically to a quiet one.
        int catchAt = program.IndexOf("catch (LegConservationException", StringComparison.Ordinal);
        int haltGate = program.IndexOf("reconstruction-halt", StringComparison.Ordinal);
        int appendAt = program.IndexOf("DecisionLog.Append(", StringComparison.Ordinal);

        Assert.InRange(haltGate, catchAt, appendAt);
    }

    // ---- the close still runs ----

    [Fact]
    public void A_refused_reconstruction_still_reaches_the_flatten()
    {
        string program = Program();

        int catchAt = program.IndexOf("catch (LegConservationException", StringComparison.Ordinal);
        int degradedAt = program.IndexOf("degraded-flatten", StringComparison.Ordinal);

        Assert.True(degradedAt > catchAt,
            "the degraded flatten must be reachable after reconstruction has refused the book");
        Assert.Contains("cli.ClosePositionAsync", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering that makes a leg-by-leg close safe. Buying the shorts back first leaves a
    /// long-only book, bounded by premium already paid. Selling the longs first would leave
    /// the shorts uncovered -- a naked short call, created by the very operation meant to
    /// remove risk.
    /// </summary>
    [Fact]
    public void The_degraded_close_takes_shorts_before_longs()
    {
        string program = Program();

        Assert.Contains("OrderBy(l => l.Quantity > 0)", program, StringComparison.Ordinal);

        List<OpenPosition> book =
        [
            Leg("SPY260903C00764000", 30m),
            Leg("SPY260903C00769000", -20m),
            Leg("SPY260903C00774000", -10m),
        ];

        // The same ordering the agent applies: false sorts before true, so shorts lead.
        List<OpenPosition> ordered = [.. book.OrderBy(l => l.Quantity > 0)];

        Assert.All(ordered.Take(2), l => Assert.True(l.Quantity < 0));
        Assert.True(ordered[^1].Quantity > 0);

        // No prefix of the sequence may leave a short uncovered by a long.
        decimal longsLeft = book.Where(l => l.Quantity > 0).Sum(l => l.Quantity);
        decimal shortsLeft = book.Where(l => l.Quantity < 0).Sum(l => Math.Abs(l.Quantity));

        foreach (OpenPosition leg in ordered)
        {
            if (leg.Quantity < 0)
            {
                shortsLeft -= Math.Abs(leg.Quantity);
            }
            else
            {
                longsLeft -= leg.Quantity;
            }

            Assert.True(longsLeft >= shortsLeft,
                $"after closing {leg.Symbol} the book holds {shortsLeft} short against {longsLeft} long");
        }
    }

    [Fact]
    public void The_degraded_close_never_runs_outside_a_live_flatten()
    {
        string program = Program();
        int at = program.IndexOf("// Degraded flatten.", StringComparison.Ordinal);
        Assert.True(at > 0);

        string block = program[at..(at + 1400)];

        // Guarded on all four: a refused reconstruction, a calendar, an open market, and a
        // permission state that actually demands flattening.
        Assert.Contains("reconstructionFault is not null", block, StringComparison.Ordinal);
        Assert.Contains("activeCalendar is not null", block, StringComparison.Ordinal);
        Assert.Contains("clock.IsOpen", block, StringComparison.Ordinal);
        Assert.Contains("TradingPermission.FlattenAll", block, StringComparison.Ordinal);

        // position close has no dry-run mode, so it must be unreachable outside a live run.
        Assert.Matches(new Regex(@"if\s*\(!live\)", RegexOptions.None), block);
    }

    [Fact]
    public void A_degraded_close_records_the_order_id_like_any_other_execution()
    {
        string program = Program();
        int at = program.IndexOf("// Degraded flatten.", StringComparison.Ordinal);
        string block = program[at..(at + 1800)];

        Assert.Contains("DecisionLog.Executed(", block, StringComparison.Ordinal);
        Assert.Contains("Verdict.CLOSED", block, StringComparison.Ordinal);
    }

    private static OpenPosition Leg(string symbol, decimal qty) => new()
    {
        Symbol = symbol,
        Underlying = OccSymbol.Underlying(symbol)!,
        IsOption = true,
        Quantity = qty,
        CostBasis = qty * 100m,
        MarketValue = qty * 100m,
        UnrealizedPl = 0m,
    };
}
