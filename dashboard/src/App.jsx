import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

const POLL_MS = 60_000;

const usd = (n) =>
  '$' + Number(n ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const usd0 = (n) =>
  '$' + Number(n ?? 0).toLocaleString('en-US', { maximumFractionDigits: 0 });

/**
 * A verdict states what the mandate concluded. Only `executed` says an order exists.
 * TAKEN with executed=false is an approval, not a position, and is labelled as one.
 */
function verdictLabel(d) {
  if (d.verdict === 'TAKEN') return d.executed ? 'TAKEN' : 'WOULD TAKE';
  if (d.verdict === 'CLOSED') return d.executed ? 'CLOSED' : 'WOULD CLOSE';
  return d.verdict;
}

function verdictClass(d) {
  if (d.verdict === 'TAKEN' || d.verdict === 'CLOSED') return d.executed ? 'executed' : 'approved';
  if (d.verdict === 'SKIPPED') return 'skipped';
  return 'refused';
}

function useTheme() {
  const [theme, setTheme] = useState(() => localStorage.getItem('uo-theme') || 'system');

  useEffect(() => {
    const root = document.documentElement;
    if (theme === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', theme);
    try {
      localStorage.setItem('uo-theme', theme);
    } catch {
      /* private browsing; the page still renders correctly without the preference */
    }
  }, [theme]);

  return [theme, setTheme];
}

/** Polls an endpoint, keeping the last good payload visible while a refresh is in flight. */
function useFeed(url) {
  const [data, setData] = useState(null);
  const [error, setError] = useState(null);
  const [loaded, setLoaded] = useState(false);

  const load = useCallback(async () => {
    try {
      const res = await fetch(url, { headers: { Accept: 'application/json' } });
      const body = await res.json();
      setData(body);
      setError(null);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoaded(true);
    }
  }, [url]);

  useEffect(() => {
    load();
    const id = setInterval(load, POLL_MS);
    return () => clearInterval(id);
  }, [load]);

  return { data, error, loaded };
}

function Gauge({ name, deployed, ceiling, foot }) {
  const pct = ceiling > 0 ? Math.min(1, deployed / ceiling) : 0;
  const empty = !deployed;

  return (
    <div className="card gauge">
      <div className="gauge-top">
        <span className="gauge-name">{name}</span>
        <span className="gauge-val mono">
          {usd(deployed)} <span style={{ color: 'var(--ink3)' }}>of {usd(ceiling)}</span>
        </span>
      </div>
      <div className="track">
        <div className={'fill' + (empty ? ' empty' : '')} style={{ width: `${pct * 100}%` }} />
      </div>
      <div className="gauge-foot">{foot}</div>
    </div>
  );
}

export default function App() {
  const [theme, setTheme] = useTheme();
  const decisions = useFeed('/api/decisions');
  const portfolio = useFeed('/api/portfolio');

  const feed = decisions.data?.feed ?? null;
  const run = decisions.data?.latest ?? null;
  const acct = portfolio.data?.account ?? null;

  // A row is "fresh" only when it was absent from the previous poll. Nothing is injected on a
  // timer; if the agent has not run, the stream sits still, which is the truth.
  const seen = useRef(new Set());
  const [fresh, setFresh] = useState(new Set());

  const rows = feed?.rejections ?? [];

  useEffect(() => {
    if (!rows.length) return;
    const keys = rows.map((r) => `${r.t}|${r.cand}|${r.verdict}`);
    const isFirstLoad = seen.current.size === 0;
    const added = keys.filter((k) => !seen.current.has(k));
    keys.forEach((k) => seen.current.add(k));
    if (!isFirstLoad && added.length) {
      setFresh(new Set(added));
      const id = setTimeout(() => setFresh(new Set()), 1500);
      return () => clearTimeout(id);
    }
  }, [rows]);

  const refused = rows.filter((r) => r.verdict !== 'TAKEN').length;
  const executed = rows.filter((r) => r.executed).length;

  const equity = acct?.equity ?? run?.equity ?? 0;
  const riskCeiling = feed?.riskCeiling ?? 0;
  const riskDeployed = feed?.riskDeployed ?? 0;

  const exposure = run?.symbolExposure ?? [];
  const symbolCeiling = exposure[0]?.ceilingDollars ?? equity * 0.05;
  const symbolDeployed = useMemo(
    () => exposure.reduce((sum, g) => sum + (g.deployedDollars || 0), 0),
    [exposure],
  );

  const loading = !decisions.loaded && !portfolio.loaded;
  const noRuns = decisions.data?.state === 'no-runs';
  const readError = decisions.data?.state === 'error' || decisions.error;
  const alpacaUnconfigured = portfolio.data?.state === 'unconfigured';

  return (
    <div className="wrap">
      <header className="masthead">
        <div>
          <h1>Uncharted Options</h1>
          <p className="tagline">A defined-risk options agent. What it refused, and why.</p>
        </div>
        <div className="stamps mono">
          <span>
            <span className="label">Account</span> <b>{acct?.accountNumber ?? run?.account ?? '—'}</b>
          </span>
          <span>
            <span className="label">Equity</span> <b>{equity ? usd(equity) : '—'}</b>
          </span>
          <span>
            <span className="label">Phase</span> <b>{feed?.day ?? '—'}</b>
          </span>
          <span>
            <span className="label">Cycle</span> <b>{feed?.clock ?? '—'}</b>
          </span>
          <button
            className="theme-toggle"
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
            aria-label="Switch colour theme"
          >
            {theme === 'dark' ? 'Light' : 'Dark'}
          </button>
        </div>
      </header>

      {readError && (
        <div className="banner bad">
          <span className="tag">OFFLINE</span>
          <span>
            The decision log could not be read. Figures below are from the last successful poll,
            or absent. {decisions.data?.message ?? decisions.error}
          </span>
        </div>
      )}

      {feed?.dryRun && (
        <div className="banner">
          <span className="tag">DRY RUN</span>
          <span>
            Account, chain, quotes, greeks, sizing and every gate are live. No orders were
            created, so nothing here is a position — approved candidates read <em>would take</em>.
          </span>
        </div>
      )}

      <section className="section-head">
        <h2>Two hard ceilings</h2>
        <p>
          Risk per position and exposure per underlying, both as a fraction of equity. These are
          not advisory: a candidate that breaches either is refused before it reaches an order.
        </p>
      </section>

      <div className="ceilings">
        <Gauge
          name="Risk per position"
          deployed={riskDeployed}
          ceiling={riskCeiling}
          foot={`The 3 — 3% of equity, capped at construction`}
        />
        <Gauge
          name="Exposure per underlying"
          deployed={symbolDeployed}
          ceiling={symbolCeiling}
          foot={
            exposure.length
              ? `The 5 — ${exposure.map((g) => `${g.label} ${usd0(g.deployedDollars)}`).join(', ')}`
              : 'The 5 — no underlyings held'
          }
        />
        <Gauge
          name="Aggregate deployed"
          deployed={riskDeployed}
          ceiling={equity}
          foot={`${feed?.preGate ?? 0} contracts screened · ${refused} refused · ${executed} executed`}
        />
      </div>

      <section className="section-head">
        <h2>The rejection stream</h2>
        <p>
          Every candidate the agent evaluated. A refusal here is the mandate working — the
          record of what was declined is the only evidence a limit is enforced rather than
          described, and it exists nowhere in broker data.
        </p>
      </section>

      <div className="card">
        <div className="stream-head">
          <span>Time</span>
          <span>Candidate</span>
          <span>Verdict</span>
          <span>Gate</span>
          <span>Finding</span>
        </div>

        {loading && (
          <div className="empty">
            <b>Reading the log…</b>
          </div>
        )}

        {!loading && noRuns && (
          <div className="empty">
            <b>The agent has not run yet</b>
            <span>
              No decision log has been written. The first scheduled cycle will populate this.
            </span>
          </div>
        )}

        {!loading && !noRuns && rows.length === 0 && (
          <div className="empty">
            <b>Cycle ran, nothing to evaluate</b>
            <span>The agent completed a pass and reached no candidate.</span>
          </div>
        )}

        {rows.map((r, i) => {
          const key = `${r.t}|${r.cand}|${r.verdict}`;
          return (
            <div
              key={`${key}-${i}`}
              className={
                'row' + (r.executed ? ' executed' : '') + (fresh.has(key) ? ' fresh' : '')
              }
            >
              <span className="cell-time mono">{r.t}</span>
              <span className="cell-cand mono">{r.cand}</span>
              <span className={'verdict ' + verdictClass(r)}>{verdictLabel(r)}</span>
              <span className="cell-gate mono">{r.gate}</span>
              <span className="cell-finding">{r.reason}</span>
            </div>
          );
        })}
      </div>

      <section className="section-head">
        <h2>What is held</h2>
        <p>
          Defined-risk verticals only. Maximum loss is fixed when the order is constructed and
          enforced by the broker, not by this agent staying alive.
        </p>
      </section>

      <div className="card">
        {(feed?.positions ?? []).length === 0 ? (
          <div className="empty">
            <b>No positions held</b>
            <span>
              {usd(0)} of {usd(riskCeiling)} deployed. This is the normal state before the first
              fill and after the final flatten — not an error, and not a page still loading.
            </span>
          </div>
        ) : (
          feed.positions.map((p, i) => (
            <div className="pos-row" key={`${p.sym}-${i}`}>
              <div>
                <div className="pos-title">{p.title}</div>
                <div className="pos-sub mono">{p.legs}</div>
              </div>
              <div className="mono">×{p.qty}</div>
              <div className="pos-metrics">
                {(p.metrics ?? []).map((m, j) => (
                  <span key={j}>
                    <span className="k">{m.k}</span> <span className="mono">{m.v}</span>
                  </span>
                ))}
              </div>
              <div className="pos-risk mono">
                {usd(p.maxLoss)}
                <div className="pos-sub">{p.maxLossPct}% of equity at risk</div>
              </div>
            </div>
          ))
        )}
      </div>

      <section className="section-head">
        <h2>Reconciles as</h2>
        <p>
          Closed positions and their realised outcome, derived from execution fills. A spread
          counts as closed only once every leg has netted back to zero.
        </p>
      </section>

      <div className="card">
        {(feed?.closed ?? []).length === 0 ? (
          <div className="empty">
            <b>Nothing closed yet</b>
            <span>
              {feed?.wins ?? 0} wins, {feed?.losses ?? 0} losses. Realised figures appear once a
              position is unwound; none has been.
            </span>
          </div>
        ) : (
          feed.closed.map((c, i) => (
            <div className="pos-row" key={`${c.sym}-${i}`}>
              <div>
                <div className="pos-title">{c.title}</div>
                <div className="pos-sub mono">{c.sym}</div>
              </div>
              <div className={'mono pnl ' + (c.win ? 'win' : 'loss')}>
                {c.pnl >= 0 ? '+' : ''}
                {usd(c.pnl)}
              </div>
              <div className="pos-metrics">{c.reason}</div>
              <div className="pos-risk mono">{c.win ? 'win' : 'loss'}</div>
            </div>
          ))
        )}
      </div>

      {alpacaUnconfigured && (
        <p className="footnote">
          Position and equity figures come from the decision log only — the Alpaca read is not
          configured in this deployment. {portfolio.data?.message}
        </p>
      )}

      <p className="footnote">
        Read from <code>decisions/latest.json</code> and <code>decisions/dashboard.json</code>,
        written by the agent each cycle and served through a read-only proxy that holds the
        broker key server-side. Refreshes every 60 seconds. <code>closed</code>,{' '}
        <code>wins</code> and <code>losses</code> are computed from execution fills — empty
        because nothing has been unwound, not because they are placeholders.
      </p>
    </div>
  );
}
