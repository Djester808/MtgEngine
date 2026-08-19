#!/usr/bin/env node
//
// Scores a recorded run. Free, offline, and re-runnable.
//
//   node score.js --label=baseline [--verbose]
//
// Separate from run.js on purpose: builds cost money and checks do not. Adding a check
// and re-scoring every run you have ever recorded costs nothing, which is the only way
// the check set can grow without the evidence base going stale.

const fs = require('fs');
const path = require('path');
const { score } = require('./checks');

const RESULTS = path.join(__dirname, 'results');

function arg(name, fallback) {
  const hit = process.argv.slice(2).find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : fallback;
}
const verbose = process.argv.slice(2).includes('--verbose');

function loadRun(label) {
  const dir = path.join(RESULTS, label);
  if (!fs.existsSync(dir)) {
    console.error(`No run named "${label}". Recorded runs: ${listRuns().join(', ') || '(none)'}`);
    process.exit(1);
  }
  return fs
    .readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => score(JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'))));
}

function listRuns() {
  return fs.existsSync(RESULTS) ? fs.readdirSync(RESULTS) : [];
}

const mark = (c) => (c.ok ? 'ok  ' : c.kind === 'hard' ? 'FAIL' : 'out ');

function render(results) {
  let hardFails = 0;
  let bandMisses = 0;

  for (const r of results) {
    if (r.error) {
      console.log(`\n${r.id}\n   ${r.error}`);
      hardFails++;
      continue;
    }

    const a = r.assessment || {};
    console.log(
      `\n${r.id}   (${r.seconds}s, interaction ${r.interactionSplit})` +
        `\n   assessment: ${a.findings} finding(s), ${a.withFix} with a fix, ` +
        `${a.doctrineCitations} doctrine citation(s)`,
    );
    for (const c of r.checks) {
      if (c.kind === 'hard') {
        if (!c.ok) hardFails++;
        if (c.ok && !verbose) {
          console.log(`   ${mark(c)} ${c.name.padEnd(17)} ${c.detail}`);
        } else {
          console.log(`   ${mark(c)} ${c.name.padEnd(17)} ${c.detail}   [${c.rule}]`);
        }
      } else {
        if (!c.ok) bandMisses++;
        const range = c.hi >= 99 ? `>= ${c.lo}` : `${c.lo}-${c.hi}`;
        console.log(`   ${mark(c)} ${c.name.padEnd(17)} ${String(c.value).padEnd(4)} want ${range}   [${c.rule}]`);
      }
    }
  }

  console.log(
    `\n${results.length} case(s): ${hardFails} hard failure(s), ${bandMisses} band(s) out.`,
  );
  // Out-of-band is information, not a verdict — see the header in checks.js. Only a hard
  // failure is a defect, so only a hard failure sets the exit code.
  return hardFails === 0 ? 0 : 1;
}

const label = arg('label');
if (!label) {
  console.error(`Pass --label=<name>. Recorded runs: ${listRuns().join(', ') || '(none)'}`);
  process.exit(1);
}

process.exit(render(loadRun(label)));
