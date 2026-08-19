#!/usr/bin/env node
//
// Runs the evaluation matrix against a live API and records what came back.
//
//   node run.js --label=baseline [--only=meren,chief] [--force]
//
// This is the half that costs money: each case is a real build, which is two Opus calls
// and roughly two minutes. Scoring is a separate script and free, because everything a
// check could want is snapshotted here.
//
// The snapshot is the point. Card prices move, the oracle index gets rebuilt, and a check
// that re-reads them at scoring time would make two runs incomparable for reasons that
// have nothing to do with the change being measured. So the plan and the facts of every
// card in it are frozen into the result file, and score.js never touches the network.

const fs = require('fs');
const path = require('path');
const https = require('https');

const API = process.env.API_URL || 'https://localhost:7001';
const RESULTS = path.join(__dirname, 'results');

// The dev API serves a self-signed certificate.
const agent = new https.Agent({ rejectUnauthorized: false });

function request(method, url, { token, body, timeout = 900_000 } = {}) {
  return new Promise((resolve, reject) => {
    const req = https.request(
      url,
      {
        method,
        agent,
        timeout,
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
      },
      (res) => {
        let data = '';
        res.setEncoding('utf8');
        res.on('data', (c) => (data += c));
        res.on('end', () => resolve({ status: res.statusCode, body: data }));
      },
    );
    req.on('timeout', () => req.destroy(new Error('timed out')));
    req.on('error', reject);
    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

async function json(method, url, opts) {
  const res = await request(method, url, opts);
  let parsed = null;
  try {
    parsed = res.body ? JSON.parse(res.body) : null;
  } catch {
    /* left null; the caller reports the status */
  }
  return { status: res.status, body: parsed, raw: res.body };
}

function arg(name, fallback) {
  const hit = process.argv.slice(2).find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : fallback;
}
const flag = (name) => process.argv.slice(2).includes(`--${name}`);

/** Everything a check might read about one card, frozen at run time. */
function cardFacts(detail, printings) {
  const prices = (printings || [])
    .map((p) => Number((p.prices || {}).usd))
    .filter((v) => Number.isFinite(v) && v > 0);

  return {
    name: detail.name,
    oracleId: detail.oracleId,
    colorIdentity: detail.colorIdentity || [],
    cardTypes: detail.cardTypes || [],
    supertypes: detail.supertypes || [],
    subtypes: detail.subtypes || [],
    typeLine: detail.typeLine || null,
    manaValue: detail.manaValue ?? null,
    gameChanger: !!detail.gameChanger,
    oracleText: detail.oracleText || '',
    // Cheapest printing: what the card costs to acquire, which is what a budget means.
    cheapestUsd: prices.length ? Math.min(...prices) : null,
  };
}

(async () => {
  const label = arg('label');
  if (!label) {
    console.error('Pass --label=<name>. Two runs are only comparable if each is named.');
    process.exit(1);
  }

  const username = process.env.EVAL_USERNAME;
  const password = process.env.EVAL_PASSWORD;
  if (!username || !password) {
    console.error('Set EVAL_USERNAME and EVAL_PASSWORD for an account on this API.');
    process.exit(1);
  }

  const only = arg('only');
  const all = JSON.parse(fs.readFileSync(path.join(__dirname, 'cases.json'), 'utf8')).cases;
  const cases = only
    ? all.filter((c) => only.split(',').some((p) => c.id.includes(p.trim())))
    : all;

  if (!cases.length) {
    console.error(`No cases match --only=${only}`);
    process.exit(1);
  }

  const outDir = path.join(RESULTS, label);
  fs.mkdirSync(outDir, { recursive: true });

  const todo = cases.filter(
    (c) => flag('force') || !fs.existsSync(path.join(outDir, `${c.id}.json`)),
  );
  const skipped = cases.length - todo.length;

  console.log(`label "${label}": ${todo.length} case(s) to run${skipped ? `, ${skipped} already recorded` : ''}`);
  if (!todo.length) {
    console.log('Nothing to do. Use --force to re-run, or score.js to read what is here.');
    return;
  }
  // Said out loud, because the number is the whole reason this script is separate from
  // the scorer: builds are two Opus calls each and are not free.
  console.log(`This is ${todo.length * 2} live Opus calls, roughly ${Math.round((todo.length * 125) / 60)} minutes.\n`);

  const login = await json('POST', `${API}/api/auth/login`, { body: { username, password } });
  const token = login.body && login.body.token;
  if (!token) {
    console.error('Login failed:', login.status, String(login.raw).slice(0, 200));
    process.exit(1);
  }

  for (const c of todo) {
    const started = Date.now();
    process.stdout.write(`${c.id.padEnd(20)} `);

    const deck = await json('POST', `${API}/api/decks`, {
      token,
      body: { name: `eval ${label} ${c.id}`, format: 'commander', commanderOracleId: c.oracleId },
    });
    if (!deck.body || !deck.body.id) {
      console.log(`FAILED to create deck (${deck.status})`);
      continue;
    }

    const plan = await json('POST', `${API}/api/decks/${deck.body.id}/ai-build/plan`, {
      token,
      body: {
        commanderOracleId: c.oracleId,
        bracket: c.bracket,
        priceRange: c.priceRange,
        strategy: c.strategy,
      },
    });

    // The eval owns this deck and nothing else does; leaving it behind is the orphan bug
    // the builder itself was fixed for.
    await json('DELETE', `${API}/api/decks/${deck.body.id}`, { token });

    if (plan.status !== 200 || !plan.body) {
      console.log(`FAILED (${plan.status}) ${String(plan.raw).slice(0, 120)}`);
      fs.writeFileSync(
        path.join(outDir, `${c.id}.json`),
        JSON.stringify({ case: c, error: { status: plan.status, body: plan.raw } }, null, 2),
      );
      continue;
    }

    // Snapshot every card the plan names, plus the commander.
    const facts = {};
    const ids = [...new Set([c.oracleId, ...plan.body.cards.map((x) => x.oracleId)])];
    for (const id of ids) {
      const [detail, printings] = await Promise.all([
        json('GET', `${API}/api/cards/${id}`, { token, timeout: 60_000 }),
        json('GET', `${API}/api/cards/${id}/printings`, { token, timeout: 60_000 }),
      ]);
      if (detail.body) facts[id] = cardFacts(detail.body, printings.body);
    }

    const seconds = Math.round((Date.now() - started) / 1000);
    fs.writeFileSync(
      path.join(outDir, `${c.id}.json`),
      JSON.stringify({ case: c, seconds, plan: plan.body, facts }, null, 2),
    );
    console.log(`${plan.body.cards.length} cards, shortfall ${plan.body.mainShortfall}, ${seconds}s`);
  }

  console.log(`\nWrote ${todo.length} result(s) to ${path.relative(process.cwd(), outDir)}`);
  console.log(`Score them with:  node score.js --label=${label}`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
