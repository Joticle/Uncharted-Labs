# Decision log — front-end contract

What the trading agent writes, and what a consumer can rely on. Written for someone wiring a
dashboard who cannot see the agent's source.

## Files

| Path | Shape | Use |
|---|---|---|
| `decisions/latest.json` | One **run object**, pretty-printed | Current state. Poll or re-read on page load. |
| `decisions/decisions.jsonl` | **JSON Lines** — one run object per line, appended | History. Read the last N lines for a timeline. |

Both files hold the *same* object type. `latest.json` is a copy of the final line of
`decisions.jsonl`. Parse `.jsonl` by splitting on newlines and `JSON.parse` each non-empty
line — it is **not** a JSON array, and the file has no enclosing brackets.

Runs are appended in chronological order. Sort by `timestamp` if you need certainty.

---

## The run object

```jsonc
{
  "runId": "20260829T074939Z",          // string, unique, sortable
  "timestamp": "2026-08-29T07:49:39Z",  // string, ISO-8601 UTC, always trailing Z
  "account": "PA3ILISQPBT4",            // string
  "profile": "paper",                   // string: "paper" (dev) | "comp" (judged)
  "isCompetition": false,               // boolean — use this, not the profile name
  "marketOpen": false,                  // boolean
  "equity": 100000,                     // number, USD
  "calendarState": "OpenAndManage",     // string enum, see below
  "riskPerTrade":  { GateUtilisation },
  "symbolExposure": [ GateUtilisation ],
  "decisions": [ Decision ]
}
```

### `calendarState`

One of exactly these strings. Treat an unrecognised value as `OpenAndManage` rather than
erroring — new states may be added.

| Value | Meaning |
|---|---|
| `BeforeCompetitionOpens` | Contest has not started. No orders. |
| `OpenAndManage` | Normal operation. |
| `ManageOnly` | Past the entry cutoff. Closing only, no new positions. |
| `FlattenAll` | Everything must be flat except contracts held to expiry. |
| `Closed` | Contest over. |

---

## `GateUtilisation`

A ceiling and how much of it is used. **Both numerator and denominator are supplied, plus a
precomputed fraction.** The consumer never has to divide.

```jsonc
{
  "label": "risk per trade",   // string. For symbolExposure this is the ticker, e.g. "SPY"
  "ceilingPercent": 3.00,      // number — the gate, as a percent of equity
  "ceilingDollars": 3000.00,   // number — the same gate in dollars
  "deployedDollars": 1620.00,  // number — capital currently at risk
  "deployedPercent": 1.62,     // number — the same, as a percent of equity
  "utilisation": 0.54          // number 0..1, already clamped. Use for a fill bar.
}
```

`utilisation` is clamped to a maximum of 1.0 — a bar cannot overflow. If you want to show a
breach, compare `deployedDollars > ceilingDollars` rather than looking for `utilisation > 1`.

`riskPerTrade` is a single object. `symbolExposure` is an **array with one entry per underlying
currently held** — see the empty state below.

---

## `Decision`

```jsonc
{
  "underlying": "SPY",            // string, always present and non-empty
  "structure": "772C/777C",       // string — may be EMPTY, see below
  "verdict": "TAKEN",             // "TAKEN" | "REJECTED" | "SKIPPED"
  "gate": "sized",                // string, see the gate list
  "finding": "delta 0.39 | 2.09:1 | $162.00 max loss | 1.62% of equity",
  "metrics": { DecisionMetrics }
}
```

### `verdict`

| Value | Meaning | Render as |
|---|---|---|
| `TAKEN` | Position opened | Positive / accent |
| `REJECTED` | A spread was constructed and a gate declined it | Neutral, **not** an error |
| `SKIPPED` | Never evaluated — blacked out, or outside the trading window | Muted |
| `CLOSED` | An open position was unwound by the exit ladder | Positive / accent |
| `HELD` | An open position was evaluated and left alone | Muted |

`CLOSED` and `HELD` appear only when positions exist. On `CLOSED` and `HELD` the `gate` field
carries the exit stage — `PinRisk`, `StopLoss`, `TakeProfit`, `TimeStop`, `CompetitionFlatten`
or `None` — rather than an entry gate.

**`REJECTED` is the hero content, not a failure.** It is the evidence the mandate is enforced.
Do not style it as an error state.

### `structure`

Either `"772C/777C"` (long strike / short strike) or `"$10 width"` when a width was evaluated but
no spread formed, or **an empty string `""`** when the decision concerns the underlying as a whole
(a blackout, or a closed window). Never null.

Guard on `structure !== ""` before rendering it as a strike pair.

### `gate`

Free-form string, but the current set is:

`sized` · `cost-drag` · `reward-floor` · `liquidity` · `no-short-leg` · `delta-band` ·
`malformed-spread` · `blackout` · `competition-calendar` · `RiskPerTrade` · `SymbolExposure` ·
`Affordability` · `OrderCeiling` · `BelowMinimumSize`

Treat it as an opaque label — display it, don't branch on it exhaustively. New gates get added.

### `finding`

Human-readable, already formatted, **ASCII only**, pipe-separated where it has parts. Safe to
render directly. Length varies from ~20 to ~140 characters — do not assume a single line.

---

## `DecisionMetrics`

**Every field is always present and always a number. There are no nulls.** A field that does not
apply is `0`.

```jsonc
{
  "longStrike": 772,          "shortStrike": 777,
  "width": 5,                 "delta": 0.39,
  "debit": 1.62,              "rewardRisk": 2.09,
  "costDragPercent": 5.2,     "maxLossDollars": 162.00,
  "contracts": 10,            "riskDollars": 1620.00,
  "riskPercent": 1.62
}
```

| Field | Unit | Note |
|---|---|---|
| `longStrike`, `shortStrike` | USD | `0` when no spread formed |
| `width` | USD | Strike distance |
| `delta` | 0–1 | Of the long leg. `0` means not applicable, never a real zero-delta strike |
| `debit` | USD per share | Multiply by 100 for per-contract |
| `rewardRisk` | ratio | `2.09` means 2.09 : 1 |
| `costDragPercent` | percent | Crossing cost against fair value. Already ×100 |
| `maxLossDollars` | USD | **Per contract.** Fixed at construction |
| `contracts` | count | |
| `riskDollars` | USD | Total at risk = `maxLossDollars × contracts` |
| `riskPercent` | percent | Of equity. Already ×100 |

Percent fields are **already multiplied by 100**. `1.62` means 1.62%, not 162%.

---

## Empty states — read this before designing

At the Monday open the agent has no positions, no closed trades, and one decision. The dashboard
must look like a working instrument at that moment, not an unfinished page.

### Zero positions

`symbolExposure` is `[]`. This is the **normal** state before the first fill and after the
Thursday flatten — it is not an error and not a loading state.

`riskPerTrade` is still fully populated with `deployedDollars: 0` and `utilisation: 0`. Render
the gate bars at zero with their ceilings visible: *"$0 of $3,000 deployed"*. An empty bar with a
labelled ceiling reads as a working instrument; a hidden panel reads as broken.

Do **not** render "no data" for `symbolExposure: []`. Render the 5% ceiling with nothing against
it, or a short line such as *"No underlyings held."*

### Zero or one decision

`decisions` is never absent, but may hold a single entry. On a barred day that entry is often a
`SKIPPED` with `structure: ""`.

There is no state where `decisions` is `[]` in practice, but treat it defensively: an empty array
means the agent ran and reached no conclusion, which is worth showing as *"Cycle ran, nothing to
evaluate"* rather than a blank panel.

### No file at all

Before the first run, `decisions/` may not exist. Handle a 404 on both files as *"Agent has not
run yet"*, distinct from an empty result.

### Zero P&L

`decisions/latest.json` carries **no P&L or position-value fields**. It records decisions, not
performance.

Performance lives in a third file, **`decisions/dashboard.json`**, written by the same run.

---

## Stability guarantees

- Field **names** and **types** will not change during the contest.
- New fields may be **added**. Ignore unknown keys.
- New `gate` values and `calendarState` values may appear. Do not exhaustively switch.
- `timestamp` is always ISO-8601 UTC with a trailing `Z`.
- Numbers are JSON numbers, never strings. Money is rounded to 2 decimal places, `delta` to 3.
- `decisions.jsonl` is append-only. A line, once written, is never edited or removed.

## Worked sample

Six real cycles are committed at `decisions/decisions.jsonl` in the repository, covering
5 `TAKEN`, 5 `REJECTED` and 2 `SKIPPED` across two underlyings and three expiries, with one
blackout in force. Build against that file rather than against invented data.


---

## `decisions/dashboard.json`

A view model for the dashboard, written each cycle beside the log. Where the log uses stable
self-describing keys, this uses the dashboard's own vocabulary.

```jsonc
{
  "generatedAt": "2026-08-30T05:12:44Z",   // ISO-8601 UTC
  "day": "Pre-open",                        // "Pre-open" | "Day N of 4" | "Closed"
  "clock": "01:12 ET | 08.30.26",           // ASCII only
  "account": "PA3ILISQPBT4",
  "equity": 100000,
  "positions": [ FeedPosition ],
  "rejections": [ FeedRejection ],
  "closed":    [ FeedClosed ],
  "preGate": 58,        // contracts examined before any gate ran
  "wins": 0, "losses": 0,
  "curve": [ 100000, 100000 ],   // account equity, oldest first
  "curveFrom": "Inception 08.31", "curveTo": "08.30", "curveLabel": "Account equity",
  "riskDeployed": 0, "riskCeiling": 3000.00
}
```

**`FeedRejection`** — `{ t, cand, verdict, gate, reason }`. `t` is `HH:MM` Eastern. `cand` is
`"SPY 772C/777C"` or just the ticker when no spread formed. Same five verdicts as the log.

**`FeedPosition`** — `{ sym, title, kind, qty, legs, dte, open, n, mlPer, maxLoss, maxLossPct,
metrics[] }`. `metrics` is an array of `{ k, v }` where `v` is already formatted for display.

**`FeedClosed`** — `{ sym, title, reason, pnl, win }`. `pnl` is realised dollars, negative for a
loss. `reason` states what the broker can attest to — when it closed and over how many fills.
The *why* (which exit stage fired) is recorded live in the decision stream as a `CLOSED` entry
at the moment it happened; it is not inferred backwards from fills.

### How `closed`, `wins` and `losses` are derived

Alpaca publishes no realised-profit figure per trade. These are computed from execution fills:
signed cash is summed across every fill touching a spread, grouped by underlying and expiry,
and a spread counts as closed only once **every individual leg** has netted back to zero. A
vertical's legs carry opposite signs from the moment it opens, so netting the group as a whole
would report every open spread as closed the instant it was created.

An open or partially unwound spread therefore contributes nothing to `closed`, and no position
is counted as a loss merely because its opening debit has been paid.
