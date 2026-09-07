#!/usr/bin/env node
/**
 * PreToolUse gate: keep a launched agent inside its scope, its build discipline and its
 * time box.
 *
 * CARD_ENGINE_PLAN.md § "Enforcement" — an instruction the assistant is asked to follow is
 * not a control. The briefs tell a builder it owns a list of files, builds scoped, wraps
 * every build in `timeout`, never runs `sed -i` on C# and never touches `git stash`. This
 * hook is what makes those statements true; the brief repeats them so an agent knows why it
 * was stopped, not so that it stops.
 *
 * It reads `<cwd>/.agent/manifest.json`, written by the coordinator before launch:
 *
 *   { "id": "E4", "role": "builder",
 *     "owns": ["MtgEngine.Rules/Combat/**", "tests/MtgEngine.Rules.Tests/CombatTests*.cs"],
 *     "anchor": "CombatTests", "maxToolCalls": 120, "launchedAt": "2026-09-07T14:00:00Z" }
 *
 * The coordinator cannot write that file before launch when the agent gets its own worktree:
 * the harness picks the path and the branch, so there is nothing to write into until the
 * agent is already running. So the manifest is registered ahead of the launch and claimed
 * from inside the worktree — `node tools/ledger.js claim <id>` — and until it is, this hook
 * denies that worktree every write and every command except reading and the claim itself.
 * Otherwise an isolated agent would be indistinguishable from the coordinator and get the
 * run of the repo, which is the loophole this replaces.
 *
 * No manifest in the MAIN checkout means the coordinator's own session: everything is
 * allowed and nothing is written. With a manifest, every call bumps `<cwd>/.agent/calls` and stamps
 * `<cwd>/.agent/last-call` — that file is the heartbeat `tools/sweep.js` reads — and then:
 *
 *   Edit/Write outside `owns`            denied: write it to seam-requests/<id>.md
 *   dotnet build with no project, or .sln  denied: scoped builds only
 *   dotnet test without --filter (builder/verifier)  denied: the full suite is the queue's
 *   dotnet build/test/run not under `timeout`  denied
 *   sed -i on a .cs path, git stash, git checkout outside `owns`  denied
 *   anything past maxToolCalls except commit/status/add/diff and the report  denied
 *
 * Always writable whatever `owns` says: seam-requests/<id>.md, ready/<id>,
 * .agent/report.json — the three ways an agent is allowed to end.
 *
 * Contract: stdin is the hook payload as JSON; exit 0 allows the call, exit 2 blocks it and
 * feeds stderr back to the agent. Anything unexpected allows — a broken gate must not brick
 * every edit in the repo.
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const WRITE_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit']);

/** Bash prefixes an agent past its time box may still run: they are how it stops cleanly. */
const TIME_BOX_ESCAPES = [/^git\s+commit\b/, /^git\s+status\b/, /^git\s+add\b/, /^git\s+diff\b/];

/** Forward slashes, no repeats, lower case — Windows paths arrive spelled several ways. */
const norm = (p) =>
  String(p || '')
    .replace(/\\/g, '/')
    .replace(/\/+/g, '/')
    .toLowerCase();

/** A path relative to the worktree root, or the absolute path when it lies outside it. */
function relTo(cwd, filePath) {
  const target = norm(filePath);
  const root = norm(cwd).replace(/\/$/, '');
  if (root && target.startsWith(root + '/')) return target.slice(root.length + 1);
  return target.replace(/^\.\//, '');
}

/** Glob to regex: `**` crosses separators, `*` and `?` do not. */
function globToRe(glob) {
  const g = norm(glob).replace(/^\.\//, '');
  let re = '';
  for (let i = 0; i < g.length; i++) {
    const ch = g[i];
    if (ch === '*' && g[i + 1] === '*') {
      i++;
      if (g[i + 1] === '/') {
        i++;
        re += '(?:.*/)?';
      } else {
        re += '.*';
      }
    } else if (ch === '*') {
      re += '[^/]*';
    } else if (ch === '?') {
      re += '[^/]';
    } else {
      re += ch.replace(/[.+^${}()|[\]\\]/g, '\\$&');
    }
  }
  return new RegExp('^' + re + '$');
}

const owned = (owns, rel) => owns.some((g) => globToRe(g).test(rel));

function deny(lines) {
  process.stderr.write(lines.filter((l) => l !== undefined).join('\n') + '\n');
  process.exit(2);
}

/** Split a shell line into the pieces that run as separate commands. */
const segmentsOf = (cmd) =>
  String(cmd || '')
    .split(/&&|\|\||;|\n|\|/)
    .map((s) => s.trim())
    .filter(Boolean);

/**
 * `/tmp/` is shared across agents, so it is denied — but `.agent/tmp/`, which the plan
 * tells agents to use instead, must not trip the same rule. Only a `/tmp/` that starts a
 * path counts.
 */
const MENTIONS_TMP = /(?<![\w.\-/\\])(?:\/tmp\/|\\tmp\\)/;

/* ---------------------------------------------------------------------------------------- *
 * An agent worktree that has not claimed its manifest yet.
 * ---------------------------------------------------------------------------------------- */

/** Commands that only read. Matched on the first word, exactly — `./bin/ls` is not `ls`. */
const READ_ONLY_HEADS = new Set([
  'cat',
  'ls',
  'pwd',
  'head',
  'tail',
  'grep',
  'rg',
  'find',
  'wc',
  'echo',
]);

/** …and the subcommands of git that only read. */
const READ_ONLY_GIT = new Set(['status', 'branch', 'log', 'rev-parse', 'worktree', 'diff']);

/** The one thing an unclaimed worktree is for. */
const CLAIM_RE = /^node\s+["']?(?:\.[\\/])?tools[\\/]ledger\.js["']?\s+claim\s+\S+$/;

/**
 * The harness cuts an agent worktree from the commit the session started on, which can be
 * behind the integration branch — behind wave 0 itself, so `tools/ledger.js` is not even
 * there to claim with. A fast-forward to the integration branch is the one write an
 * unclaimed worktree may make, and it cannot lose anything because the branch is brand new.
 */
const SYNC_RE = /^git\s+merge\s+--ff-only\s+card-engine$/;

/**
 * Is `cwd` a *linked* worktree rather than the main checkout? In the main checkout
 * `--git-dir` and `--git-common-dir` resolve to the same directory; in a linked worktree the
 * first is `<main>/.git/worktrees/<name>` and the second is `<main>/.git`.
 *
 * Cannot answer, for any reason: not a linked worktree. A guard that mistakes the
 * coordinator's checkout for an unclaimed agent bricks the session.
 */
function isLinkedWorktree(cwd) {
  // Fast path, so the coordinator does not pay two `git` spawns on every call it makes: a
  // main checkout has `.git` as a directory, a linked worktree has it as a file. Anything
  // else — a subdirectory, no git at all — falls through to the real check.
  try {
    if (fs.statSync(path.join(cwd, '.git')).isDirectory()) return false;
  } catch {
    /* no .git here; ask git */
  }
  try {
    const q = (a) =>
      execSync(`git ${a}`, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    const gitDir = path.resolve(cwd, q('rev-parse --git-dir'));
    const commonDir = path.resolve(cwd, q('rev-parse --git-common-dir'));
    return norm(gitDir) !== norm(commonDir);
  } catch {
    return false;
  }
}

/**
 * One command of a possibly-compound line, from an unclaimed worktree. Every segment has to
 * pass: `cat x && rm -rf y` reads on its first word and is not read-only.
 */
function readOnlyOrClaim(seg) {
  const s = seg.trim();
  if (!s) return true;
  if (CLAIM_RE.test(s) || SYNC_RE.test(s)) return true;

  // A redirection turns any of these into a write — `echo x > Program.cs` reads as `echo`.
  // Descriptor duplication and /dev/null do not, so they do not count.
  const redirects = s.replace(/\d?>+\s*&\s*\d/g, '').replace(/\d?>+\s*\/dev\/null/g, '');
  if (/>/.test(redirects)) return false;

  const words = s.split(/\s+/);
  const head = words[0].toLowerCase();
  if (head === 'git') return READ_ONLY_GIT.has(String(words[1] || '').toLowerCase());
  // `find -exec`/-delete runs arbitrary commands under a read-only name.
  if (head === 'find' && /(^|\s)-(exec|execdir|delete|ok|fprint|fls)\b/.test(s)) return false;
  return READ_ONLY_HEADS.has(head);
}

/**
 * No manifest at `cwd`. Returns when the call may proceed; exits 2 when it may not.
 *
 * The main checkout with no manifest is the coordinator and keeps the run of the repo. A
 * linked worktree with no manifest is an agent that has not claimed yet, and gets nothing but
 * reading and the claim.
 */
function checkUnclaimed(cwd, payload) {
  const tool = payload.tool_name;
  const isWrite = WRITE_TOOLS.has(tool);
  if (!isWrite && tool !== 'Bash') return;
  if (!isLinkedWorktree(cwd)) return;

  if (tool === 'Bash') {
    const cmd = String(payload.tool_input?.command || '');
    if (segmentsOf(cmd).every(readOnlyOrClaim)) return;
  }

  deny([
    'This is an agent worktree with no manifest. Two commands first, in this order:',
    '  git merge --ff-only card-engine',
    '  node tools/ledger.js claim <your id>',
    '',
    'Your id is in your brief; the coordinator registered the manifest before launching you.',
    'Until you claim it nothing here knows what you own, so everything is denied except',
    'reading (cat, ls, head, tail, grep, rg, find, wc, echo, git status/log/diff/rev-parse),',
    'that one fast-forward, and the claim. If the claim says you are not registered, report',
    'that and stop.',
  ]);
}

function checkBash(cmd, manifest, id) {
  const role = String(manifest.role || '');
  const owns = Array.isArray(manifest.owns) ? manifest.owns : [];

  if (MENTIONS_TMP.test(cmd)) {
    deny([
      'Blocked: this command writes under /tmp, which is shared across agents.',
      'Two agents there overwrite each other silently. Use .agent/tmp/ in this worktree.',
    ]);
  }

  if (/\bgit\s+stash\b/.test(cmd)) {
    deny([
      'Blocked: git stash is shared across worktrees — parallel agents pop each other’s',
      'stashes and the failure is silent. Commit on your branch instead.',
    ]);
  }

  if (/\bsed\b/.test(cmd) && /(^|\s)-i\b|(^|\s)--in-place\b/.test(cmd) && /\.cs\b/.test(cmd)) {
    deny([
      'Blocked: sed -i rewrites C# files with LF endings and the analyzers then fail the',
      'build. Files here are CRLF. Use Edit/Write instead.',
    ]);
  }

  for (const seg of segmentsOf(cmd)) {
    const m = seg.match(/(^|\s)git\s+(checkout|restore)\s+(.*)$/);
    if (!m) continue;
    const args = m[3].split(/\s+/).filter(Boolean);
    for (const tok of args) {
      if (tok.startsWith('-')) continue;
      const looksLikePath = /[\\/]/.test(tok) || /\.[a-z0-9]+$/i.test(tok);
      if (!looksLikePath) continue; // a branch or a ref, not a working-tree path
      const rel = relTo(manifest.__cwd, tok);
      if (!owned(owns, rel)) {
        deny([
          `Blocked: git ${m[2]} of ${tok}, which you do not own.`,
          `Reverting another agent’s file destroys its work. write the change you need to seam-requests/${id}.md`,
        ]);
      }
    }
  }

  for (const seg of segmentsOf(cmd)) {
    const dotnet = seg.match(/(^|\s)dotnet\s+(build|test|run)\b/);
    if (!dotnet) continue;
    const verb = dotnet[2];
    const usesBuildScript = /tools\/build\.sh/.test(seg);
    const tokens = seg.split(/\s+/).filter(Boolean);

    if (verb === 'build' && !usesBuildScript) {
      if (tokens.some((t) => /\.sln$/i.test(t))) {
        deny([
          'Blocked: that builds the whole solution. Scoped builds only — one project is 20x',
          'faster and the solution build is the queue’s job.',
          '  timeout 540 tools/build.sh <Project>/<Project>.csproj [<anchor>]',
        ]);
      }
      const namesProject = tokens.some((t, i) => {
        if (i === 0 || t.startsWith('-')) return false;
        if (/\.csproj$/i.test(t)) return true;
        if (/[\\/]/.test(t)) return true;
        try {
          return fs.statSync(path.join(manifest.__cwd, t)).isDirectory();
        } catch {
          return false;
        }
      });
      if (!namesProject) {
        deny([
          'Blocked: dotnet build with no project names the solution by implication. Scoped',
          'builds only:',
          '  timeout 540 tools/build.sh <Project>/<Project>.csproj [<anchor>]',
        ]);
      }
    }

    if (verb === 'test' && !usesBuildScript && (role === 'builder' || role === 'verifier')) {
      if (!/--filter\b/.test(seg)) {
        deny([
          'Blocked: dotnet test without --filter runs the full suite, which is the queue’s',
          'job and long enough to eat your time box. Filter to your anchor:',
          `  timeout 540 dotnet test <test project> --no-build --filter "FullyQualifiedName~${manifest.anchor || '<Anchor>'}"`,
        ]);
      }
    }

    if (!usesBuildScript && !/^timeout\s+\d+\s+/.test(seg)) {
      deny([
        `Blocked: dotnet ${verb} outside a timeout. The build lock is 15 minutes and the`,
        'Bash timeout is 10, so an unwrapped build stalls the whole agent.',
        `  timeout 540 ${seg}`,
        '  (exit 124 means the lock — run it once more.)',
      ]);
    }
  }
}

function main() {
  let payload;
  try {
    payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  } catch {
    process.exit(0); // No readable payload: not our place to block.
  }

  const cwd = payload.cwd || process.cwd();
  const agentDir = path.join(cwd, '.agent');

  let manifest = null;
  try {
    manifest = JSON.parse(fs.readFileSync(path.join(agentDir, 'manifest.json'), 'utf8'));
  } catch {
    manifest = null;
  }
  if (!manifest || typeof manifest !== 'object') {
    // Either the coordinator's own checkout — allowed, and no side effects — or an agent
    // worktree that has not claimed its manifest yet, which may only read and claim.
    checkUnclaimed(cwd, payload);
    process.exit(0);
  }
  manifest.__cwd = cwd;

  const id = String(manifest.id || 'agent');
  const owns = Array.isArray(manifest.owns) ? manifest.owns : [];
  const tool = payload.tool_name;
  const cmd = tool === 'Bash' ? String(payload.tool_input?.command || '') : '';

  // The heartbeat and the turn count: every call, allowed or not.
  let calls = 0;
  try {
    fs.mkdirSync(agentDir, { recursive: true });
    calls = parseInt(fs.readFileSync(path.join(agentDir, 'calls'), 'utf8').trim(), 10) || 0;
  } catch {
    /* first call, or an unreadable counter — start from zero */
  }
  calls += 1;
  try {
    fs.writeFileSync(path.join(agentDir, 'calls'), String(calls) + '\n');
    fs.writeFileSync(path.join(agentDir, 'last-call'), new Date().toISOString() + '\n');
  } catch {
    /* a worktree we cannot write is not a reason to block the call */
  }

  // The three files that are always writable: they are how an agent ends.
  const rel = WRITE_TOOLS.has(tool) ? relTo(cwd, payload.tool_input?.file_path) : '';
  const isExit =
    !!rel &&
    (rel === norm(`seam-requests/${id}.md`) ||
      rel === norm(`ready/${id}`) ||
      rel === '.agent/report.json');

  const escapesTimeBox = isExit || (tool === 'Bash' && TIME_BOX_ESCAPES.some((re) => re.test(cmd)));
  const max = Number(manifest.maxToolCalls);
  if (Number.isFinite(max) && max > 0 && calls > max && !escapesTimeBox) {
    deny([
      `Blocked: time box exceeded: commit what is green, report, stop (call ${calls} of ${max}).`,
      '',
      'Still allowed: git commit / status / add / diff, and writing',
      `  ready/${id}, seam-requests/${id}.md, .agent/report.json`,
    ]);
  }

  // The wall clock, too: a call cap does not bound a builder whose calls are long, and the
  // plan's box is thirty minutes. Past `maxMinutes` (default 35) only the exits remain.
  const minutes = (Date.now() - Date.parse(manifest.launchedAt || '')) / 60000;
  const maxMinutes = Number(manifest.maxMinutes) > 0 ? Number(manifest.maxMinutes) : 35;
  if (Number.isFinite(minutes) && minutes > maxMinutes && !escapesTimeBox) {
    deny([
      `Blocked: time box exceeded: ${Math.round(minutes)} minutes since launch, the box is ${maxMinutes}.`,
      'Commit what is green, write ready/' + id + ' or seam-requests/' + id + '.md, and report.',
      '',
      'Still allowed: git commit / status / add / diff, and writing',
      `  ready/${id}, seam-requests/${id}.md, .agent/report.json`,
    ]);
  }

  if (WRITE_TOOLS.has(tool)) {
    if (!rel || isExit) process.exit(0);
    if (owned(owns, rel)) process.exit(0);
    deny([
      `Blocked: ${tool} on ${payload.tool_input.file_path}, which is not yours.`,
      '',
      `You own: ${owns.length ? owns.join(', ') : '(nothing declared)'}`,
      '',
      `If you need a seam changed, write the change you need to seam-requests/${id}.md and`,
      'stop that thread. Do not edit another agent’s files: the merge is clean and the',
      'runtime is not.',
    ]);
  }

  if (tool === 'Bash') checkBash(cmd, manifest, id);

  process.exit(0);
}

main();
