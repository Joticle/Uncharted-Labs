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
    this.setState({ live: this.mapLive(dec, pf), src: { dec, pf } });
  }

  mapLive(dec, pf) {
    const feed = dec && dec.feed ? dec.feed : null;
    if (!feed) return null;

    const num = (s) => {
      if (typeof s === 'number') return s;
      const n = parseFloat(String(s == null ? '' : s).replace(/[^0-9.\-]/g, ''));
      return Number.isFinite(n) ? n : 0;
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

    const positions = (feed.positions || []).map((p) => ({
      title: p.title,
      sym: p.sym,
      kind: p.kind,
      qty: p.qty + ' ×',
      dte: p.dte + ' DTE',
      mlPer: num(p.mlPer),
      n: p.n,
      open: num(p.unrealised),
      legs: legsOf(p),
      metrics: (p.metrics || []).map((m) => [m.k, m.v]),
    }));

    // "cost-drag" -> "Cost drag". The design sets these in a small-caps column.
    const gateLabel = (g) =>
      String(g || '').replace(/[-_]/g, ' ').replace(/^./, (c) => c.toUpperCase());

    // A verdict states what the mandate concluded; only an order id says a position exists.
    // The design has no column for that, so an approval that was never placed is relabelled
    // in the verdict itself -- and its gate becomes the reason no order exists, so the gate
    // ledger does not file it as a refusal it never was.
    const unplaced = { TAKEN: 'WOULD TAKE', CLOSED: 'WOULD CLOSE' };

    const rejections = (feed.rejections || []).map((r) => {
      const approvedOnly = !r.executed && unplaced[r.verdict];
      return {
        t: r.t,
        cand: r.cand,
        verdict: approvedOnly ? unplaced[r.verdict] : r.verdict,
        gate: approvedOnly ? (feed.dryRun ? 'Dry run' : 'Not placed') : gateLabel(r.gate),
        reason: r.reason,
      };
    });

    const closed = (feed.closed || []).map((c) => [
      (c.closedOn || ''), c.title, String(c.reason || '').toUpperCase(), (c.held || ''), num(c.pnl),
    ]);

    const realised = closed.reduce((a, c) => a + c[4], 0);
    const openPl = positions.reduce((a, p) => a + p.open, 0);
    const equity = pf && pf.account ? pf.account.equity : feed.equity;

    // renderVals computes eq = inception + realised + openPl. Solving for inception makes the
    // displayed equity the broker's figure rather than a reconstruction of it.
    const inception = equity - realised - openPl;

    const curve = (pf && pf.curve && pf.curve.length ? pf.curve : feed.curve) || [];

    return {
      inception,
      day: String(feed.day || '').toLowerCase(),
      clock: feed.clock,
      positions,
      rejections,
      closed,
      preGate: feed.preGate || 0,
      wins: feed.wins || 0,
      losses: feed.losses || 0,
      curve,
      curveFrom: feed.curveFrom,
      curveTo: feed.curveTo,
      curveLabel: feed.curveLabel,
      symbols: feed.symbols || [],
      blackoutNote: feed.blackoutNote || '',
      concurrencyNote: feed.concurrencyNote || '',
      fundingNote: feed.fundingNote || '',
    };
  }

  data(stage) {
    const live = this.state.live;
    if (live) return live;

    // Before the first fetch resolves, and if it fails. Ceilings still render against a
    // stated denominator rather than vanishing -- an instrument at rest, not a broken page.
    return {
      inception: 100000, day: '', clock: 'connecting',
      positions: [], rejections: [], closed: [],
      preGate: 0, wins: 0, losses: 0, curve: [],
      curveFrom: '', curveTo: '', curveLabel: 'Equity',
      symbols: [], blackoutNote: '', concurrencyNote: '', fundingNote: '',
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
        "state = { theme: null, stage: null, extra: [], fresh: null, live: null, src: null };",
        1,
    )

    anchor = logic.index("theme()")
    logic = logic[:anchor] + LIVE_JS.strip() + "\n\n  " + logic[anchor:]

    logic = honesty_patch(logic)

    print("  patched: componentDidMount, componentWillUnmount, queue, data")
    return template[: m.start(2)] + logic + template[m.end(2):]


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
        risk: s.blackout ? 0 : (bySym[s.n] || 0),
        k: s.note || (held ? held + ' position' + (held > 1 ? 's' : '') : 'no position'),
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
        "        { label: 'Open positions', value: String(nPos), note: d.concurrencyNote || '', color: nPos === 0 ? dim : ink },",
    ),
    (
        "funding note",
        "          note: deltaV === 0 ? 'funded at $100,000 on 08.31' : (deltaV > 0 ? '+' : '−') + usd0(Math.abs(deltaV)) + ' since funding', color: ink },",
        "          note: deltaV === 0 ? (d.fundingNote || '') : (deltaV > 0 ? '+' : '−') + usd0(Math.abs(deltaV)) + ' since funding', color: ink },",
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
