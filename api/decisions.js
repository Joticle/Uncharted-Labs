// Serves the agent's decision log to the browser.
//
// The log lives in this repository and is committed by the scheduled agent run, not by a
// deploy. Bundling it at build time would freeze it at whatever the last deploy captured, so
// this reads it at request time from the raw file on the default branch. The dashboard then
// stays current with the agent's cycles without a rebuild, which is what "works with my
// machine off" actually requires.
//
// Same-origin, so the browser needs no CORS grant and the cache policy is ours to set.

const REPO = process.env.DECISIONS_REPO || 'Joticle/Uncharted-Labs';
const BRANCH = process.env.DECISIONS_BRANCH || 'main';
const BASE = `https://raw.githubusercontent.com/${REPO}/${BRANCH}`;

/** GitHub raw caches for a few minutes; a cache-buster keeps the agent's latest cycle visible. */
async function readJson(path) {
  const res = await fetch(`${BASE}/${path}?t=${Date.now()}`, {
    headers: { 'Accept': 'application/json, text/plain, */*' },
  });

  if (res.status === 404) return null;
  if (!res.ok) throw new Error(`${path} returned ${res.status}`);
  return res.text();
}

export default async function handler(req, res) {
  try {
    const [latestRaw, feedRaw, historyRaw] = await Promise.all([
      readJson('decisions/latest.json'),
      readJson('decisions/dashboard.json'),
      readJson('decisions/decisions.jsonl'),
    ]);

    // Distinguish "the agent has not run yet" from "the fetch failed". The dashboard renders
    // those differently, and conflating them would show a broken page for a normal state.
    if (latestRaw === null && feedRaw === null) {
      return res.status(200).json({
        state: 'no-runs',
        message: 'The agent has not written a decision log yet.',
        latest: null,
        feed: null,
        history: [],
      });
    }

    const history = (historyRaw || '')
      .split('\n')
      .filter((line) => line.trim().length > 0)
      .map((line) => {
        try {
          return JSON.parse(line);
        } catch {
          return null;
        }
      })
      .filter(Boolean);

    res.status(200).json({
      state: 'ok',
      latest: latestRaw ? JSON.parse(latestRaw) : null,
      feed: feedRaw ? JSON.parse(feedRaw) : null,
      history,
      fetchedAt: new Date().toISOString(),
    });
  } catch (err) {
    res.status(502).json({
      state: 'error',
      message: `Could not read the decision log: ${err.message}`,
      latest: null,
      feed: null,
      history: [],
    });
  }
}
