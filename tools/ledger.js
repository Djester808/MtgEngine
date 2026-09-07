#!/usr/bin/env node
/**
 * The launch ledger and the manifest registry.
 *
 * CARD_ENGINE_PLAN.md § "Orchestration" — every launch is recorded so a lost notification
 * does not become a duplicate launch, and after a rate-limit reset the ledger is read before
 * anything is launched. Append-only on purpose: the history of a killed-and-relaunched agent
 * is the thing you need at 2am, not its current row.
 *
 * CARD_ENGINE_PLAN.md § "Enforcement" — the plan says the coordinator writes
 * `<worktree>/.agent/manifest.json` before launch. It cannot: an agent launched with
 * worktree isolation gets a fresh linked worktree whose path and branch nothing knows in
 * advance, so there is no directory to write into until the agent is already running. That
 * left a hole — a linked worktree with no manifest looked exactly like the coordinator's own
 * checkout to `agent-guard.js`, and everything was allowed. So the manifest is *registered*
 * by the coordinator ahead of the launch and *claimed* by the agent once it knows where it
 * is:
 *
 *   node tools/ledger.js register E4 builder CombatTests 120 "MtgEngine.Rules/Combat/**" ...
 *      -> .agents/manifests/E4.json in the MAIN checkout (the coordinator's)
 *   node tools/ledger.js claim E4          (run by the agent, inside its own worktree)
 *      -> <cwd>/.agent/manifest.json, stamped launchedAt/claimedIn, call counter zeroed
 *
 * Until it claims, `agent-guard.js` denies the agent every write and every command but the
 * claim itself — so claiming is not a courtesy, it is the only way out of the worktree's
 * front door. `tools/sweep.js` prints a registration nobody claimed.
 *
 * Every path here resolves against the MAIN checkout, whichever worktree the command runs
 * in: one registry and one ledger, or the coordinator cannot see what its agents did.
 *
 *   node tools/ledger.js register <id> <role> <anchor> <maxToolCalls> <owns-glob> [<owns-glob>...]
 *   node tools/ledger.js claim <id>
 *   node tools/ledger.js add <id> <role> <worktree> [note]
 *   node tools/ledger.js set <id> <state> [note]      launched|claimed|ready|merged|killed|blocked
 *   node tools/ledger.js list
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const STATES = ['launched', 'claimed', 'ready', 'merged', 'killed', 'blocked'];

/** The checkout this script was invoked from — may be a linked worktree. */
const here = path.resolve(__dirname, '..');

/**
 * The main checkout: the first entry of `git worktree list --porcelain`, which git always
 * lists first whichever worktree you ask from. Falls back to this checkout when git is not
 * available or this is not a repo — then it is the only checkout there is.
 */
function mainCheckout() {
  try {
    const out = execSync('git worktree list --porcelain', {
      cwd: here,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    for (const line of out.split('\n')) {
      if (line.startsWith('worktree ')) return path.resolve(line.slice('worktree '.length).trim());
    }
  } catch {
    /* no git, or not a repo */
  }
  return here;
}

const root = mainCheckout();
const dir = path.join(root, '.agents');
const file = path.join(dir, 'ledger.jsonl');
const manifestsDir = path.join(dir, 'manifests');

function usage(msg) {
  if (msg) console.error(`ledger: ${msg}`);
  console.error(
    'usage: node tools/ledger.js register <id> <role> <anchor> <maxToolCalls> <owns-glob> [<owns-glob>...]'
  );
  console.error('       node tools/ledger.js claim <id>          # run inside the agent worktree');
  console.error('       node tools/ledger.js add <id> <role> <worktree> [note]');
  console.error('       node tools/ledger.js set <id> <state> [note]   # ' + STATES.join('|'));
  console.error('       node tools/ledger.js list');
  process.exit(64);
}

/**
 * An id becomes a file name under `.agents/manifests/`, so it may not carry a separator or
 * climb out of the directory.
 */
function checkId(id) {
  if (!id) usage('an agent id is required');
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(id) || id.includes('..')) {
    usage(`'${id}' is not a usable agent id — letters, digits, dot, dash and underscore only`);
  }
  return id;
}

function append(row) {
  fs.mkdirSync(dir, { recursive: true });
  fs.appendFileSync(file, JSON.stringify(row) + '\n', 'utf8');
}

function rows() {
  if (!fs.existsSync(file)) return [];
  return fs
    .readFileSync(file, 'utf8')
    .split('\n')
    .filter((l) => l.trim())
    .map((l) => {
      try {
        return JSON.parse(l);
      } catch {
        return null;
      }
    })
    .filter(Boolean);
}

const [cmd, ...args] = process.argv.slice(2);
const now = new Date().toISOString();

if (cmd === 'register') {
  const [id, role, anchor, maxRaw, ...owns] = args;
  if (!id || !role || !anchor || !maxRaw || owns.length === 0) {
    usage('register needs <id> <role> <anchor> <maxToolCalls> and at least one <owns-glob>');
  }
  checkId(id);
  const maxToolCalls = Number(maxRaw);
  if (!Number.isInteger(maxToolCalls) || maxToolCalls <= 0) {
    usage(`maxToolCalls must be a positive whole number, got '${maxRaw}'`);
  }

  fs.mkdirSync(manifestsDir, { recursive: true });
  const target = path.join(manifestsDir, `${id}.json`);
  const replaced = fs.existsSync(target);
  const manifest = { id, role, anchor, owns, maxToolCalls, registeredAt: now };
  fs.writeFileSync(target, JSON.stringify(manifest, null, 2) + '\n', 'utf8');

  console.log(`ledger: ${id} ${replaced ? 're-registered' : 'registered'} as ${role} (${target})`);
  console.log(`        anchor ${anchor}, maxToolCalls ${maxToolCalls}, owns ${owns.join(', ')}`);
  console.log(`        the agent claims it with: node tools/ledger.js claim ${id}`);
} else if (cmd === 'claim') {
  const [id] = args;
  checkId(id);

  const src = path.join(manifestsDir, `${id}.json`);
  let registered;
  try {
    registered = JSON.parse(fs.readFileSync(src, 'utf8'));
  } catch {
    console.error(`ledger: ${id} is not registered — no manifest at ${src}`);
    console.error('The coordinator registers an agent before it can claim:');
    console.error(
      '  node tools/ledger.js register <id> <role> <anchor> <maxToolCalls> <owns-glob> [...]'
    );
    console.error('Check the id in your brief, and ask the coordinator if it is right.');
    process.exit(65);
  }
  if (!registered || typeof registered !== 'object') {
    console.error(`ledger: ${src} is not a usable manifest — ask the coordinator to re-register.`);
    process.exit(65);
  }

  // The worktree is wherever the agent is standing. agent-guard.js reads the manifest at the
  // cwd of the call it is gating, and matches `owns` relative to it, so the two must agree.
  const worktree = process.cwd();
  let top = null;
  try {
    top = execSync('git rev-parse --show-toplevel', {
      cwd: worktree,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    }).trim();
  } catch {
    /* not a repo, or no git — nothing to compare against */
  }
  if (top && path.resolve(top) !== path.resolve(worktree)) {
    console.error(`ledger: warning — ${worktree} is not the worktree root (${top}).`);
    console.error('        Your owns globs are matched relative to the manifest, so claim from');
    console.error('        the worktree root or the guard will read the wrong paths.');
  }

  const agentDir = path.join(worktree, '.agent');
  const dest = path.join(agentDir, 'manifest.json');

  // Idempotent: a second claim keeps the first launchedAt, and keeps the call count. Both are
  // the time box, and a re-claim that reset them would be a way to buy another one.
  let launchedAt = now;
  let reclaim = false;
  try {
    const prev = JSON.parse(fs.readFileSync(dest, 'utf8'));
    if (prev && prev.id === id && typeof prev.launchedAt === 'string' && prev.launchedAt.trim()) {
      launchedAt = prev.launchedAt;
      reclaim = true;
    }
  } catch {
    /* first claim in this worktree */
  }

  fs.mkdirSync(agentDir, { recursive: true });
  fs.writeFileSync(
    dest,
    JSON.stringify({ ...registered, id, launchedAt, claimedIn: worktree, claimedAt: now }, null, 2) +
      '\n',
    'utf8'
  );
  if (!reclaim) fs.writeFileSync(path.join(agentDir, 'calls'), '0\n', 'utf8');
  // The claim is the first heartbeat: the guard only stamps guarded calls, and an agent that
  // spends its first minutes reading makes none, which the sweep would otherwise read as idle.
  fs.writeFileSync(path.join(agentDir, 'last-call'), now + '\n', 'utf8');

  append({
    ts: now,
    action: 'claim',
    id,
    role: registered.role || '?',
    worktree,
    state: 'claimed',
    note: reclaim ? 're-claim: launchedAt and call count kept' : undefined,
  });

  console.log(`ledger: ${id} claimed ${worktree}`);
  console.log(`        manifest ${dest}`);
  console.log(
    reclaim
      ? `        already claimed at ${launchedAt} — time box and call count unchanged`
      : `        launchedAt ${launchedAt}, call count reset to 0`
  );
  console.log(
    `        you own: ${Array.isArray(registered.owns) && registered.owns.length ? registered.owns.join(', ') : '(nothing declared)'}`
  );
} else if (cmd === 'add') {
  const [id, role, worktree, ...rest] = args;
  if (!id || !role || !worktree) usage('add needs <id> <role> <worktree>');
  append({ ts: now, action: 'add', id, role, worktree, state: 'launched', note: rest.join(' ') || undefined });
  console.log(`ledger: ${id} added as ${role} in ${worktree}`);
} else if (cmd === 'set') {
  const [id, state, ...rest] = args;
  if (!id || !state) usage('set needs <id> <state>');
  if (!STATES.includes(state)) usage(`unknown state '${state}' — one of ${STATES.join(', ')}`);
  append({ ts: now, action: 'set', id, state, note: rest.join(' ') || undefined });
  console.log(`ledger: ${id} -> ${state}`);
} else if (cmd === 'list') {
  const latest = new Map();
  for (const r of rows()) {
    const prev = latest.get(r.id) || {};
    latest.set(r.id, {
      id: r.id,
      role: r.role || prev.role || '?',
      worktree: r.worktree || prev.worktree || '?',
      state: r.state || prev.state || '?',
      ts: r.ts || prev.ts,
      note: r.note !== undefined ? r.note : prev.note,
    });
  }
  if (latest.size === 0) {
    console.log('ledger: empty (no agents recorded)');
    process.exit(0);
  }
  const pad = (s, n) => String(s).padEnd(n);
  const stamp = (ts) => String(ts || '').slice(0, 16).replace('T', ' ');
  console.log(`${pad('id', 8)}${pad('role', 12)}${pad('state', 10)}${pad('since', 18)}worktree / note`);
  for (const a of [...latest.values()].sort((x, y) => String(x.id).localeCompare(String(y.id)))) {
    const tail = a.note ? `${a.worktree}  (${a.note})` : a.worktree;
    console.log(`${pad(a.id, 8)}${pad(a.role, 12)}${pad(a.state, 10)}${pad(stamp(a.ts), 18)}${tail}`);
  }
} else {
  usage(cmd ? `unknown command '${cmd}'` : undefined);
}
