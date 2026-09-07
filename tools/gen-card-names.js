#!/usr/bin/env node
/**
 * Generate tools/card-names.txt: every card name and every card-face name in the Scryfall
 * oracle corpus, one per line, sorted and unique, LF endings.
 *
 * CARD_ENGINE_PLAN.md rule 1 — a card is data or it does not exist. The architecture test
 * reads this file and fails if a string literal in MtgEngine.Rules matches one of these
 * names. The corpus itself is not in git (200 MB, downloaded on startup); the name list is,
 * so the test works in a fresh clone and in CI.
 *
 *   node tools/gen-card-names.js [<oracle_cards.json>] [<out.txt>]
 *
 * The corpus is JSON Lines — one card object per line — but it has been seen wrapped as a
 * JSON array, so every line is parsed defensively: bare "[" / "]" lines and trailing commas
 * are stripped, and a line that will not parse is counted and skipped rather than fatal.
 */

const fs = require('fs');
const path = require('path');
const readline = require('readline');

const repoRoot = path.resolve(__dirname, '..');
const input =
  process.argv[2] ||
  path.join(repoRoot, 'MtgEngine.Api', 'bin', 'Debug', 'net10.0', 'bulk-data', 'oracle_cards.json');
const output = process.argv[3] || path.join(repoRoot, 'tools', 'card-names.txt');

if (!fs.existsSync(input)) {
  console.error(`gen-card-names: corpus not found at ${input}`);
  console.error('Start the API once to download the Scryfall bulk data, then re-run.');
  process.exit(1);
}

const names = new Set();
let cards = 0;
let skipped = 0;

const rl = readline.createInterface({
  input: fs.createReadStream(input, { encoding: 'utf8' }),
  crlfDelay: Infinity,
});

rl.on('line', (raw) => {
  let line = raw.trim();
  if (!line || line === '[' || line === ']') return;
  if (line.endsWith(',')) line = line.slice(0, -1);
  let card;
  try {
    card = JSON.parse(line);
  } catch {
    skipped += 1;
    return;
  }
  if (!card || typeof card !== 'object') return;
  cards += 1;
  if (typeof card.name === 'string' && card.name.trim()) names.add(card.name.trim());
  if (Array.isArray(card.card_faces)) {
    for (const face of card.card_faces) {
      if (face && typeof face.name === 'string' && face.name.trim()) names.add(face.name.trim());
    }
  }
});

rl.on('close', () => {
  const sorted = [...names].sort();
  fs.writeFileSync(output, sorted.join('\n') + '\n', 'utf8');
  console.log(`gen-card-names: ${cards} cards read, ${skipped} unparsable line(s) skipped`);
  console.log(`gen-card-names: ${sorted.length} names -> ${path.relative(repoRoot, output).replace(/\\/g, '/')}`);
});
