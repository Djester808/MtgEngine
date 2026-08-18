#!/usr/bin/env node
/**
 * Stop gate: before a task is allowed to be finished, the standards that have no build
 * gate until commit time get run.
 *
 * `require-docs.js` makes sure the relevant doc has been *read*. This session proved that
 * is not enough. The client's standards were read in full, before a line was written, and
 * then broken four separate times in the same change: a centred empty state and a loading
 * state written out by hand beside the mixins that already existed, an ellipsis on a name
 * where the rule puts ellipsis last, three copies of one rule-rendering block, and an
 * entire feature — three list modes, two detail panes and a full-screen sheet — added
 * without a single entry in the capture harness, so the phone audit ran and truthfully
 * reported no change. Every one was found only because a human went looking, turns later.
 *
 * Why Stop and not PostToolUse: running per-edit means firing halfway through writing a
 * stylesheet, when a block legitimately does not resemble its final self yet. Nothing is
 * finished mid-iteration, so nothing should be judged there. This runs once, at the point
 * the work is being handed back, which is the last moment the change is still in hand.
 *
 * Contract: stdin is the hook payload as JSON; exit 0 lets the turn end, exit 2 blocks it
 * and feeds stderr back to the assistant to act on. Anything unexpected exits 0 — a gate
 * that cannot fail open would make the repo unworkable the first time it broke.
 */

const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

/**
 * Checks that are cheap enough to run on every completed task.
 *
 * The full `npm run verify` takes minutes and `verify:ui` needs both servers up; neither
 * belongs on a hook. These two are static, diff-scoped and ratcheted against a recorded
 * baseline, so they cost a few hundred milliseconds and only speak up about something new.
 */
const CHECKS = [
  {
    // The Angular client is a *sibling* checkout, not a folder inside this repo, and it
    // is its own git repository. Resolving it against the project dir and asking this
    // repo's git about its files is why the first version of this hook silently passed
    // on a change it should have blocked: nothing matched, so nothing ran.
    pkg: '../mtg-client',
    script: 'tools/check-shared-treatments.js',
    when: /^src\/.*\.scss$/,
  },
  {
    pkg: '../mtg-client',
    script: 'tools/check-ui-states.js',
    when: /^src\/.*\.component\.ts$/,
  },
];

function readPayload() {
  try {
    return JSON.parse(fs.readFileSync(0, 'utf8'));
  } catch {
    return null;
  }
}

/** Everything a package has touched, so a check with nothing to say is never run. */
function changedFiles(pkgDir) {
  const git = (args) => {
    try {
      return execFileSync('git', args, { cwd: pkgDir, encoding: 'utf8' });
    } catch {
      return '';
    }
  };

  return [
    ...git(['diff', '--name-only', '--diff-filter=ACMR', 'HEAD']).split('\n'),
    ...git(['ls-files', '--others', '--exclude-standard']).split('\n'),
  ]
    .map((f) => f.trim().replace(/\\/g, '/').toLowerCase())
    .filter(Boolean);
}

function main() {
  const payload = readPayload() || {};

  // Set once this hook has already blocked a stop. Without honouring it, a finding this
  // gate cannot fix by itself would trap the turn in a loop it can never leave.
  if (payload.stop_hook_active) process.exit(0);

  const projectDir = payload.cwd || process.cwd();
  const failures = [];

  for (const check of CHECKS) {
    // Two bases, because a hook that quietly does nothing is worse than no hook and this
    // one already did: given a POSIX-shaped cwd, path.resolve produced C:\c\Users\... ,
    // the package "did not exist", every check was skipped and the gate reported success
    // on a change it should have blocked.
    const cwd = [projectDir, process.cwd()]
      .map((base) => path.resolve(base, check.pkg))
      .find((dir) => fs.existsSync(path.join(dir, check.script)));

    if (!cwd) continue;

    // Ask that package's own git, about paths relative to that package.
    if (!changedFiles(cwd).some((f) => check.when.test(f))) continue;

    try {
      execFileSync('node', [check.script], { cwd, encoding: 'utf8', stdio: 'pipe' });
    } catch (err) {
      // The checks print their finding and their remedy already; passing that through
      // verbatim keeps one explanation of each rule rather than two that drift apart.
      const output = `${err.stdout || ''}${err.stderr || ''}`.trim();
      if (output) failures.push(output);
    }
  }

  if (failures.length === 0) process.exit(0);

  process.stderr.write(
    [
      'Not finished: a standard in CLAUDE.md is being broken by this change.',
      '',
      ...failures,
      'These are the rules with no build gate until commit time, which is exactly why they',
      'are the ones that keep getting skipped. Fix them now, while the change is in hand.',
      'If a finding is wrong, say so and why — do not work around it by editing elsewhere.',
    ].join('\n') + '\n',
  );
  process.exit(2);
}

main();
