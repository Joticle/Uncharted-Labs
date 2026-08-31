// Read-only Alpaca proxy.
//
// The dashboard runs in the browser, so the API key cannot go anywhere near it. This function
// holds the credentials server-side and exposes exactly three reads: account, open positions,
// and equity history.
//
// There is no order path here, and that is structural rather than a matter of discipline:
// every request this file can construct is a GET against a fixed allow-list of endpoints, and
// the endpoint is chosen from a constant in this file rather than from anything the caller
// sends. A query parameter cannot reach a different URL, and no code path issues POST or
// DELETE. Placing or cancelling an order through this function is not something you would have
// to be careful to avoid; it is not expressible.

const ACCOUNTS = {
  dev: { key: 'ALPACA_DEV_KEY', secret: 'ALPACA_DEV_SECRET' },
  comp: { key: 'ALPACA_COMP_KEY', secret: 'ALPACA_COMP_SECRET' },
};

// Fixed. Never interpolated from a request.
const TRADING = 'https://paper-api.alpaca.markets/v2';

async function get(path, key, secret) {
  const res = await fetch(`${TRADING}${path}`, {
    method: 'GET',
    headers: {
      'APCA-API-KEY-ID': key,
      'APCA-API-SECRET-KEY': secret,
      'Accept': 'application/json',
    },
  });

  if (!res.ok) {
    const body = await res.text();
    throw new Error(`${path} -> ${res.status} ${body.slice(0, 160)}`);
  }

  return res.json();
}

export default async function handler(req, res) {
  // Which account to show is a deployment decision, not a caller's. Defaults to dev so a
  // misconfigured deploy reads the account that does not matter.
  const which = (process.env.ALPACA_ACCOUNT || 'dev').toLowerCase();
  const names = ACCOUNTS[which] || ACCOUNTS.dev;

  const key = process.env[names.key];
  const secret = process.env[names.secret];

  if (!key || !secret) {
    return res.status(200).json({
      state: 'unconfigured',
      message: `Set ${names.key} and ${names.secret} in the deployment environment.`,
      account: null,
      positions: [],
      curve: [],
    });
  }

  try {
    const [account, positions, history] = await Promise.all([
      get('/account', key, secret),
      get('/positions', key, secret),
      get('/account/portfolio/history?period=1W&timeframe=1D', key, secret).catch(() => null),
    ]);

    res.status(200).json({
      state: 'ok',
      which,
      account: {
        accountNumber: account.account_number,
        // equity, never buying_power. Alpaca reports four adjacent figures that are wrong for
        // this purpose, and the 4x one sorts first alphabetically.
        equity: Number(account.equity),
        optionsBuyingPower: Number(account.options_buying_power),
        optionsTradingLevel: Number(account.options_trading_level),
      },
      positions: (positions || []).map((p) => ({
        symbol: p.symbol,
        assetClass: p.asset_class,
        qty: Number(p.qty),
        costBasis: Number(p.cost_basis),
        marketValue: Number(p.market_value),
        unrealizedPl: Number(p.unrealized_pl),
      })),
      curve: history?.equity?.map(Number).filter((n) => Number.isFinite(n)) ?? [],
      fetchedAt: new Date().toISOString(),
    });
  } catch (err) {
    res.status(502).json({
      state: 'error',
      message: `Alpaca read failed: ${err.message}`,
      account: null,
      positions: [],
      curve: [],
    });
  }
}
