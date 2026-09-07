#!/usr/bin/env node
/**
 * The idle sweep, read-only: one line per launched agent, with the action the plan's table
 * says to take. It prints actions; it never performs them — killing an agent and its test
 * hosts is the coordinator's call, and a script that kills on a stale clock would kill an
 * agent mid-merge.
 *
 * CARD_ENGINE_PLAN.md § "Idle agents":
 *
 *   heartbeat under 10 min                          nothing
 *   10-20 min and no ready/<id>                     message the agent: report or stop
 *   over 20 min, or over 30 min since launchedAt    kill it, kill its test hosts by pid,
 *                                                   ledger killed:<reason>, relaunch narrower
 *
 * The heartbeat is .agent/last-call, stamped by .claude/hooks/agent-guard.js on every tool
 * call. An agent with a ready/<id> marker is finished, not idle — the queue merges it.
 *
 * It also prints the other failure a launch has: registered and never claimed. An isolated
 * agent claims its manifest from inside its own worktree (`node tools/ledger.js claim <id>`),
 * so a registration in .agents/manifests with no worktree carrying it means the agent died
 * before its first command, or never read its brief. There is no heartbeat to go stale for
 * that one, so without this line it is invisible.
 *
 *   node tools/sweep.js
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/** Every worktree of this repo, main one included. */
function worktrees() {
  let out = '';
  try {
    out = execSync('git worktree list --porcelain', { encoding: 'utf8' });
  } catch {
    return [process.cwd()];
  }
  const dirs = [];
  for (const line of out.split('\n')) {
    if (line.startsWith('worktree ')) dirs.push(line.slice('worktree '.length).trim());
  }
  return dirs;
}

const minutesSince = (iso) => {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return null;
  return (Date.now() - t) / 60000;
};

const show = (m) => (m === null ? '  -  ' : m.toFixed(1).padStart(5));

function verdict({ ready, sinceCall, sinceLaunch }) {
  if (ready) return ['nothing', 'ready marker written — the queue merges it'];
  const stale = sinceCall === null ? Infinity : sinceCall;
  if (stale > 20)
    return ['kill', `heartbeat ${show(sinceCall).trim()}m: kill it, kill its test hosts by pid, ledger killed:idle, relaunch narrower`];
  if (sinceLaunch !== null && sinceLaunch > 30)
    return ['kill', `${show(sinceLaunch).trim()}m since launch: kill it, ledger killed:over-box, relaunch narrower`];
  if (stale >= 10) return ['message', 'report or stop'];
  return ['nothing', ''];
}

const trees = worktrees();
/** git lists the main checkout first, whichever worktree you ask from. */
const mainCheckout = trees[0] || process.cwd();

/**
 * The ledger's last word on each id. A merged, killed or blocked agent is finished: its
 * heartbeat is stale forever, and reporting it as "kill" every sweep buried the live rows
 * under a dozen dead ones (wave 1). Those worktrees are for pruning, not killing.
 */
function ledgerStates() {
  const states = new Map();
  let text = '';
  try {
    text = fs.readFileSync(path.join(mainCheckout, '.agents', 'ledger.jsonl'), 'utf8');
  } catch {
    return states;
  }
  for (const line of text.split('\n')) {
    if (!line.trim()) continue;
    try {
      const entry = JSON.parse(line);
      if (entry.id && entry.state) states.set(String(entry.id), String(entry.state));
    } catch {
      /* a torn line at the tail while the ledger is being written */
    }
  }
  return states;
}

const FINISHED = new Set(['merged', 'killed', 'blocked']);
const states = ledgerStates();

const rows = [];
for (const wt of trees) {
  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(path.join(wt, '.agent', 'manifest.json'), 'utf8'));
  } catch {
    continue; // no manifest: the coordinator's own checkout, or a worktree between agents
  }
  const id = String(manifest.id || '?');
  let lastCall = null;
  try {
    lastCall = fs.readFileSync(path.join(wt, '.agent', 'last-call'), 'utf8').trim();
  } catch {
    /* never made a call, or the counter is unwritable */
  }
  const ready = fs.existsSync(path.join(wt, 'ready', id));
  const sinceCall = lastCall ? minutesSince(lastCall) : null;
  const sinceLaunch = manifest.launchedAt ? minutesSince(manifest.launchedAt) : null;
  const state = states.get(id) || null;
  const [action, why] = FINISHED.has(state)
    ? ['prune', `ledger ${state}: finished — git worktree remove --force when its branch is no longer needed`]
    : verdict({ ready, sinceCall, sinceLaunch });
  rows.push({ id, role: String(manifest.role || '?'), sinceCall, sinceLaunch, ready, action, why, wt, state });
}

/** Registered by the coordinator, and no worktree is carrying that manifest. */
const claimed = new Set(rows.map((r) => r.id));
const unclaimed = [];
const registryDir = path.join(mainCheckout, '.agents', 'manifests');
let registryFiles = [];
try {
  registryFiles = fs.readdirSync(registryDir).filter((f) => f.toLowerCase().endsWith('.json'));
} catch {
  /* nothing registered yet */
}
for (const f of registryFiles) {
  let m = null;
  try {
    m = JSON.parse(fs.readFileSync(path.join(registryDir, f), 'utf8'));
  } catch {
    /* unreadable registration — still worth naming, by its file */
  }
  const id = String((m && m.id) || path.basename(f, path.extname(f)));
  // A finished agent whose worktree was pruned still has its registration; that is history, not a lost launch.
  if (!claimed.has(id) && !FINISHED.has(states.get(id) || null)) unclaimed.push({ id, role: String((m && m.role) || '?') });
}
unclaimed.sort((a, b) => a.id.localeCompare(b.id));

if (rows.length === 0 && unclaimed.length === 0) {
  console.log('sweep: no worktree carries .agent/manifest.json — nothing launched');
  process.exit(0);
}

const pad = (s, n) => String(s).padEnd(n);
console.log(`${pad('id', 8)}${pad('role', 11)}${pad('idle', 7)}${pad('age', 7)}${pad('ready', 7)}${pad('action', 9)}why`);
for (const r of rows.sort((a, b) => a.id.localeCompare(b.id))) {
  console.log(
    `${pad(r.id, 8)}${pad(r.role, 11)}${pad(show(r.sinceCall), 7)}${pad(show(r.sinceLaunch), 7)}${pad(r.ready ? 'yes' : 'no', 7)}${pad(r.action, 9)}${r.why}`
  );
}
for (const r of unclaimed) {
  console.log(
    `${pad(r.id, 8)}${pad(r.role, 11)}${pad('  -  ', 7)}${pad('  -  ', 7)}${pad('no', 7)}${pad('check', 9)}registered, not claimed — no worktree carries this manifest`
  );
}
const acting = rows.filter((r) => r.action !== 'nothing' && r.action !== 'prune');
const prunable = rows.filter((r) => r.action === 'prune');
console.log('');
const tail = unclaimed.length ? `, ${unclaimed.length} registered, not claimed` : '';
const pruneTail = prunable.length ? `, ${prunable.length} finished (prune)` : '';
console.log(
  `sweep: ${rows.length} agent(s), ${acting.length} needing action${tail}${pruneTail}. Read-only — nothing was killed.`
);
for (const r of acting) console.log(`  ${r.action} ${r.id}  (${r.wt})`);
if (prunable.length) console.log(`  prune: ${prunable.map((r) => r.id).join(' ')}`);
for (const r of unclaimed) {
  console.log(`  check ${r.id}  (registered, not claimed — it never ran, or died before its first command)`);
}
