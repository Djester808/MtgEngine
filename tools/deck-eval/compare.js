#!/usr/bin/env node
//
// Diffs two recorded runs, check by check.
//
//   node compare.js --before=baseline --after=low-effort-assessment
//
// The reason the harness exists. A prompt or model change is otherwise judged on one build
// and a feeling about it, which is how a sentence claiming Sol Ring was a Game Changer
// survived in the prompt for months.

const fs = require('fs');
const path = require('path');
const { score } = require('./checks');

const RESULTS = path.join(__dirname, 'results');

function arg(name) {
  const hit = process.argv.slice(2).find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : null;
}

function load(label) {
  const dir = path.join(RESULTS, label);
  if (!fs.existsSync(dir)) {
    console.error(`No run named "${label}".`);
    process.exit(1);
  }
  const out = new Map();
  for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.json'))) {
    const r = score(JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
    out.set(r.id, r);
  }
  return out;
}

const beforeLabel = arg('before');
const afterLabel = arg('after');
if (!beforeLabel || !afterLabel) {
  console.error('Pass --before=<label> --after=<label>.');
  process.exit(1);
}

const before = load(beforeLabel);
const after = load(afterLabel);

const shared = [...after.keys()].filter((id) => before.has(id));
const onlyAfter = [...after.keys()].filter((id) => !before.has(id));
const onlyBefore = [...before.keys()].filter((id) => !after.has(id));

console.log(`${beforeLabel} -> ${afterLabel}   (${shared.length} case(s) in both)\n`);

let better = 0;
let worse = 0;
let secondsBefore = 0;
let secondsAfter = 0;

for (const id of shared) {
  const b = before.get(id);
  const a = after.get(id);
  secondsBefore += b.seconds || 0;
  secondsAfter += a.seconds || 0;

  const lines = [];
  const bByName = new Map(b.checks.map((c) => [c.name, c]));

  for (const ac of a.checks) {
    const bc = bByName.get(ac.name);
    if (!bc) continue;

    if (ac.kind === 'band' && ac.value !== bc.value) {
      const arrow = ac.ok === bc.ok ? '   ' : ac.ok ? '  +' : '  -';
      if (ac.ok !== bc.ok) (ac.ok ? better++ : worse++);
      lines.push(`${arrow} ${ac.name.padEnd(17)} ${bc.value} -> ${ac.value}` +
        (ac.ok === bc.ok ? '' : ac.ok ? '  (now in band)' : '  (now out of band)'));
    } else if (ac.kind === 'hard' && ac.ok !== bc.ok) {
      (ac.ok ? better++ : worse++);
      lines.push(`${ac.ok ? '  +' : '  -'} ${ac.name.padEnd(17)} ${bc.ok ? 'was ok' : 'was FAIL'} -> ${ac.ok ? 'ok' : 'FAIL'}   ${ac.detail}`);
    }
  }

  const dt = (a.seconds || 0) - (b.seconds || 0);
  const timing = Math.abs(dt) >= 5 ? `   ${dt > 0 ? '+' : ''}${dt}s` : '';
  if (lines.length || timing) {
    console.log(`${id}${timing}`);
    lines.forEach((l) => console.log(l));
    console.log();
  }
}

if (onlyAfter.length) console.log(`only in ${afterLabel}: ${onlyAfter.join(', ')}`);
if (onlyBefore.length) console.log(`only in ${beforeLabel}: ${onlyBefore.join(', ')}`);

console.log(`${better} check(s) improved, ${worse} regressed.`);
if (secondsBefore && secondsAfter) {
  const pct = Math.round(((secondsAfter - secondsBefore) / secondsBefore) * 100);
  console.log(`total build time ${secondsBefore}s -> ${secondsAfter}s (${pct > 0 ? '+' : ''}${pct}%)`);
}

process.exit(worse === 0 ? 0 : 1);
