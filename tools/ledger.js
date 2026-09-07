#!/usr/bin/env node
/**
 * The launch ledger: append-only JSON Lines at .agents/ledger.jsonl (untracked).
 *
 * CARD_ENGINE_PLAN.md § "Orchestration" — every launch is recorded so a lost notification
 * does not become a duplicate launch, and after a rate-limit reset the ledger is read before
 * anything is launched. Append-only on purpose: the history of a killed-and-relaunched agent
 * is the thing you need at 2am, not its current row.
 *
 *   node tools/ledger.js add <id> <role> <worktree> [note]
 *   node tools/ledger.js set <id> <state> [note]      launched|ready|merged|killed|blocked
 *   node tools/ledger.js list
 */

const fs = require('fs');
const path = require('path');

const STATES = ['launched', 'ready', 'merged', 'killed', 'blocked'];
const repoRoot = path.resolve(__dirname, '..');
const dir = path.join(repoRoot, '.agents');
const file = path.join(dir, 'ledger.jsonl');

function usage(msg) {
  if (msg) console.error(`ledger: ${msg}`);
  console.error('usage: node tools/ledger.js add <id> <role> <worktree> [note]');
  console.error('       node tools/ledger.js set <id> <state> [note]   # ' + STATES.join('|'));
  console.error('       node tools/ledger.js list');
  process.exit(64);
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

if (cmd === 'add') {
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
