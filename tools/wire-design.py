#!/usr/bin/env python3
"""
Unbundle the Claude Design export and wire it to live data.

The export is a single self-contained HTML file: a base64+gzip manifest of assets, a
JSON-encoded page template, and a loader that mints blob URLs and swaps the document. This
script unpacks that into an ordinary static site and patches exactly one seam.

The seam is `data(stage)` in the design's `text/x-dc` script -- the single method returning the
whole view model that `renderVals()` consumes. Replacing it, plus `componentDidMount` and
`queue()`, is the entire change. The ~450 lines of rendering below it are untouched, which is
the point: the design is approved, and an approximation of it is not the same artifact.

Re-run this whenever the design is re-exported.

    python tools/wire-design.py <export.html>
"""

from __future__ import annotations

import base64
import gzip
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "dashboard-design"

EXT = {
    "text/javascript": ".js",
    "text/css": ".css",
    "font/woff2": ".woff2",
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/svg+xml": ".svg",
}

# ---------------------------------------------------------------- the replacement data layer

LIVE_JS = r"""
  // ---- wired to live data; everything below renderVals() is the design, untouched ----

  componentDidMount() {
    this.load();
    this.iv = setInterval(() => this.load(), 60000);
  }

  componentWillUnmount() { clearInterval(this.iv); }

  // No injected cadence. The design's mock pushed a synthetic refusal every seventeen
  // seconds to look alive; the agent runs on a schedule measured in hours. If nothing has
  // happened, the stream sits still, which is the honest rendering.
  queue() { return []; }

  async load() {
    const j = async (u) => {
      try { const r = await fetch(u, { headers: { Accept: 'application/json' } }); return await r.json(); }
      catch (e) { return { state: 'error', message: String(e) }; }
    };
    const [dec, pf] = await Promise.all([j('/api/decisions'), j('/api/portfolio')]);
    this.setState({ days: this.buildDays(dec, pf), live: this.mapLive(dec, pf), src: { dec, pf } });
  }

  // The log is append-only history, so the stage buttons replay it rather than switching
  // between scripted scenes. Each checkpoint is a day the agent actually ran, and selecting
  // one shows the book as it stood at the end of that day -- gates filling and refusals
  // accumulating, which is the discipline shown over time rather than at one instant.
  // The log keeps UTC, and every figure on the page is stated in Eastern. Slicing the
  // timestamp gave the UTC date, so the six cycles of Wednesday evening -- 00:17 to 00:33
  // UTC, which is 20:17 to 20:33 ET -- were filed under 09.03 and labelled a tab 09.03 while
  // the header beneath it read 09.02. One run, two calendars, and a judge told that a tab
  // dated Thursday holds Wednesday.
  etDay(iso) {
    const at = Date.parse(iso);
    if (!Number.isFinite(at)) return String(iso).slice(0, 10);

    try {
      // en-CA formats as yyyy-mm-dd, which sorts and slices like the ISO date it replaces.
      return new Intl.DateTimeFormat('en-CA', {
        timeZone: 'America/New_York', year: 'numeric', month: '2-digit', day: '2-digit',
      }).format(new Date(at));
    } catch (e) {
      // Eastern is UTC-4 for the whole contest window, which is the same assumption the
      // agent makes rather than carrying a timezone database.
      return new Date(at - 4 * 3600 * 1000).toISOString().slice(0, 10);
    }
  }

  buildDays(dec, pf) {
    const history = (dec && dec.history) || [];
    if (!history.length) return [];

    const byDay = new Map();
    history.slice().sort((a, b) => String(a.timestamp).localeCompare(String(b.timestamp)))
      .forEach((r) => {
        const day = this.etDay(r.timestamp);
        if (!byDay.has(day)) byDay.set(day, []);
        byDay.get(day).push(r);
      });

    const days = [...byDay.entries()].map(([date, runs]) => ({ date, runs }));

    // Three slots, because the design has three buttons, and they hold the three most
    // recent days the agent ran. First / middle / latest reached back into the weekend
    // rehearsals and stepped over Tuesday 09.01 -- a full session carrying three entries and
    // both positions that later closed on pin risk -- to land on Saturday instead. Fewer
    // days than slots leaves the surplus empty rather than duplicating a day.
    const last3 = days.slice(-3);
    const pick = last3.length === 3 ? last3
      : last3.length === 2 ? [last3[0], null, last3[1]]
      : [last3[0], null, null];

    return pick.map((d) => d && ({
      date: d.date,
      // "08.31" -- the design sets these small and monospaced.
      label: d.date.slice(5).replace('-', '.'),
      runs: d.runs,
      upto: days.slice(0, days.findIndex((x) => x.date === d.date) + 1),
    }));
  }

  mapLive(dec, pf, stageIndex) {
    const feed = dec && dec.feed ? dec.feed : null;
    if (!feed) return null;

    // When replaying a checkpoint, the run for that day supplies the state and every run up
    // to it supplies the accumulated stream.
    const days = this.state.days && this.state.days.length ? this.state.days : this.buildDays(dec, pf);
    const cp = (typeof stageIndex === 'number' && days[stageIndex]) ? days[stageIndex] : null;

    // The most recent checkpoint is the live view, so it reads the broker. Only an earlier
    // checkpoint replays the log -- the broker has no memory of what it held on Sunday, and
    // the log has no knowledge of what filled since the last cycle.
    const isLatest = !cp || cp === days.filter(Boolean)[days.filter(Boolean).length - 1];
    const asOf = (cp && !isLatest) ? cp.runs[cp.runs.length - 1] : null;
    const accumulated = cp ? cp.upto.flatMap((d) => d.runs) : null;

    const num = (s) => {
      if (typeof s === 'number') return s;
      const n = parseFloat(String(s == null ? '' : s).replace(/[^0-9.\-]/g, ''));
      return Number.isFinite(n) ? n : 0;
    };

    // ---- what a replayed checkpoint can honestly reconstruct ----
    //
    // The log records equity, both ceilings and every decision, so those replay exactly. It
    // does not record the book: no leg detail, no mark, no unrealised figure for a day that
    // has passed, and the broker keeps no memory of what it held. Panels needing the book
    // therefore read the aggregate the log does carry, or show the placeholder. What none of
    // them may do is read today: $100,000 equity beside a −$240 change is two different days
    // in one row, and a judge clicking 08.29 / 08.31 / 09.02 sees that before anything else.
    const allDays = cp ? cp.upto : [];
    const dayEnd = (dd) => dd.runs[dd.runs.length - 1];
    const funding = allDays.length ? num(allDays[0].runs[0].equity) : 0;
    const asOfDay = asOf ? this.etDay(asOf.timestamp).slice(5).replace('-', '.') : null;

    // Exits carry their own date, so the closed table is cut at the checkpoint rather than
    // inherited whole. An exit with no usable date is left out of a replay rather than
    // assumed to predate it.
    const closedUpTo = (feed.closed || []).filter((c) => {
      if (!asOfDay) return true;
      const on = String(c.closedOn || '');
      return /^\d\d\.\d\d$/.test(on) && on <= asOfDay;
    });

    // SPY260903C00764000 -> { underlying, expiry, strike }. Mirrors OccSymbol on the agent
    // side: a fixed 15-character suffix, and whatever precedes it is the root.
    // Every displayed time is Eastern, matching the header and the market. The log keeps ISO
    // UTC -- that is the durable record and the contract documents it as such -- but a page
    // showing 13:25 in one panel and 17:25 in another for the same cycle reads as two clocks.
    const ET = -4 * 3600 * 1000;
    const etParts = (iso) => new Date(Date.parse(iso) + ET).toISOString();
    const etHM = (iso) => etParts(iso).slice(11, 16);
    // Dates go through the same conversion the tabs use, so a stream row and the tab above
    // it cannot land on different days.
    const etDate = (iso) => this.etDay(iso).slice(5).replace('-', '.');

    const occ = (sym) => {
      const s = String(sym || '');
      if (s.length <= 15) return null;
      const tail = s.slice(-15);
      if (!/^\d{6}[CP]\d{8}$/.test(tail)) return null;
      return {
        underlying: s.slice(0, -15),
        expiry: '20' + tail.slice(0, 2) + '-' + tail.slice(2, 4) + '-' + tail.slice(4, 6),
        strike: Number(tail.slice(7)) / 1000,
      };
    };

    // "+772C / -777C" -> the two-leg shape the design renders.
    const legsOf = (p) => {
      const parts = String(p.legs || '').split('/').map((x) => x.trim());
      return parts.slice(0, 2).map((leg, i) => ({
        side: leg.startsWith('-') ? 'SHORT' : 'LONG',
        strike: leg.replace(/^[+-]/, ''),
        px: p.legPrices && p.legPrices[i] != null ? String(p.legPrices[i]) : '',
        dist: p.legDistances && p.legDistances[i] != null ? String(p.legDistances[i]) : '',
      }));
    };

    // Positions come from the broker, not from the log. The log records what the agent
    // decided; only the account says what it holds. The run that opens a position reads
    // positions before placing the order, so a log-sourced panel is a cycle behind by
    // construction -- and on 1 Sep it showed an empty book against two filled orders.
    //
    // A replayed checkpoint has no book to show at all. There is no log snapshot to keep:
    // LogRun carries equity and the two ceilings, never the legs behind them.
    const brokerLegs = (pf && Array.isArray(pf.positions) ? pf.positions : [])
      .filter((l) => l.assetClass === 'us_option' && occ(l.symbol));

    // Same pairing rule as the agent: group by underlying and expiry, long leg positive,
    // short leg negative. Netting one leg alone reports a spread as two unrelated trades.
    const spreadsFromBroker = () => {
      const groups = new Map();
      brokerLegs.forEach((l) => {
        const o = occ(l.symbol);
        const k = o.underlying + '|' + o.expiry;
        if (!groups.has(k)) groups.set(k, []);
        groups.get(k).push({ ...l, ...o });
      });

      const out = [];
      groups.forEach((legs) => {
        // Mirrors SpreadReconstruction on the agent side. The broker aggregates by symbol, so
        // three lots sharing a long strike arrive as one leg of thirty contracts against two
        // short strikes. Pairing only the first short invents a single oversized position and
        // hides the rest of the book.
        //
        // Narrowest short first, against the highest long strike beneath it. A chosen
        // convention, not a recovered fact: the broker keeps no record of which long contracts
        // were bought alongside which short.
        const longs = legs.filter((l) => l.qty > 0).map((l) => ({
          ...l, left: Math.abs(l.qty),
          basisPer: l.costBasis / Math.abs(l.qty), valuePer: l.marketValue / Math.abs(l.qty),
        })).sort((a, b) => a.strike - b.strike);

        const shorts = legs.filter((l) => l.qty < 0).map((l) => ({
          ...l, left: Math.abs(l.qty),
          basisPer: l.costBasis / Math.abs(l.qty), valuePer: l.marketValue / Math.abs(l.qty),
        })).sort((a, b) => a.strike - b.strike);

        shorts.forEach((sh) => {
          while (sh.left > 0) {
            const lg = longs.filter((l) => l.left > 0 && l.strike < sh.strike)
              .sort((a, b) => b.strike - a.strike)[0];
            if (!lg) break;

            const n = Math.min(lg.left, sh.left);
            lg.left -= n; sh.left -= n;

            const debitPerShare = (lg.basisPer + sh.basisPer) / 100;
            const markPerShare = (lg.valuePer + sh.valuePer) / 100;
            const unrealised = (lg.unrealizedPl / Math.abs(lg.qty) + sh.unrealizedPl / Math.abs(sh.qty)) * n;
            const dte = Math.max(0, Math.round((Date.parse(lg.expiry + 'T20:00:00Z') - Date.now()) / 86400000));
            const px = (l) => Math.abs(l.basisPer / 100).toFixed(2);

            out.push({
              title: lg.underlying + ' ' + lg.strike + 'C / ' + sh.strike + 'C',
              sym: lg.underlying,
              kind: 'call debit',
              qty: n + ' ×',
              dte: dte + ' DTE · ' + lg.expiry.slice(5).replace('-', '.'),
              mlPer: Math.round(debitPerShare * 100),
              n,
              open: Math.round(unrealised),
              legs: [
                { side: 'LONG', strike: lg.strike + 'C', px: px(lg), dist: '' },
                { side: 'SHORT', strike: sh.strike + 'C', px: px(sh), dist: '' },
              ],
              metrics: [
                ['net debit', debitPerShare.toFixed(2)],
                ['mark', markPerShare.toFixed(2)],
                ['max gain', '$' + Math.round((sh.strike - lg.strike - debitPerShare) * n * 100).toLocaleString('en-US')],
              ],
            });
          }
        });
      });
      return out;
    };

    // feed.positions is the live book, and drawing it under a past date was the defect:
    // 766/771 was opened on 09.02 and expires 09.03, yet it rendered on the 08.29 slice at
    // 0 DTE against an account that was holding nothing whatsoever that day.
    const positions = asOf ? [] : spreadsFromBroker();

    // "cost-drag" -> "Cost drag". The design sets these in a small-caps column.
    const gateLabel = (g) =>
      String(g || '').replace(/[-_]/g, ' ').replace(/^./, (c) => c.toUpperCase());

    // A verdict states what the mandate concluded; only an order id says a position exists.
    // The design has no column for that, so an approval that was never placed is relabelled
    // in the verdict itself -- and its gate becomes the reason no order exists, so the gate
    // ledger does not file it as a refusal it never was.
    const unplaced = { TAKEN: 'WOULD TAKE', CLOSED: 'WOULD CLOSE' };

    // Replayed: every decision from the first day through the selected one, newest first.
    const sourceRejections = accumulated
      ? accumulated.flatMap((r) => (r.decisions || []).map((d) => ({
          t: etHM(r.timestamp),
          cand: d.structure ? d.underlying + ' ' + d.structure : d.underlying,
          verdict: d.verdict, gate: d.gate, reason: d.finding, executed: d.executed,
        }))).reverse()
      : (feed.rejections || []);

    const rejections = sourceRejections.map((r) => {
      const approvedOnly = !r.executed && unplaced[r.verdict];
      return {
        t: r.t,
        cand: r.cand,
        verdict: approvedOnly ? unplaced[r.verdict] : r.verdict,
        gate: approvedOnly ? (feed.dryRun ? 'Dry run' : 'Not placed') : gateLabel(r.gate),
        reason: r.reason,
      };
    });

    const closed = closedUpTo.map((c) => [
      (c.closedOn || ''), c.title, String(c.reason || '').toUpperCase(), (c.held || ''), num(c.pnl),
    ]);

    const realised = closed.reduce((a, c) => a + c[4], 0);
    const equity = asOf ? num(asOf.equity)
      : (pf && pf.account && Number.isFinite(pf.account.equity) ? pf.account.equity : feed.equity);

    // Unrealised profit is implied by what is known: an account taking no deposits holds
    // funding, plus what it has banked, plus what it is carrying. That holds on the live view
    // as well as a replayed one, and summing the reconstructed book instead was wrong in both
    // directions -- realised only counts spreads whose every leg has netted to zero, so a
    // partial close banks nothing it can see, and a book that has just expired carries
    // nothing. On the final afternoon the header read $103,312 above "-$240 since funding":
    // eight of ten contracts closed for +$2,832 and neither figure could account for it.
    const openPl = allDays.length
      ? equity - funding - realised
      : positions.reduce((a, p) => a + p.open, 0);

    // renderVals computes eq = inception + realised + openPl. Anchoring inception at funding
    // and deriving unrealised above keeps that identity exact -- eq still resolves to the
    // broker's own equity -- while making the delta beneath the headline what it claims to
    // be: the distance from the account's starting balance. Solving for inception instead
    // made the delta the sum of whatever the reconstruction happened to recognise.
    const inception = allDays.length ? funding : equity - realised - openPl;

    const curve = (pf && pf.curve && pf.curve.length ? pf.curve : feed.curve) || [];

    return {
      inception,
      day: asOf ? String(asOf.calendarState || '').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase() : String(feed.day || '').toLowerCase(),
      clock: asOf ? etHM(asOf.timestamp) + ' ET | ' + etDate(asOf.timestamp) : feed.clock,
      checkpoints: days.map((d) => (d ? d.label : '')),
      positions,
      rejections,
      closed,
      // Handed over rather than recomputed from the book: on a replay there is none, and on
      // the live view summing it misses everything the pairing could not resolve.
      openPl: allDays.length ? openPl : null,
      // Risk deployed replays from the log's own ceiling ledger, which is exact.
      riskDeployed: asOf ? num((asOf.riskPerTrade || {}).deployedDollars) : null,
      // A day that deployed nothing held nothing, and that is exact rather than inferred. A
      // day that did deploy carries no count in the log, so the card shows the placeholder
      // instead of a number borrowed from the live book.
      positionCount: asOf
        ? (num((asOf.riskPerTrade || {}).deployedDollars) === 0 ? 0 : null)
        : null,
      // Pre-gate filtering is a per-cycle figure the log does not keep.
      preGate: asOf ? 0 : (feed.preGate || 0),
      wins: asOf ? closedUpTo.filter((c) => c.win).length : (feed.wins || 0),
      losses: asOf ? closedUpTo.filter((c) => !c.win).length : (feed.losses || 0),
      // One point per day the agent ran, ending at the checkpoint, taken from the log.
      curve: asOf ? [funding, ...allDays.map((dd) => num(dayEnd(dd).equity))] : curve,
      curveFrom: asOf
        ? 'Inception ' + allDays[0].date.slice(5).replace('-', '.')
        : feed.curveFrom,
      curveTo: asOf ? asOfDay : feed.curveTo,
      curveLabel: feed.curveLabel,
      bookMaxGain: (() => {
        const risk = positions.reduce((a, p) => a + p.mlPer * p.n, 0);
        const gain = positions.reduce((a, p) => {
          const width = Number(String(p.legs[1].strike).replace(/\D/g, ''))
                      - Number(String(p.legs[0].strike).replace(/\D/g, ''));
          return a + (width * 100 - p.mlPer) * p.n;
        }, 0);
        if (!risk) return '';
        return '$' + Math.round(gain).toLocaleString('en-US')
             + ' · ' + (gain / risk).toFixed(2) + ':1 if every short clears';
      })(),
      // Exposure replays from the log's ceiling ledger, keeping the same underlyings so the
      // panel does not change shape between days. A day holding capital the log does not
      // itemise shows the em dash where a position count would otherwise go.
      symbols: asOf
        ? (feed.symbols || []).map((s) => {
            const row = (asOf.symbolExposure || []).find((x) => x.label === s.n);
            const risk = row ? num(row.deployedDollars) : 0;
            return { n: s.n, note: s.note, blackout: !!s.blackout, risk,
                     k: risk ? '—' : 'no position' };
          })
        : (feed.symbols || []),
      blackoutNote: feed.blackoutNote || '',
      concurrencyNote: feed.concurrencyNote || '',
      fundingNote: asOf
        ? 'funded at $' + funding.toLocaleString('en-US',
            { minimumFractionDigits: 2, maximumFractionDigits: 2 })
        : (feed.fundingNote || ''),
    };
  }

  data(stage) {
    const idx = { day0: 0, thin: 1, full: 2 }[stage];
    const src = this.state.src;

    // 'full' is the latest checkpoint, which is also the live view.
    if (src && typeof idx === 'number') {
      const replayed = this.mapLive(src.dec, src.pf, idx);
      if (replayed) return replayed;
    }

    const live = this.state.live;
    if (live) return live;

    // Before the first fetch resolves, and if it fails. Ceilings still render against a
    // stated denominator rather than vanishing -- an instrument at rest, not a broken page.
    return {
      inception: 100000, day: '', clock: 'connecting',
      positions: [], rejections: [], closed: [],
      preGate: 0, wins: 0, losses: 0, curve: [],
      curveFrom: '', curveTo: '', curveLabel: 'Equity',
      symbols: [], blackoutNote: '', concurrencyNote: '', fundingNote: '', bookMaxGain: '',
      checkpoints: [],
    };
  }
"""


def unbundle(src: pathlib.Path) -> str:
    text = src.read_text(encoding="utf-8", errors="replace")

    def block(kind: str) -> str:
        m = re.search(rf'<script type="{re.escape(kind)}"[^>]*>', text)
        if not m:
            sys.exit(f"error: no {kind} block. Is this a Claude Design export?")
        start = m.end()
        return text[start:text.find("</script>", start)].strip()

    manifest = json.loads(block("__bundler/manifest"))
    template = json.loads(block("__bundler/template"))

    (OUT / "assets").mkdir(parents=True, exist_ok=True)
    for old in (OUT / "assets").glob("*"):
        old.unlink()

    for uuid, entry in manifest.items():
        raw = base64.b64decode(entry["data"])
        if entry.get("compressed"):
            raw = gzip.decompress(raw)
        name = f"{uuid}{EXT.get(entry.get('mime', ''), '.bin')}"
        (OUT / "assets" / name).write_bytes(raw)
        template = template.replace(uuid, f"assets/{name}")

    print(f"  unbundled {len(manifest)} assets")
    return template


NL = chr(10)

# The three stage buttons live in the template as text nodes, not in the script. Their
# handlers and styles are untouched; only the labels become data, so the buttons describe
# the log rather than a script.
_BTN = '<div sc-camel-on-click="{{ %s }}" style="{{ %s }}">%s</div>'

MARKUP_SUBSTITUTIONS = [
    (
        "days-to-close NaN guard",
        "const daysStat = nClosed === 0\n      ? { label: 'Days to close', value: '—', note: 'reports on first exit', color: dim }\n      : { label: 'Days to close', value: (d.closed.reduce((a, c) => a + parseFloat(c[3]), 0) / nClosed).toFixed(1), note: 'mean holding period, in sessions', color: ink };",
        "// A mean over unparseable holding periods is NaN, and NaN on the page reads as a broken\n    // instrument rather than a quiet one. The agent now emits a real session count; this\n    // refuses to print anything that is not a finite number, falling back to the same\n    // placeholder the card already shows before the first exit.\n    const heldMean = nClosed === 0\n      ? Number.NaN\n      : d.closed.reduce((a, c) => a + Number.parseFloat(c[3]), 0) / nClosed;\n    const daysStat = (nClosed > 0 && Number.isFinite(heldMean))\n      ? { label: 'Days to close', value: heldMean.toFixed(1), note: 'mean holding period, in sessions', color: ink }\n      : { label: 'Days to close', value: '—', note: 'reports on first exit', color: dim };",
    ),
    (
        "book-level upside markup",
        '<div style="font-family:\'IBM Plex Mono\',monospace;font-size:9px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;color:var(--ink3,#8b98a4)">Aggregate deployed</div>\n<div style="font-family:\'IBM Plex Mono\',monospace;font-size:15px;font-weight:600;font-variant-numeric:tabular-nums">{{ aggLine }}</div>',
        '<div style="font-family:\'IBM Plex Mono\',monospace;font-size:9px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;color:var(--ink3,#8b98a4)">Aggregate deployed</div>\n<div style="font-family:\'IBM Plex Mono\',monospace;font-size:15px;font-weight:600;font-variant-numeric:tabular-nums">{{ aggLine }}</div></div>\n<div style="display:flex;align-items:baseline;justify-content:space-between;gap:10px;padding-top:14px;border-top:1px solid var(--line2,rgba(18,26,34,.06));padding-top:10px;border-top:none">\n<div style="font-family:\'IBM Plex Mono\',monospace;font-size:9px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;color:var(--ink3,#8b98a4)">Max gain at settlement</div>\n<div style="font-family:\'IBM Plex Mono\',monospace;font-size:15px;font-weight:600;font-variant-numeric:tabular-nums;color:var(--ink2,#5c6b78)">{{ bookGainLine }}</div>',
    ),
    (
        "stage button text",
        NL.join([
            _BTN % ('setDay0', 'b0', 'Day 0'),
            _BTN % ('setThin', 'b1', 'Day 2'),
            _BTN % ('setFull', 'b2', 'Day 4'),
        ]),
        NL.join([
            _BTN % ('setDay0', 'b0', '{{ dayLabel0 }}'),
            _BTN % ('setThin', 'b1', '{{ dayLabel1 }}'),
            _BTN % ('setFull', 'b2', '{{ dayLabel2 }}'),
        ]),
    ),
]


def patch_markup(template: str) -> str:
    for label, old, new in MARKUP_SUBSTITUTIONS:
        if old not in template:
            sys.exit(f"error: could not find the {label} markup. The design changed.")
        template = template.replace(old, new, 1)
        print(f"  de-fixtured: {label}")
    return template


def patch(template: str) -> str:
    m = re.search(r'(<script type="text/x-dc"[^>]*>)(.*?)(</script>)', template, re.S)
    if not m:
        sys.exit("error: no text/x-dc script found.")

    logic = m.group(2)

    # Excise the three fixture-bound methods. Brace-matching rather than regex, because the
    # bodies contain braces and a greedy pattern would swallow the rest of the class.
    def cut(body: str, signature: str) -> str:
        i = body.find(signature)
        if i < 0:
            sys.exit(f"error: '{signature}' not found -- the design's structure changed.")
        j = body.index("{", i)
        depth = 0
        for k in range(j, len(body)):
            if body[k] == "{":
                depth += 1
            elif body[k] == "}":
                depth -= 1
                if depth == 0:
                    return body[:i] + body[k + 1:]
        sys.exit(f"error: unbalanced braces after '{signature}'.")

    for sig in ("componentDidMount()", "componentWillUnmount()", "queue()", "data(stage)"):
        logic = cut(logic, sig)

    # Seed the state key the new data() reads.
    logic = logic.replace(
        "state = { theme: null, stage: null, extra: [], fresh: null };",
        "state = { theme: null, stage: null, extra: [], fresh: null, live: null, src: null, days: [] };",
        1,
    )

    anchor = logic.index("theme()")
    logic = logic[:anchor] + LIVE_JS.strip() + "\n\n  " + logic[anchor:]

    logic = honesty_patch(logic)

    print("  patched: componentDidMount, componentWillUnmount, queue, data")
    return patch_markup(template[: m.start(2)] + logic + template[m.end(2):])


# ------------------------------------------------------------------- fixtures in the render
#
# Four pieces of invented data live inside renderVals() rather than in data(): a hardcoded
# SPY/IWM/QQQ universe, a QQQ earnings blackout dated 09.04, a five-position concurrency cap,
# and a funding date. All four would render as fact on a live dashboard -- the blackout most
# damagingly, since it draws a hatched "gate held" bar for a rule that does not exist.
#
# These substitutions move each one behind a value supplied by data(). No layout, type, colour
# or copy structure changes: the blackout bar still draws, when a blackout is genuinely in
# force. Presentation logic changes; presentation does not.

SUBSTITUTIONS = [
    (
        "hardcoded ticker universe",
        """    const symRows = [
      { n: 'SPY', risk: bySym.SPY || 0, k: (bySym.SPY ? (d.positions.filter(p => p.sym === 'SPY').length + ' position' + (d.positions.filter(p => p.sym === 'SPY').length > 1 ? 's' : '')) : 'no position') },
      { n: 'IWM', risk: bySym.IWM || 0, k: (bySym.IWM ? '1 position' : 'no position') },
      { n: 'QQQ', risk: 0, k: 'blackout · earnings 09.04', blackout: true }
    ];""",
        """    const symRows = (d.symbols || []).map(s => {
      const held = d.positions.filter(p => p.sym === s.n).length;
      return {
        n: s.n,
        risk: s.blackout ? 0 : (s.risk != null ? s.risk : (bySym[s.n] || 0)),
        k: s.blackout ? s.note
          : (s.k != null ? s.k
            : (held ? held + ' position' + (held > 1 ? 's' : '') : 'no position')),
        blackout: !!s.blackout
      };
    });""",
    ),
    (
        "QQQ blackout note",
        "      blackoutNote: 'QQQ is refused outright until 09.04. A blackout is a gate with no fill — the channel stays empty by rule, not by circumstance.',",
        "      blackoutNote: d.blackoutNote || '',",
    ),
    (
        "concurrency cap",
        "        { label: 'Open positions', value: String(nPos), note: 'of 5 concurrent · cap unchanged', color: nPos === 0 ? dim : ink },",
        """        { label: 'Open positions', value: nPos == null ? '—' : String(nPos),
          note: nPos == null ? 'not itemised in the log for a past day' : (d.concurrencyNote || ''),
          color: nPos ? ink : dim },""",
    ),
    (
        "book-level upside",
        "      aggLine: usd0(totalRisk) + ' · ' + f2(pct(totalRisk)),",
        """      aggLine: usd0(totalRisk) + ' · ' + f2(pct(totalRisk)),
      // The upside stated at book level, worded so it is not mistaken for the same kind of
      // claim as the risk line above it. Maximum loss is enforced by the broker whatever
      // happens; maximum gain requires every short strike to be cleared at settlement.
      bookGainLine: d.bookMaxGain || '',""",
    ),
    (
        "stage button labels",
        "      b0: this.btn(stage === 'day0', true), b1: this.btn(stage === 'thin', true), b2: this.btn(stage === 'full', true),",
        """      b0: this.btn(stage === 'day0', true), b1: this.btn(stage === 'thin', true), b2: this.btn(stage === 'full', true),
      // The buttons describe the log rather than a script: each is a day the agent ran.
      // An em dash marks a slot the history does not reach yet.
      dayLabel0: (d.checkpoints && d.checkpoints[0]) || '—',
      dayLabel1: (d.checkpoints && d.checkpoints[1]) || '—',
      dayLabel2: (d.checkpoints && d.checkpoints[2]) || '—',""",
    ),
    (
        "funding note",
        "          note: deltaV === 0 ? 'funded at $100,000 on 08.31' : (deltaV > 0 ? '+' : '−') + usd0(Math.abs(deltaV)) + ' since funding', color: ink },",
        "          note: deltaV === 0 ? (d.fundingNote || '') : (deltaV > 0 ? '+' : '−') + usd0(Math.abs(deltaV)) + ' since funding', color: ink },",
    ),
    (
        "unrealised from the checkpoint",
        """    const openPl = d.positions.reduce((a, p) => a + p.open, 0);""",
        """    const openPl = d.openPl != null ? d.openPl : d.positions.reduce((a, p) => a + p.open, 0);""",
    ),
    (
        "aggregate risk from the log",
        """    const totalRisk = d.positions.reduce((a, p) => a + p.mlPer * p.n, 0);
    const nPos = d.positions.length;""",
        """    // A replayed day has no leg detail, but the log's ceiling ledger holds the aggregate,
    // and that is what these two read. nPos is null when a past day deployed capital the
    // log does not itemise: the card then shows the placeholder rather than counting an
    // empty list and announcing that the account was flat.
    const totalRisk = d.riskDeployed != null
      ? d.riskDeployed
      : d.positions.reduce((a, p) => a + p.mlPer * p.n, 0);
    const nPos = d.positionCount != null
      ? d.positionCount
      : (d.riskDeployed != null ? null : d.positions.length);""",
    ),
    (
        "state line may be unknown",
        """      stateLine: nPos + (nPos === 1 ? ' position' : ' positions') + ' open · ' + usd0(totalRisk) +""",
        """      stateLine: (nPos == null ? '—' : nPos + (nPos === 1 ? ' position' : ' positions')) + ' open · ' + usd0(totalRisk) +""",
    ),
    (
        "position heading may be unknown",
        """      posHeading: nPos === 0 ? 'Open positions · none' : 'Open positions · ' + nPos,""",
        """      posHeading: nPos == null ? 'Open positions · —'
        : (nPos === 0 ? 'Open positions · none' : 'Open positions · ' + nPos),""",
    ),
    (
        "spread count may be unknown",
        """          note: totalRisk === 0 ? 'nothing deployed · every ceiling intact' : f2(pct(totalRisk)) + ' of equity, across ' + nPos + ' spreads',""",
        """          note: totalRisk === 0 ? 'nothing deployed · every ceiling intact'
            : f2(pct(totalRisk)) + ' of equity'
              + (nPos == null ? '' : ', across ' + nPos + ' spreads'),""",
    ),
]


def honesty_patch(logic: str) -> str:
    for label, old, new in SUBSTITUTIONS:
        if old not in logic:
            sys.exit(
                f"error: could not find the {label} fixture. The design changed; "
                f"re-check renderVals() before trusting this output."
            )
        logic = logic.replace(old, new, 1)
        print(f"  de-fixtured: {label}")
    return logic


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)

    src = pathlib.Path(sys.argv[1])
    if not src.exists():
        sys.exit(f"error: {src} not found")

    template = patch(unbundle(src))
    (OUT / "index.html").write_text(template, encoding="utf-8")
    print(f"  wrote {OUT / 'index.html'} ({len(template)} chars)")


if __name__ == "__main__":
    main()
