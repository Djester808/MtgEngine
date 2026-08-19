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

/**
 * Modal wording. A sweeper you can decline is not a liability.
 *
 * §6.4 is about a card whose effect *is* symmetrical removal — "the deck loses more than
 * the table does". A card offering a small sweep as one of three modes never has to sweep,
 * and counting it made the only card this check ever flagged a false positive: Golgari
 * Charm, whose other modes are destroy-an-enchantment and regenerate-your-team.
 */
const MODAL = /choose (one|two|up to)/i;

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
    const wipes = cards
      .filter((c) => SYMMETRICAL_WIPE.test(c.oracleText) && !MODAL.test(c.oracleText))
      .map((c) => c.name);
    checks.push(hard(
      'mass-removal-fit',
      wipes.length === 0,
      wipes.length
        ? `${creaturePct}% creatures, yet: ${wipes.join(', ')}`
        : `${creaturePct}% creatures, no symmetrical sweeper`,
      '§6.4',
    ));
  }

  // ---- the assessment, measured on its own terms --------------------------
  //
  // Deck checks cannot judge a change to the assessment. It runs after the deck is built
  // and cannot influence which cards were chosen, so any movement in lands or ramp between
  // two runs is the build call's own variance — a separate model call that was not the
  // thing under test. Measured: dropping assessment effort moved five deck bands it cannot
  // reach. These numbers are the ones that respond.
  const findings = (plan.assessment && plan.assessment.findings) || [];
  const prose = findings.map((x) => `${x.finding || ''} ${x.fix || ''}`).join(' ');
  const assessment = {
    findings: findings.length,
    // The actionable half. A finding without a fix names a problem and stops there.
    withFix: findings.filter((x) => (x.fix || '').length > 5).length,
    doctrineCitations: (prose.match(/§\s?\d+(\.\d+)?/g) || []).length,
    verdictChars: ((plan.assessment && plan.assessment.verdict) || '').length,
  };

  return {
    id: kase.id,
    seconds: record.seconds,
    assessment,
    interactionSplit: `${f.interaction ?? 0} (${f.interactionOnCreatures ?? 0} on creatures)`,
    checks,
  };
}

/**
 * Coupling language that says nothing checkable.
 *
 * Doctrine §9.11: a reason must name the concrete interaction — what triggers what, what
 * one card produces that another consumes. "Synergizes with the commander" reads as a hook
 * while asserting nothing, and is exactly how a card with no real link keeps a slot.
 *
 * The prompt already forbids it. This measures whether that instruction is obeyed, because
 * an instruction is not a control.
 */
const FILLER = /(synergiz\w*|works? well with|pairs? with|fuels?|supports?|contributes? to the plan|alongside)/i;

/** A mechanism is named when the reason points at a rules step, not just an association. */
const MECHANISM = /(trigger|whenever|enters|dies|sacrific|counter|token|draw|mana|attack|cast|exile|destroy|graveyard|\+1\/\+1|tap|untap)/i;

/**
 * Scores one recorded commander-shortlist result.
 *
 * Suggestions fail differently from builds — the wrong colours, a padded list, a card that
 * cannot legally head a deck, a reason that gestures instead of explaining — so they get
 * their own checks rather than being forced through the deck ones.
 */
function scoreSuggestions(record) {
  const { case: kase, result, facts } = record;
  if (record.error) {
    return { id: kase.id, kind: 'suggestions', error: `request failed (${record.error.status})`, checks: [] };
  }

  const list = result.commanders || [];
  const checks = [];

  // Fewer than asked for is correct when the pool cannot honestly fill the list; more is
  // never correct, and padding is what the prompt is told not to do.
  checks.push(hard(
    'count-honest',
    list.length <= kase.count,
    `${list.length} returned of ${kase.count} asked for, ${result.discarded ?? 0} discarded`,
    '§9.6',
  ));

  const wanted = [...(kase.colors || [])].sort().join('');
  const wrongColour = list.filter(
    (c) => wanted && [...(c.colorIdentity || [])].sort().join('') !== wanted,
  );
  checks.push(hard(
    'colour-match',
    wrongColour.length === 0,
    wanted
      ? wrongColour.length
        ? wrongColour.slice(0, 4).map((c) => `${c.name} (${c.colorIdentity.join('')})`).join(', ')
        : `all exactly ${wanted}`
      : 'no colour constraint',
    '§1.2',
  ));

  // Anything returned has to be able to head a deck at all.
  const notCommander = list.filter((c) => {
    const f = facts[c.oracleId];
    if (!f) return true;
    const legendaryCreature =
      (f.supertypes || []).includes('Legendary') && (f.cardTypes || []).includes('Creature');
    return !legendaryCreature && !/can be your commander/i.test(f.oracleText || '');
  });
  checks.push(hard(
    'is-a-commander',
    notCommander.length === 0,
    notCommander.length ? notCommander.map((c) => c.name).join(', ') : 'all can head a deck',
    '§1.1',
  ));

  const gcs = list.filter((c) => (facts[c.oracleId] || {}).gameChanger).map((c) => c.name);
  checks.push(hard(
    'bracket-gate',
    kase.bracket >= 4 || gcs.length === 0,
    gcs.length ? gcs.join(', ') : 'none',
    '§1.4',
  ));

  // §9.11, measured: filler is only a defect when nothing checkable sits beside it.
  const hollow = list.filter((c) => FILLER.test(c.reason || '') && !MECHANISM.test(c.reason || ''));
  checks.push(hard(
    'reasons-name-a-mechanism',
    hollow.length === 0,
    hollow.length ? hollow.slice(0, 3).map((c) => `${c.name}: "${c.reason}"`).join(' | ') : 'every reason names one',
    '§9.11',
  ));

  // The prompt asks for variety, so a shortlist of ten decks that build the same way is a
  // worse answer than a shorter one.
  const archetypes = new Set(list.map((c) => (c.archetype || '').toLowerCase().trim()));
  checks.push(band('archetype-variety', archetypes.size, [Math.min(3, list.length), 99], 'prompt'));

  return {
    id: kase.id,
    kind: 'suggestions',
    seconds: record.seconds,
    checks,
  };
}

module.exports = { score, scoreSuggestions, landBand, PRICE_CEILING };

