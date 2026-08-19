//
// What a built deck is measured on.
//
// Every check here is computable from the recorded plan and the card facts frozen beside
// it. Nothing asks a model whether a deck is good — that would be circular, expensive, and
// unstable between runs. The point of the harness is to make a prompt change measurable,
// which needs the measurement to be the one thing that does not move.
//
// Two kinds:
//
//   hard  — a failure is a defect. Legality, completeness, the bracket gate, the price
//           ceiling. These should be green on every run, and a red one is a bug report.
//   band  — a doctrine range from Knowledge/commander-doctrine.md. Out of band is a
//           signal, not a failure: §2 says the roles overlap and to count a card in the
//           role it is played for, which a classifier cannot do. Read these as a trend
//           across runs, not as a verdict on one deck.

const BASIC_LANDS = new Set([
  'Plains', 'Island', 'Swamp', 'Mountain', 'Forest', 'Wastes',
  'Snow-Covered Plains', 'Snow-Covered Island', 'Snow-Covered Swamp',
  'Snow-Covered Mountain', 'Snow-Covered Forest',
]);

/** Ceilings must match AiBuildService.PriceCeiling. */
const PRICE_CEILING = { budget: 3, mid: 30 };

/** Symmetrical sweepers — the effects §6.4 says a creature-dense deck should not want. */
const SYMMETRICAL_WIPE =
  /(destroy all creatures|exile all creatures|destroy each creature|all creatures get -\d+\/-\d+|destroy all nonland permanents)/i;

const has = (facts, type) => (facts.cardTypes || []).includes(type);
const isLand = (facts) => has(facts, 'Land');
const isCreature = (facts) => has(facts, 'Creature');

/**
 * The land band, adjusted for curve per doctrine §2.2.
 *
 * §3.1 starts at 37 and moves with the curve, so a flat 36-38 would mark a correctly-built
 * low-curve deck as wrong.
 */
function landBand(averageManaValue) {
  if (averageManaValue <= 2.5) return [33, 35];
  if (averageManaValue >= 3.8) return [38, 40];
  return [36, 38];
}

function band(name, value, [lo, hi], rule) {
  return { name, kind: 'band', value, lo, hi, ok: value >= lo && value <= hi, rule };
}

function hard(name, ok, detail, rule) {
  return { name, kind: 'hard', ok, detail, rule };
}

/**
 * Scores one recorded result.
 *
 * @param {object} record  what run.js wrote: { case, plan, facts }
 * @returns {{ id: string, checks: object[], error?: string }}
 */
function score(record) {
  const { case: kase, plan, facts } = record;
  if (record.error) {
    return { id: kase.id, error: `build failed (${record.error.status})`, checks: [] };
  }

  const cards = plan.cards.map((c) => facts[c.oracleId]).filter(Boolean);
  const commander = facts[kase.oracleId];
  const checks = [];

  // ---- hard ------------------------------------------------------------------
  checks.push(hard(
    'complete',
    plan.cards.length === 99 && plan.mainShortfall === 0,
    `${plan.cards.length} cards, shortfall ${plan.mainShortfall}, ${plan.cardsSkipped} rejected`,
    '§1.1',
  ));

  const cmdColors = new Set(commander ? commander.colorIdentity : []);
  const offColor = cards.filter((c) => c.colorIdentity.some((x) => !cmdColors.has(x)));
  checks.push(hard(
    'colour-identity',
    offColor.length === 0,
    offColor.length ? offColor.slice(0, 4).map((c) => c.name).join(', ') : 'all inside the commander',
    '§1.2',
  ));

  const counts = new Map();
  for (const c of cards) {
    if (BASIC_LANDS.has(c.name)) continue;
    counts.set(c.name, (counts.get(c.name) || 0) + 1);
  }
  const dupes = [...counts.entries()].filter(([, n]) => n > 1);
  checks.push(hard(
    'singleton',
    dupes.length === 0,
    dupes.length ? dupes.slice(0, 4).map(([n, c]) => `${n} x${c}`).join(', ') : 'no repeats outside basics',
    '§1.1',
  ));

  const gcs = cards.filter((c) => c.gameChanger).map((c) => c.name);
  checks.push(hard(
    'bracket-gate',
    kase.bracket >= 4 || gcs.length === 0,
    kase.bracket >= 4
      ? `${gcs.length} permitted and taken${gcs.length ? `: ${gcs.slice(0, 3).join(', ')}` : ''}`
      : gcs.length ? `must be none below bracket 4: ${gcs.join(', ')}` : 'none, as required',
    '§1.4',
  ));

  const ceiling = PRICE_CEILING[kase.priceRange];
  const over = ceiling
    ? cards.filter((c) => c.cheapestUsd !== null && c.cheapestUsd > ceiling)
    : [];
  checks.push(hard(
    'price-ceiling',
    over.length === 0,
    ceiling
      ? over.length
        ? over.slice(0, 4).map((c) => `${c.name} $${c.cheapestUsd.toFixed(2)}`).join(', ')
        : `all inside $${ceiling}`
      : 'uncapped',
    'PriceCeiling',
  ));

  // ---- bands -----------------------------------------------------------------
  const f = (plan.assessment && plan.assessment.facts) || {};
  const avgMv = f.averageManaValue ?? 0;

  checks.push(band('lands', f.lands ?? 0, landBand(avgMv), '§3.1/§2.2'));
  checks.push(band('ramp', f.ramp ?? 0, [8, 12], '§4'));
  checks.push(band('card-advantage', f.draw ?? 0, [8, 12], '§5'));
  checks.push(band('interaction', f.interaction ?? 0, [8, 12], '§6.1'));
  checks.push(band('mana-sources', f.manaSources ?? 0, [45, 48], '§2.1'));

  if (kase.tribe) {
    const density = cards.filter(
      (c) => c.subtypes.includes(kase.tribe) || new RegExp(`\\b${kase.tribe}s?\\b`, 'i').test(c.oracleText),
    ).length;
    checks.push(band('tribal-density', density, [12, 99], '§7'));
  }

  // §6.4 inverts with archetype: a creature-dense board loses more than the table does.
  const creaturePct = f.creaturePercentOfNonland ?? 0;
  if (creaturePct >= 55) {
    const wipes = cards.filter((c) => SYMMETRICAL_WIPE.test(c.oracleText)).map((c) => c.name);
    checks.push(hard(
      'mass-removal-fit',
      wipes.length === 0,
      wipes.length
        ? `${creaturePct}% creatures, yet: ${wipes.join(', ')}`
        : `${creaturePct}% creatures, no symmetrical sweeper`,
      '§6.4',
    ));
  }

  return {
    id: kase.id,
    seconds: record.seconds,
    interactionSplit: `${f.interaction ?? 0} (${f.interactionOnCreatures ?? 0} on creatures)`,
    checks,
  };
}

module.exports = { score, landBand, PRICE_CEILING };
