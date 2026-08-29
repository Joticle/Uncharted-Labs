# Uncharted Options

Autonomous defined-risk options trading agent on Alpaca.

## The problem

Autonomous trading agents have a trust problem: every risk limit is a line of code, and code
fails. A stop-loss is an instruction, not a guarantee — it gaps through on a bad open, and it
doesn't exist at all if the agent that was supposed to place it has a bug.

## The approach

Uncharted Options moves the risk limit out of the software and into the instrument. It trades only
defined-risk vertical spreads, where maximum loss per position is fixed when the order is
constructed and enforced by the broker — not by the agent's control flow. A 3% risk budget
stops being a rule the agent has to remember and becomes a property of what it holds.

The practical consequence is that position sizing is exact rather than estimated. A
share-based sizer approximates risk as `entry - stop` and hopes the stop fills near that
price. Here the denominator is known with certainty before the order is sent.

## The gates

Every gate answers a documented retail failure mode rather than a preference.

| Gate | Value | Why |
|---|---|---|
| **The 3** — risk per trade | 3% of equity | Hard cap on any single position |
| **The 5** — symbol exposure | 5% of equity | Nets existing exposure, so one symbol can't be re-entered on every signal |
| **The 7** — reward:risk floor | 1.5:1 | A vertical's payoff is capped at `width - debit`; the inherited 7:1 would reject nearly every tradeable spread |
| Delta band | 0.35–0.45 | Keeps selection in the liquid part of the chain |
| Relative spread cap | 10% of mid | Cost drag on wide bid-ask is a primary driver of retail multi-leg losses |

A 2025 University of Florida study found retail traders lost money in every measured period
trading complex multi-leg options, averaging 16.4% over three days. The attributed
mechanisms — earnings-window timing, wide bid-ask on illiquid strikes, and cost drag — are
what the delta band and spread cap exist to avoid. The claim is not that spreads make retail
profitable; it's that the losses concentrate in behaviours a gated agent does not exhibit.

## Architecture

```
src/UnchartedOptions.Core     domain: account, spreads, the 3-5-7 gates, chain selection
src/UnchartedOptions.Alpaca   CLI adapter: account, clock, chains, multi-leg orders
src/UnchartedOptions.Agent    single-shot entrypoint — one evaluation cycle, then exit
tests/UnchartedOptions.Tests  the mandate, exhaustively
```

The agent is single-shot by design: it holds no state between runs, so the same binary runs
identically under cron, a scheduler, or a terminal. **The broker is the state** — positions
and orders are read back from Alpaca rather than mirrored in a database. That's the same
principle as the risk model.

### Two decisions worth calling out

**The account model has no `buying_power` property.** Alpaca returns five adjacent balance
fields and four are wrong for sizing:

```
equity                        100,000   <- the only correct sizing base
buying_power                  400,000   4x margin
regt_buying_power             200,000
options_buying_power          100,000   not leveraged
non_marginable_buying_power   100,000
```

`buying_power` sorts first alphabetically and is what an autocomplete lands on. Binding
sizing to it silently quadruples every position with nothing thrown. The footgun is removed
from the model rather than documented in a comment, and a regression test asserts that a
4x-margin account still sizes off equity.

**Orders go through `ProcessStartInfo.ArgumentList`, never a shell.** The multi-leg `--legs`
argument is a JSON array; passing it through PowerShell requires escaping every interior
quote, and getting it wrong yields a misleading parser error. Discrete argument passing
sidesteps shell interpretation entirely.

## Running it

Requires the [Alpaca CLI](https://github.com/alpacahq/cli) authenticated against a paper
account, and .NET 10.

```bash
dotnet run --project src/UnchartedOptions.Agent          # dry run — broker validates, places nothing
dotnet run --project src/UnchartedOptions.Agent --live   # places the order
dotnet test                                      # the mandate
```

A dry run exercises the entire path — account, clock, chain, selection, sizing, order
construction and broker validation — without creating an order. It is the integration test.

## Status

Built for the [lablab.ai × Alpaca AI Trading Agents Hackathon](https://lablab.ai),
28 Aug – 4 Sept 2026. Paper trading only.

## Licence

MIT.
