#!/usr/bin/env node
/**
 * Gate check: no hand-written .cs under MtgEngine.Rules/ or MtgEngine.Cards/ over 500 lines.
 *
 * CARD_ENGINE_PLAN.md § "The four rules, in tests and gate checks" — "files stay readable".
 * A 2,000-line subsystem file is where card-shaped special cases hide, and where two agents
 * collide. Split it.
 *
 * Generated code is exempt: anything under a Generated/ directory, or named *.g.cs. So are
 * bin/ and obj/, which are full of machine output that nobody reads.
 *
 * Exit 1 listing the offenders, exit 0 otherwise. CHECK_ROOT overrides the repo root (used
 * by the hook/tool tests); missing directories are fine — this runs before they exist.
 */

const fs = require('fs');
const path = require('path');

const MAX_LINES = 500;
const ROOTS = ['MtgEngine.Rules', 'MtgEngine.Cards'];

const root = process.env.CHECK_ROOT || path.resolve(__dirname, '..');

const exempt = (rel) => /(^|\/)generated\//i.test(rel) || /\.g\.cs$/i.test(rel) || /(^|\/)(bin|obj)\//i.test(rel);

function walk(dir, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out; // the directory does not exist yet
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) walk(full, out);
    else if (e.isFile() && /\.cs$/i.test(e.name)) out.push(full);
  }
  return out;
}

const offenders = [];
for (const r of ROOTS) {
  for (const file of walk(path.join(root, r), [])) {
    const rel = path.relative(root, file).replace(/\\/g, '/');
    if (exempt(rel)) continue;
    let text;
    try {
      text = fs.readFileSync(file, 'utf8');
    } catch {
      continue;
    }
    const lines = text.split(/\r\n|\n|\r/).length - (text.endsWith('\n') || text.endsWith('\r') ? 1 : 0);
    if (lines > MAX_LINES) offenders.push({ rel, lines });
  }
}

if (offenders.length === 0) {
  console.log(`check-file-size: no .cs over ${MAX_LINES} lines under ${ROOTS.join('/, ')}/`);
  process.exit(0);
}

offenders.sort((a, b) => b.lines - a.lines);
console.error(`check-file-size: ${offenders.length} file(s) over ${MAX_LINES} lines:`);
for (const o of offenders) console.error(`  ${o.lines}  ${o.rel}`);
console.error('Split them by responsibility. Generated/ and *.g.cs are exempt.');
process.exit(1);
