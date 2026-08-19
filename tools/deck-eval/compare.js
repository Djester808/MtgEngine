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
const { score, scoreSuggestions } = require('./checks');

/** A recorded case is either a build or a shortlist; they fail differently. */
const scoreRecord = (r) => (r.case.kind === 'suggestions' ? scoreSuggestions(r) : score(r));

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
    const r = scoreRecord(JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
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

console.log(`${beforeLabel} -> ${afterLabel}   (${shared.length} case(s) in both)`);
// Said every time, because it is the easiest mistake to make with this output: the build
// is its own model call and re-runs non-deterministically, so deck checks move between any
// two runs whether or not the change under test could reach them. Attribute a difference
// only to a change that could plausibly have caused it, and trust several cases moving the
// same way over one moving a lot.
console.log('Deck checks carry build-to-build variance. Read them as a trend, not a verdict.\n');

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

  const ab = b.assessment || {};
  const aa = a.assessment || {};
  for (const [k, label] of [
    ['findings', 'assess:findings'],
    ['withFix', 'assess:with-fix'],
    ['doctrineCitations', 'assess:citations'],
  ]) {
    if ((ab[k] ?? 0) !== (aa[k] ?? 0)) {
      lines.push(`    ${label.padEnd(17)} ${ab[k] ?? 0} -> ${aa[k] ?? 0}`);
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
