# Uncharted Options

**Autonomous defined-risk options trading agent on Alpaca.**
Built by Uncharted Labs for the lablab.ai × Alpaca AI Trading Agents Hackathon, 28 Aug – 4 Sep 2026.

---

## The thesis: risk containment is a property of the instrument, not of agent code

Autonomous trading agents have a trust problem. Every risk limit is a line of code, and code
fails. A stop-loss is an instruction, not a guarantee — it gaps through on a bad open, and it
does not exist at all if the agent that was supposed to place it has crashed, hung, or shipped
with a bug.

Uncharted Options moves the risk limit out of the software and into the instrument. It trades
only **defined-risk vertical spreads**, where maximum loss per position is fixed at the moment
the order is constructed and enforced by the broker's own position accounting. A 3% risk budget
stops being a rule the agent has to remember and becomes a property of what it holds.

The practical consequence is that sizing is **exact rather than estimated**. A share-based sizer
approximates risk as `entry − stop` and then hopes the stop fills near that price. Here the
denominator is known with certainty before the order is sent.

---

## Pre-event work disclosure

**Portions of this agent's risk logic and signal generation are adapted from a private codebase
in development since January 2026 by the same author.** Per the hackathon FAQ, reuse of
pre-existing work is permitted with disclosure.

| Adapted from prior work | Written for this project |
|---|---|
| The 3% risk and 5% symbol-exposure gates | The options expression of both gates |
| The exit-hierarchy concept and its stage ordering | The spread-based ladder, and the deletion of two stages |
| Position-sizing arithmetic | The vertical-spread constructor |
| — | All Alpaca CLI integration, chain selection, and the decision log |

The prior codebase is private and is not published here. No file from it is present in this
repository. The work was re-implemented against a written specification rather than copied.

---

## The doctrine: 3 and 5

The doctrine originates as an equity strategy — 3% risk, 5% exposure, 7:1 minimum
reward-to-risk. The 7:1 target is a meaningful constraint in equities, where such asymmetry is
rare. **It does not translate to options**: a spread paying 7:1 is priced at roughly a 12%
chance of paying, so forcing that ratio buys low-probability structure rather than edge. The
options expression therefore ports the 3% and 5% gates and replaces the reward target with
liquid 35–45 delta strike selection.

| Gate | Value | Why |
|---|---|---|
| **The 3** — risk per trade | 3% of equity | Hard cap on any single position |
| **The 5** — symbol exposure | 5% of equity | Nets existing positions, so one underlying can't be re-entered on every signal |
| Delta band | 0.35 – 0.45 | Keeps selection in the liquid part of the chain |
| Cost-drag ceiling | 15% of fair value | Crossing cost, measured against the mid-to-mid debit |
| Reward floor | 1.5 : 1 | A weak filter, not doctrine — rejects spreads whose debit isn't worth their payoff |
| Blackout | 3 sessions either side | Earnings and ex-dividend |

A 2025 University of Florida study found retail traders lost money in **every** measured period
trading complex multi-leg options, averaging 16.4% over three days and roughly three times worse
around earnings. The attributed mechanisms — earnings-window timing, wide bid-ask on illiquid
strikes, and cost drag — are exactly what the delta band, the blackout, and the cost-drag ceiling
exist to avoid. The claim is not that spreads make retail profitable. It is that the losses
concentrate in behaviours a gated agent does not exhibit.

---

## Five decisions worth reading

These are the places where the thesis either held, or would have quietly failed.

### 1. Pin risk is refused, not optimised

A vertical held through expiry settles cleanly only when the underlying finishes clear of **both**
strikes — above both, everything exercises and offsets; below both, everything expires worthless.
Between them, the long leg is exercised and the short is not.

On a 7-contract position at a 772 strike, that leaves roughly **$540,000 of stock against a
$100,000 account.** That is not an unpredictable settlement. It is a position the account cannot
carry.

So the agent refuses the zone rather than optimising within it: any position within $1.50 of
either strike on the last day before expiry is closed. Positions clear of both strikes are left
alone, because their settlement is clean — tested both ways, so the rule doesn't cost the good
outcomes. This is the agent's most concrete answer to robustness of the trading workflow.

### 2. The exit ladder's first stage was deleted

The predecessor's ladder opened with a gap-risk hard exit: an emergency check for an overnight
move blowing through a stop. **On a defined-risk vertical there is nothing for it to do.** The
position cannot lose more than the debit however violently the underlying gaps, and no code needs
to run for that bound to hold.

The ladder is shorter because the instrument already did the first job. That is the thesis
demonstrated rather than asserted.

### 3. The trailing stop was deleted rather than adding state

A trailing stop needs a high-water mark carried between runs — state the agent owns, that can be
lost, corrupted, or drift out of agreement with the broker. The claim of this design is that the
risk limit does not live in software state, and **a single persisted file would be the one
exception in a design asserting there are none.** A judge who reads "the broker is the source of
truth" and then finds a state file the agent depends on has found the seam.

Take-profit at 65% of max and a stop at 50% of the debit already bracket the outcome, so the stage
went rather than the claim. A regression test enforces it:

```csharp
Assert.DoesNotContain("Trailing", Enum.GetNames<ExitReason>());
// the same situation, evaluated twice, cannot disagree
```

If a future stage needs memory, that test fails and the seam gets noticed rather than shipped.

### 4. Closing is atomic, and that is not a convenience

`alpaca position close` takes a single symbol. Unwinding a spread leg by leg leaves a window in
which the long leg has been sold and the short has not — **a naked short call.** That is the one
moment the bounded-loss claim is false, during the exact operation meant to realise that bound.

Closes therefore go out as a single `mleg` order with `sell_to_close` and `buy_to_close`. The
atomicity that makes the risk defined on entry is what makes it defined on exit.

### 5. The account model has no `buying_power` property

Alpaca returns five adjacent balance fields, and four are wrong for sizing:

```
equity                        100,000   <- the only correct sizing base
buying_power                  400,000   4x margin
regt_buying_power             200,000
options_buying_power          100,000   not leveraged
non_marginable_buying_power   100,000
```

`buying_power` sorts first alphabetically and is what an autocomplete lands on. **A sizing
function reading the wrong field turns a 3% gate into 12% silently** — nothing throws, nothing
logs, and the position is four times the size the mandate authorised.

The model therefore exposes `Equity` only and keeps raw buying power off the object entirely.
The footgun is removed rather than documented. A regression test asserts that a 4x-margin account
sizes to 30 contracts, not 120.

Alpaca's own schema now documents `portfolio_value` as **deprecated, "equivalent to the equity
field"** — which is precisely the field the predecessor codebase had built its sizing on.

---

## Why the CLI, and not MCP or an SDK

Alpaca's guidance asks for a justification if an SDK is used. This project uses none, but the
reasoning is worth stating:

- **The CLI is a language-neutral execution boundary.** Orders leave the process as an argument
  vector and come back as JSON on stdout. Nothing about the agent's correctness depends on a
  client library's serialisation of an order.
- **JSON over stdout is inspectable.** Every call the agent makes can be replayed by hand in a
  terminal, which is how the multi-leg wire format was verified before a line of adapter code
  existed.
- **`--dry-run` is a real integration test.** The broker validates and echoes an order without
  creating it, so the entire path — account, chain, selection, sizing, construction, submission —
  can be exercised against a live account without placing a trade.
- **Pinned at `v0.0.13`.** The tool is alpha. A flag rename mid-contest is unrecoverable, so the
  version is fixed and the wire format is pinned by test.

MCP would put a language model in the order path. For an agent whose entire claim is that the
risk limit is structural rather than inferential, that is the wrong place for one to sit.

---

## Architecture

```
src/UnchartedOptions.Core     domain: account, spreads, the 3-5 gates, chain selection,
                              exit ladder, blackout calendar, decision log
src/UnchartedOptions.Alpaca   CLI adapter: account, clock, chains, positions, multi-leg orders
src/UnchartedOptions.Agent    single-shot entrypoint — one evaluation cycle, then exit
tests/UnchartedOptions.Tests  the mandate, exhaustively
```

The agent is **single-shot**: it holds no state between runs, so the same binary runs identically
under GitHub Actions cron, a local scheduler, or a terminal. **The broker is the state** —
positions, fills and open orders are read back from Alpaca rather than mirrored locally. Entry
times for the time stop come from the broker's own fill records rather than being remembered.

### The decision log

The one artifact the broker cannot provide. Broker data shows the positions that were *opened*;
it cannot show the candidates that were *declined*, which is the whole demonstration that a
mandate is enforced rather than described.

```
SPY  $10 width    REJECTED  782C quote 10.5 % wide
SPY  $15 width    REJECTED  787C quote 18.2 % wide
SPY  772C/777C    TAKEN     delta 0.39 · 2.09:1 · $162.00 max loss · 1.62% of equity
```

Written to `decisions/` as JSON Lines, one run per line, plus a `latest.json` snapshot. It is
**not agent state**: nothing reads it back, and deleting it changes no decision the agent would
make. Evaluation runs even when trading is barred, so a blackout day still records what *would*
have been refused and on which gate.

### Two accounts, with a guard

The competition account must not carry test orders. The dev profile is the default everywhere;
`--comp` is required to reach the judged account; and orders against it are **refused outright**
before the opening bell rather than left to discipline. Each profile is pinned to the account
number it must resolve to, because authenticating the wrong account under the name `comp` would
look entirely normal — both accounts hold the same balance — until the agent traded the wrong book.

---

## Running it

Requires [.NET 10](https://dotnet.microsoft.com/) and the
[Alpaca CLI](https://github.com/alpacahq/cli) authenticated against a paper account.

```bash
dotnet test                                                     # the mandate
dotnet run --project src/UnchartedOptions.Agent -- --verify     # account configuration
dotnet run --project src/UnchartedOptions.Agent -- --preflight  # readiness before the open
dotnet run --project src/UnchartedOptions.Agent                 # dry run: validates, places nothing
dotnet run --project src/UnchartedOptions.Agent -- --live       # places orders
```

| Flag | Effect |
|---|---|
| `--comp` | Target the judged competition account |
| `--live` | Actually place and close orders |
| `--expiry YYYY-MM-DD` | Target expiry (default: the final scored day) |
| `--earnings SYM:YYYY-MM-DD,…` | Earnings blackout dates |
| `--underlying SYM` | Underlying to trade |

A dry run exercises the entire path without creating an order. It is the integration test.

**Earnings dates are supplied manually.** Alpaca's corporate-actions endpoint carries cash
dividends, splits, mergers and spin-offs — it does not publish earnings. Rather than ship a gate
that silently never fires, earnings dates are explicit and every blackout verdict names its
source. Ex-dividend dates do come from Alpaca and populate automatically.

---

## Licence

MIT.
