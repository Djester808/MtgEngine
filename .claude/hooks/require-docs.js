#!/usr/bin/env node
/**
 * PreToolUse gate: refuse to edit a file whose governing doc has not been read this
 * session.
 *
 * CLAUDE.md asks the assistant to read the relevant knowledge doc before writing code in
 * the area it covers. That instruction was in the file for a while and was still being
 * skipped — an instruction the assistant is asked to follow is not a control. This makes
 * it one, the same way the build gate and the arch tests are.
 *
 * It reads the session transcript for an actual `Read` of the doc, so it cannot be
 * satisfied by claiming to have read something.
 *
 * Contract: stdin is the hook payload as JSON; exit 0 allows the call, exit 2 blocks it
 * and feeds stderr back to the assistant. Anything unexpected allows — a broken gate must
 * not brick every edit in the repo.
 */

const fs = require('fs');
const path = require('path');

/** Tools that write to a file. Everything else is none of this hook's business. */
const WRITE_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit']);

/**
 * Which docs govern which files.
 *
 * `test` matches against the edited path, normalised to forward slashes and lower case.
 * `docs` are repo-relative; they are matched against reads by trailing path, so the two
 * CLAUDE.md files stay distinct — reading the API's does not satisfy the client's.
 */
const RULES = [
  {
    test: /(^|\/)mtg-client\/src\//,
    docs: ['mtg-client/CLAUDE.md'],
    why: "The client's standards are not auto-loaded when you work from the API repo.",
  },
  {
    test: /(aibuildservice|synergyservice|decksuggestionsservice|candidateranking)\.cs$/,
    docs: ['MtgEngine.Api/Knowledge/commander-doctrine.md'],
    why: 'This code reasons from the doctrine, which is injected verbatim into prompts.',
  },
  {
    test: /(collectionservice|collectioncard|collectionscontroller|pricehistoryservice|pricesnapshotworker)\.cs$/,
    docs: ['CARD_COLLECTION_FEATURE.md'],
    why: 'The collection/price domain and its DTO shapes are defined there.',
  },
  {
    test: /(profileservice|profilecontroller|userscontroller|avatarimage|profiledtos)\.cs$/,
    docs: ['USER_PROFILE_FEATURE.md'],
    why: 'Which profile fields are public and which are owner-only is decided there, as is what the avatar gate does and does not protect against.',
  },
  {
    test: /(^|\/)mtg-client\/src\/app\/(community\/user-profile|profile)\//,
    docs: ['USER_PROFILE_FEATURE.md'],
    why: 'These screens bind to the profile DTOs, whose public/private split is defined there.',
  },
];

/**
 * Normalise for comparison: forward slashes, no repeats, lower case. Windows paths reach
 * here spelled several ways — `C:\a\b`, `c:/a/b`, and escaped forms with doubled
 * separators — and a trailing-path match only works if they all collapse to one spelling.
 */
const norm = (p) =>
  String(p || '')
    .replace(/\\/g, '/')
    .replace(/\/+/g, '/')
    .toLowerCase();

/** Every file path this session has actually Read, per the transcript. */
function readsFrom(transcriptPath) {
  const seen = new Set();
  if (!transcriptPath || !fs.existsSync(transcriptPath)) return seen;

  for (const line of fs.readFileSync(transcriptPath, 'utf8').split('\n')) {
    if (!line.trim()) continue;
    let entry;
    try {
      entry = JSON.parse(line);
    } catch {
      continue; // A partially written final line is normal while the session is live.
    }
    const content = entry?.message?.content;
    if (!Array.isArray(content)) continue;
    for (const block of content) {
      if (block?.type === 'tool_use' && block?.name === 'Read' && block?.input?.file_path) {
        seen.add(norm(block.input.file_path));
      }
    }
  }
  return seen;
}

function main() {
  let payload;
  try {
    payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  } catch {
    process.exit(0); // No readable payload: not our place to block.
  }

  if (!WRITE_TOOLS.has(payload.tool_name)) process.exit(0);

  const target = norm(payload.tool_input?.file_path);
  if (!target) process.exit(0);

  const rules = RULES.filter((r) => r.test.test(target));
  if (rules.length === 0) process.exit(0);

  const alreadyRead = readsFrom(payload.transcript_path);
  const missing = [];

  for (const rule of rules) {
    for (const doc of rule.docs) {
      // Editing the doc itself never requires having read it first.
      if (target.endsWith(norm(doc))) continue;
      const wasRead = [...alreadyRead].some((r) => r.endsWith(norm(doc)));
      if (!wasRead) missing.push({ doc, why: rule.why });
    }
  }

  if (missing.length === 0) process.exit(0);

  const projectDir = payload.cwd || process.cwd();
  const lines = [
    `Blocked: ${payload.tool_name} on ${payload.tool_input.file_path}`,
    '',
    'This file is governed by a doc you have not read in this session:',
    ...missing.map((m) => `  - ${path.join(projectDir, m.doc)}\n      ${m.why}`),
    '',
    'Read it, then retry. Do not route around this by editing a different file.',
    'If the doc no longer governs this path, update RULES in .claude/hooks/require-docs.js.',
  ];
  process.stderr.write(lines.join('\n') + '\n');
  process.exit(2);
}

main();
