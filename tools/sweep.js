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

const rows = [];
for (const wt of worktrees()) {
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
  const [action, why] = verdict({ ready, sinceCall, sinceLaunch });
  rows.push({ id, role: String(manifest.role || '?'), sinceCall, sinceLaunch, ready, action, why, wt });
}

if (rows.length === 0) {
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
const acting = rows.filter((r) => r.action !== 'nothing');
console.log('');
console.log(`sweep: ${rows.length} agent(s), ${acting.length} needing action. Read-only — nothing was killed.`);
for (const r of acting) console.log(`  ${r.action} ${r.id}  (${r.wt})`);
