// Pure table model. No DOM, transport, storage, or rules-engine dependencies.
export const ZONES = [
  "library",
  "hand",
  "graveyard",
  "battlefield",
  "stack",
  "exile",
  "command",
];
const PERSONAL = new Set(["library", "hand", "graveyard"]);
export const zoneKey = (kind, player = null) => {
  if (!ZONES.includes(kind)) throw new Error("Unknown zone.");
  if (PERSONAL.has(kind) !== (player !== null))
    throw new Error("Invalid zone scope.");
  return player === null ? kind : `${kind}:${player}`;
};
const CATALOG = {
  forest: {
    name: "Forest",
    type: "Basic land",
    theme: "forest",
    rules: "A green mana source.",
    mana: "G",
  },
  island: {
    name: "Island",
    type: "Basic land",
    theme: "island",
    rules: "A blue mana source.",
    mana: "U",
  },
  plains: {
    name: "Plains",
    type: "Basic land",
    theme: "plains",
    rules: "A white mana source.",
    mana: "W",
  },
  mountain: {
    name: "Mountain",
    type: "Basic land",
    theme: "mountain",
    rules: "A red mana source.",
    mana: "R",
  },
  scout: {
    name: "Canopy Scout",
    type: "Creature · sample",
    theme: "forest",
    stats: "2 / 2",
    rules: "Sample creature for exploring the board.",
  },
  drake: {
    name: "Cloudwing Drake",
    type: "Creature · sample",
    theme: "island",
    stats: "3 / 3",
    rules: "Flying. Sample creature for exploring combat.",
  },
  knight: {
    name: "Dawnwatch Knight",
    type: "Creature · sample",
    theme: "plains",
    stats: "2 / 3",
    rules: "Vigilance. Sample creature for exploring the board.",
  },
  dragon: {
    name: "Emberpeak Dragon",
    type: "Creature · sample",
    theme: "mountain",
    stats: "4 / 4",
    rules: "Flying. Sample creature for exploring combat.",
  },
  relic: {
    name: "Wayfarer’s Relic",
    type: "Artifact · sample",
    theme: "relic",
    rules: "Sample artifact. Its abilities are not simulated.",
  },
  spell: {
    name: "Sudden Insight",
    type: "Instant · sample",
    theme: "island",
    rules: "Draw two cards. Sample cost: one generic mana.",
    effects: [{ kind: "draw", count: 2 }],
  },
  spark: {
    name: "Cinder Spark",
    type: "Instant · sample",
    theme: "mountain",
    rules:
      "Deal 3 damage to target creature or player. Sample cost: one generic mana.",
    targetKind: "creature-or-player",
    effects: [{ kind: "damage", amount: 3 }],
  },
  demise: {
    name: "Quiet Demise",
    type: "Instant · sample",
    theme: "relic",
    rules: "Destroy target creature. Sample cost: one generic mana.",
    targetKind: "creature",
    effects: [{ kind: "destroy" }],
  },
  leader: {
    name: "Keeper of the Horizon",
    type: "Legendary creature · sample commander",
    theme: "forest",
    stats: "4 / 5",
    rules: "Sample commander. Costs and cast restrictions are not enforced.",
  },
  token: {
    name: "Saproling",
    type: "Token creature · sample",
    theme: "forest",
    stats: "1 / 1",
    rules: "A separate token object.",
  },
};
export function createTable(count = 4, dense = false) {
  if (![2, 3, 4].includes(count))
    throw new Error("Choose two to four players.");
  const names = ["Alex", "Mira", "Theo", "Jules"];
  const accents = ["jade", "blue", "amber", "rose"];
  const state = {
    players: Array.from({ length: count }, (_, i) => ({
      id: `p${i}`,
      seat: i,
      name: names[i],
      accent: accents[i],
      life: 40,
      mana: 0,
      manaByColor: {},
      left: false,
    })),
    zones: {},
    objects: {},
    nextId: 1,
    revision: 0,
    viewer: "p0",
    active: "p0",
    priority: null,
    step: "untap",
    declaration: null,
    landsPlayed: {},
    passes: [],
    turn: 1,
    attacks: {},
    blocks: {},
    blockedAttackers: {},
    combatBlockers: {},
    firstStrikeParticipants: [],
    damageAssignments: [],
    combatHistory: [],
    log: [],
  };
  for (const kind of ZONES) {
    if (PERSONAL.has(kind))
      for (const p of state.players) state.zones[zoneKey(kind, p.id)] = [];
    else state.zones[zoneKey(kind)] = [];
  }
  const creatures = ["scout", "drake", "knight", "dragon"];
  const lands = ["forest", "island", "plains", "mountain"];
  for (const p of state.players) {
    addObject(state, creatures[p.seat], p.id, "battlefield");
    addObject(state, "relic", p.id, "battlefield");
    for (let i = 0; i < 3; i++)
      addObject(state, lands[p.seat], p.id, "battlefield");
    for (const card of ["spell", creatures[p.seat], lands[p.seat], "relic"])
      addObject(state, card, p.id, "hand");
    for (let i = 0; i < 12; i++)
      addObject(
        state,
        i % 2 ? lands[p.seat] : creatures[p.seat],
        p.id,
        "library",
      );
    addObject(state, "spell", p.id, "graveyard");
    addObject(state, "leader", p.id, "command");
    addObject(state, "relic", p.id, "exile");
    if (dense) {
      for (let i = 0; i < 8; i++)
        addObject(state, creatures[p.seat], p.id, "battlefield");
      for (let i = 0; i < 11; i++)
        addObject(state, lands[p.seat], p.id, "battlefield");
    }
  }

  state.log.push(
    "Sample Commander-style table loaded. Card names and abilities are illustrative.",
  );
  return assertState(state);
}
function addObject(s, definition, owner, kind) {
  const id = `o${s.nextId++}`;
  const card = `card-${id}`;
  const zone = PERSONAL.has(kind) ? zoneKey(kind, owner) : zoneKey(kind);
  s.objects[id] = {
    ...CATALOG[definition],
    id,
    card,
    owner,
    controller: owner,
    zone,
    tapped: false,
    manaContribution: null,
    counters: 0,
    damage: 0,
    controlledSinceTurn: 0,
    token: definition === "token",
    commander: definition === "leader",
  };
  s.zones[zone].push(id);
  return id;
}
export function assertState(s) {
  if (!TURN_STEPS.some((step) => step.id === s.step))
    throw new Error("Unknown turn step.");
  if (
    s.priority !== null &&
    !s.players.some((p) => p.id === s.priority && !p.left)
  )
    throw new Error("Priority belongs to an unavailable player.");
  if (
    s.priority !== null &&
    (s.declaration || ["untap", "cleanup"].includes(s.step))
  )
    throw new Error("This step action does not grant priority.");
  if (new Set(s.passes).size !== s.passes.length)
    throw new Error("A player passed twice in the same succession.");
  const seen = new Set();
  for (const [zone, ids] of Object.entries(s.zones))
    for (const id of ids) {
      if (seen.has(id) || !s.objects[id] || s.objects[id].zone !== zone)
        throw new Error("Zone membership is inconsistent.");
      seen.add(id);
    }
  if (seen.size !== Object.keys(s.objects).length)
    throw new Error("An object has no zone.");
  return s;
}
export const player = (s, id) => s.players.find((p) => p.id === id);
export const living = (s) => s.players.filter((p) => !p.left);
export const contents = (s, kind, who = null) =>
  s.zones[zoneKey(kind, who)].map((id) => s.objects[id]);
export function seats(s) {
  const index = s.players.findIndex((p) => p.id === s.viewer);
  return [...s.players.slice(index), ...s.players.slice(0, index)];
}
// This browser-local projection demonstrates access rules. Real hidden state must stay on the server.
export function visibleContents(s, kind, who = null) {
  const cards = contents(s, kind, who);
  if (kind === "library" || (kind === "hand" && who !== s.viewer))
    return { count: cards.length, cards: [], hidden: true };
  return { count: cards.length, cards, hidden: false };
}
function move(s, id, kind, controller = s.viewer) {
  const o = s.objects[id];
  if (!o) throw new Error("That object is no longer available.");
  const next = PERSONAL.has(kind) ? zoneKey(kind, o.owner) : zoneKey(kind);
  if (next === o.zone) throw new Error("The card is already in that zone.");
  s.zones[o.zone] = s.zones[o.zone].filter((x) => x !== id);
  delete s.objects[id];
  removeFromCombat(s, id);
  if (o.token && kind !== "battlefield") return null;
  const newId = `o${s.nextId++}`;
  s.objects[newId] = {
    ...o,
    id: newId,
    zone: next,
    tapped: false,
    manaContribution: null,
    counters: 0,
    damage: 0,
    controlledSinceTurn:
      kind === "battlefield" ? s.turn : o.controlledSinceTurn,
    controller:
      kind === "battlefield" || kind === "stack" ? controller : o.owner,
  };
  if (kind === "library") s.zones[next].unshift(newId);
  else s.zones[next].push(newId);
  return newId;
}
function nextLiving(s, id) {
  const index = s.players.findIndex((p) => p.id === id);
  for (let n = 1; n <= s.players.length; n++) {
    const candidate = s.players[(index + n) % s.players.length];
    if (!candidate.left) return candidate.id;
  }
  return null;
}

const keyword = (o, text) =>
  !!o && new RegExp("\\b" + text + "\\b", "i").test(o.rules ?? "");
const isCreature = (o) =>
  !!o && o.zone === "battlefield" && o.type.toLowerCase().includes("creature");
export function combatStats(o) {
  const stats = /(-?\d+)\s*\/\s*(-?\d+)/.exec(o.stats ?? "0 / 0");
  return {
    power: (Number(stats?.[1]) || 0) + (o.counters ?? 0),
    toughness: (Number(stats?.[2]) || 0) + (o.counters ?? 0),
  };
}
function removeFromCombat(s, id) {
  delete s.attacks[id];
  delete s.blocks[id];
  delete s.combatBlockers[id];
  delete s.blockedAttackers[id];
  for (const [blocker, attacker] of Object.entries(s.blocks))
    if (attacker === id) delete s.blocks[blocker];
}
function resetCombat(s) {
  s.attacks = {};
  s.blocks = {};
  s.blockedAttackers = {};
  s.combatBlockers = {};
  s.firstStrikeParticipants = [];
  s.damageAssignments = [];
}
export function blockPermission(s, blockerId, attackerId, actor = s.viewer) {
  if (
    s.step !== "blockers" ||
    s.declaration !== "blockers" ||
    actor !== s.blockingPlayers[0]
  )
    return "Only the indicated defending player can choose blockers now.";
  const blocker = s.objects[blockerId],
    attacker = s.objects[attackerId];
  if (!isCreature(blocker) || blocker.controller !== actor)
    return "Choose a creature you control.";
  if (blocker.tapped) return "A tapped creature cannot block.";
  if (attackerId === null) return null;
  if (!isCreature(attacker) || s.attacks[attackerId] !== actor)
    return "Choose a creature attacking you.";
  if (/can.t block|cannot block/i.test(blocker.rules))
    return "This creature cannot block.";
  if (/can.t be blocked|cannot be blocked/i.test(attacker.rules))
    return "That attacker cannot be blocked.";
  if (
    keyword(attacker, "flying") &&
    !keyword(blocker, "flying") &&
    !keyword(blocker, "reach")
  )
    return "Flying attackers need a blocker with flying or reach.";
  return null;
}
function combatantIds(s) {
  return [
    ...new Set([...Object.keys(s.attacks), ...Object.keys(s.combatBlockers)]),
  ].filter((id) => isCreature(s.objects[id]));
}
function damageEligible(s, id) {
  const o = s.objects[id];
  return s.step === "first-damage"
    ? keyword(o, "first strike") || keyword(o, "double strike")
    : !s.firstStrikeParticipants.includes(id) || keyword(o, "double strike");
}
function lethalAssignment(source, target) {
  const remaining = Math.max(
    0,
    combatStats(target).toughness - (target.damage ?? 0),
  );
  return keyword(source, "deathtouch") && remaining > 0 ? 1 : remaining;
}
export function buildCombatDamage(s) {
  return combatantIds(s)
    .filter((id) => damageEligible(s, id))
    .map((id) => {
      const o = s.objects[id],
        power = Math.max(0, combatStats(o).power);
      const attacking = !!s.attacks[id];
      let targets = [];
      if (attacking) {
        const blockers = Object.entries(s.blocks)
          .filter(
            ([blocker, attacker]) =>
              attacker === id && isCreature(s.objects[blocker]),
          )
          .map(([blocker]) => blocker);
        targets = blockers.map((target) => ({
          target,
          kind: "creature",
          amount: 0,
        }));
        if (!s.blockedAttackers[id] || keyword(o, "trample"))
          targets.push({ target: s.attacks[id], kind: "player", amount: 0 });
      } else if (s.blocks[id] && isCreature(s.objects[s.blocks[id]]))
        targets = [{ target: s.blocks[id], kind: "creature", amount: 0 }];
      let remaining = power;
      targets.forEach((target, i) => {
        const amount =
          i === targets.length - 1 || target.kind === "player"
            ? remaining
            : Math.min(
                remaining,
                lethalAssignment(o, s.objects[target.target]),
              );
        target.amount = amount;
        remaining -= amount;
      });
      return { source: id, power, targets };
    });
}
export function validateDamage(s) {
  const canonical = buildCombatDamage(s);
  for (const plan of canonical) {
    const chosen = s.damageAssignments.find((p) => p.source === plan.source);
    if (!chosen || chosen.targets.length !== plan.targets.length)
      throw new Error("Combat changed. Review damage assignments again.");
    for (const target of chosen.targets)
      if (
        !plan.targets.some(
          (t) => t.target === target.target && t.kind === target.kind,
        ) ||
        !Number.isInteger(target.amount) ||
        target.amount < 0
      )
        throw new Error(
          "Use nonnegative whole damage amounts on the listed targets.",
        );
    if (
      chosen.targets.length &&
      chosen.targets.reduce((sum, t) => sum + t.amount, 0) !== plan.power
    )
      throw new Error(
        s.objects[plan.source].name +
          " must assign exactly " +
          plan.power +
          " damage.",
      );
    const playerDamage = chosen.targets.find((t) => t.kind === "player");
    if (s.blockedAttackers[plan.source] && playerDamage?.amount > 0)
      for (const target of chosen.targets.filter((t) => t.kind === "creature"))
        if (
          target.amount <
          lethalAssignment(s.objects[plan.source], s.objects[target.target])
        )
          throw new Error(
            "Assign lethal damage to every blocker before trample damage to the player.",
          );
  }
}
function dealCombatDamage(s) {
  validateDamage(s);
  const events = [];
  const eligibleSources = new Set(
    buildCombatDamage(s).map((plan) => plan.source),
  );
  for (const plan of s.damageAssignments) {
    if (!eligibleSources.has(plan.source)) continue;
    const source = s.objects[plan.source];
    if (!isCreature(source) || !damageEligible(s, source.id)) continue;
    for (const target of plan.targets)
      if (target.amount > 0)
        events.push({
          source: source.id,
          sourceName: source.name,
          controller: source.controller,
          target: target.target,
          targetName:
            target.kind === "player"
              ? player(s, target.target).name
              : s.objects[target.target].name,
          kind: target.kind,
          amount: target.amount,
          deathtouch: keyword(source, "deathtouch"),
          lifelink: keyword(source, "lifelink"),
        });
  }
  for (const event of events) {
    if (event.kind === "player") player(s, event.target).life -= event.amount;
    else {
      const target = s.objects[event.target];
      target.damage = (target.damage ?? 0) + event.amount;
      if (event.deathtouch) target.deathtouchDamaged = true;
    }
    if (event.lifelink) player(s, event.controller).life += event.amount;
  }
  const deaths = checkCreatureDeaths(s);
  const report = { turn: s.turn, step: s.step, events, deaths };
  s.combatHistory.push(report);
  s.declaration = null;
  s.priority = s.active;
  s.passes = [];
  return (
    (s.step === "first-damage" ? "First-strike" : "Combat") +
    " damage dealt simultaneously: " +
    (events
      .map((e) => e.sourceName + " → " + e.targetName + ": " + e.amount)
      .join("; ") || "no damage") +
    (deaths.length ? ". Died: " + deaths.map((d) => d.name).join(", ") : "") +
    "."
  );
}
function checkCreatureDeaths(s) {
  const dead = Object.values(s.objects).filter(
    (o) =>
      isCreature(o) &&
      (combatStats(o).toughness <= 0 ||
        (!keyword(o, "indestructible") &&
          ((o.damage ?? 0) >= combatStats(o).toughness ||
            o.deathtouchDamaged))),
  );
  const deaths = dead.map((o) => ({
    id: o.id,
    card: o.card,
    name: o.name,
    owner: o.owner,
    token: o.token,
    damage: o.damage ?? 0,
  }));
  for (const o of dead) move(s, o.id, "graveyard");
  for (const o of Object.values(s.objects)) o.deathtouchDamaged = false;
  return deaths;
}

// Prototype timing controller. It executes sample combat but not arbitrary card effects.
export const TURN_STEPS = [
  { id: "untap", phase: "Beginning", label: "Untap" },
  { id: "upkeep", phase: "Beginning", label: "Upkeep" },
  { id: "draw", phase: "Beginning", label: "Draw" },
  { id: "main1", phase: "Precombat main", label: "Main phase" },
  { id: "combat-begin", phase: "Combat", label: "Beginning of combat" },
  { id: "attackers", phase: "Combat", label: "Declare attackers" },
  { id: "blockers", phase: "Combat", label: "Declare blockers" },
  {
    id: "first-damage",
    phase: "Combat",
    label: "First-strike damage (if needed)",
  },
  { id: "damage", phase: "Combat", label: "Combat damage" },
  { id: "combat-end", phase: "Combat", label: "End of combat" },
  { id: "main2", phase: "Postcombat main", label: "Main phase" },
  { id: "end", phase: "Ending", label: "End step" },
  { id: "cleanup", phase: "Ending", label: "Cleanup" },
];
export const currentStep = (s) => TURN_STEPS.find((step) => step.id === s.step);
export function hasPriority(s, actor = s.viewer) {
  return (
    s.priority === actor &&
    !s.declaration &&
    !["untap", "cleanup"].includes(s.step)
  );
}
function sorceryWindow(s, actor) {
  return (
    hasPriority(s, actor) &&
    s.active === actor &&
    ["main1", "main2"].includes(s.step) &&
    s.zones.stack.length === 0
  );
}
export function actionPermission(s, type, id, actor = s.viewer) {
  const o = s.objects[id];
  if (type === "pass")
    return hasPriority(s, actor) ? null : "That player does not have priority.";
  if (!o) return "Choose an available card.";
  if (type === "tap") {
    if (o.zone !== "battlefield" || o.controller !== actor || !o.mana)
      return "Choose a mana source you control.";
    if (!hasPriority(s, actor)) return "You need priority to tap a land here.";
    if (o.tapped)
      return "This land is tapped. It untaps during your untap step.";
    if (
      o.type.toLowerCase().includes("creature") &&
      o.controlledSinceTurn >= s.turn &&
      !/haste/i.test(o.rules)
    )
      return "This creature cannot use a tap ability yet.";
    return null;
  }
  if (type === "block") return blockPermission(s, id, null, actor);
  if (type === "attack") {
    if (
      s.step !== "attackers" ||
      s.declaration !== "attackers" ||
      actor !== s.active
    )
      return "Choose attackers during your declare attackers action.";
    if (
      o.zone !== "battlefield" ||
      o.controller !== actor ||
      !o.type.toLowerCase().includes("creature")
    )
      return "Choose a creature you control.";
    if (o.tapped) return "Tapped creatures cannot attack.";
    if (keyword(o, "defender"))
      return "A creature with defender cannot attack.";
    if (o.controlledSinceTurn >= s.turn && !/haste/i.test(o.rules))
      return "This creature has not been under your control since the turn began.";
    return null;
  }
  if (o.zone !== zoneKey("hand", actor)) return "Choose a card in your hand.";
  if (type === "play-land") {
    if (!o.mana || !o.type.toLowerCase().includes("land"))
      return "Choose a land card.";
    if (!sorceryWindow(s, actor))
      return "Play lands in your main phase with priority and an empty stack.";
    if ((s.landsPlayed[actor] ?? 0) >= 1)
      return "You have already played a land this turn.";
    return null;
  }
  if (type === "cast") {
    if (o.type.toLowerCase().includes("land"))
      return "Lands are played, not cast.";
    if (!hasPriority(s, actor)) return "You need priority to cast a spell.";
    if (
      !/instant/i.test(o.type) &&
      !/flash/i.test(o.rules) &&
      !sorceryWindow(s, actor)
    )
      return "Cast this card in your main phase with priority and an empty stack.";
    return null;
  }
  return null;
}
function clearMana(s) {
  for (const p of s.players) {
    p.mana = 0;
    p.manaByColor = {};
  }
  for (const o of Object.values(s.objects)) o.manaContribution = null;
}
function enterStep(s, id) {
  clearMana(s);
  s.step = id;
  s.passes = [];
  s.declaration = null;
  s.priority = ["untap", "cleanup"].includes(id) ? null : s.active;
  if (id === "untap") {
    resetCombat(s);
    s.landsPlayed = {};
    for (const o of Object.values(s.objects))
      if (o.zone === "battlefield" && o.controller === s.active)
        o.tapped = false;
  }
  if (id === "draw" && !(s.players.length === 2 && s.turn === 1)) {
    const id = s.zones[zoneKey("library", s.active)][0];
    if (id) move(s, id, "hand");
    else
      s.log.push(
        "Empty library: loss handling is not implemented in this prototype.",
      );
  }
  if (id === "attackers") {
    resetCombat(s);
    s.declaration = "attackers";
    s.priority = null;
  }
  if (id === "blockers") {
    const activeIndex = s.players.findIndex((p) => p.id === s.active);
    s.blockingPlayers = Array.from(
      { length: s.players.length - 1 },
      (_, i) => s.players[(activeIndex + i + 1) % s.players.length],
    )
      .filter((p) => !p.left && Object.values(s.attacks).includes(p.id))
      .map((p) => p.id);
    s.declaration = "blockers";
    s.priority = null;
  }
  if (id === "first-damage" || id === "damage") {
    if (id === "first-damage")
      s.firstStrikeParticipants = combatantIds(s).filter(
        (id) =>
          keyword(s.objects[id], "first strike") ||
          keyword(s.objects[id], "double strike"),
      );
    s.damageAssignments = buildCombatDamage(s);
    s.declaration = "damage";
    s.priority = null;
  }
  if (id === "cleanup")
    for (const o of Object.values(s.objects)) {
      o.damage = 0;
      o.deathtouchDamaged = false;
    }
}
function advanceStep(s) {
  if (s.step === "combat-end") resetCombat(s);
  let index = TURN_STEPS.findIndex((step) => step.id === s.step) + 1;
  if (
    TURN_STEPS[index]?.id === "first-damage" &&
    !combatantIds(s).some(
      (id) =>
        keyword(s.objects[id], "first strike") ||
        keyword(s.objects[id], "double strike"),
    )
  )
    index++;
  if (s.step === "attackers" && !Object.keys(s.attacks).length)
    index = TURN_STEPS.findIndex((step) => step.id === "combat-end");
  if (index === TURN_STEPS.length) {
    s.active = nextLiving(s, s.active);
    s.turn++;
    index = 0;
  }
  enterStep(s, TURN_STEPS[index].id);
  return currentStep(s).phase + " — " + currentStep(s).label + ".";
}
export function legalSpellTarget(s, spell, target) {
  if (!spell?.targetKind || !target) return false;
  if (target.kind === "player")
    return (
      spell.targetKind === "creature-or-player" &&
      living(s).some((p) => p.id === target.id)
    );
  const o = s.objects[target.id];
  return (
    target.kind === "object" &&
    !!o &&
    o.zone === "battlefield" &&
    /creature/i.test(o.type) &&
    !keyword(o, "shroud") &&
    !(keyword(o, "hexproof") && o.controller !== spell.controller)
  );
}
function resolveTop(s) {
  const o = s.objects[s.zones.stack.at(-1)];
  const valid = !o.targetKind || legalSpellTarget(s, o, o.spellTarget);
  const messages = [];
  if (valid)
    for (const effect of o.effects ?? []) {
      const target = o.spellTarget;
      if (effect.kind === "draw") {
        let drawn = 0;
        for (let i = 0; i < effect.count; i++) {
          const id = s.zones[zoneKey("library", o.controller)][0];
          if (!id) break;
          move(s, id, "hand", o.controller);
          drawn++;
        }
        messages.push(
          player(s, o.controller).name + " drew " + drawn + " cards",
        );
      } else if (effect.kind === "damage") {
        if (target.kind === "player")
          player(s, target.id).life -= effect.amount;
        else if (s.objects[target.id])
          s.objects[target.id].damage += effect.amount;
        messages.push(
          effect.amount +
            " damage to " +
            (target.kind === "player"
              ? player(s, target.id).name
              : s.objects[target.id]?.name),
        );
      } else if (effect.kind === "destroy") {
        const victim = s.objects[target.id];
        if (victim && !keyword(victim, "indestructible")) {
          move(s, victim.id, "graveyard", victim.owner);
          messages.push(victim.name + " destroyed");
        } else messages.push("Indestructible prevented destruction");
      }
    }
  move(
    s,
    o.id,
    /instant|sorcery/i.test(o.type) ? "graveyard" : "battlefield",
    o.controller,
  );
  s.passes = [];
  s.priority = s.active;
  return valid
    ? o.name + " resolved. " + messages.join("; ")
    : o.name + " did not resolve: its target is no longer legal.";
}
function paySampleCost(s, o, actor) {
  // Explicit sample costs, not an Oracle/card text parser.
  const cost = o.sampleCost ?? { generic: 1 };
  const p = player(s, actor),
    pool = { ...p.manaByColor, C: (p.manaByColor.C ?? 0) + p.mana };
  for (const [color, amount] of Object.entries(cost))
    if (color !== "generic") {
      if ((pool[color] ?? 0) < amount)
        throw new Error("Not enough " + color + " mana for this sample cost.");
      pool[color] -= amount;
    }
  let generic = cost.generic ?? 0;
  for (const color of ["C", "W", "U", "B", "R", "G"]) {
    const spend = Math.min(generic, pool[color] ?? 0);
    generic -= spend;
    pool[color] = (pool[color] ?? 0) - spend;
  }
  if (generic) throw new Error("Tap lands for the sample mana cost first.");
  p.mana = 0;
  p.manaByColor = pool;
}
function turnInteraction(s, action) {
  const actor = action.player ?? s.viewer,
    o = s.objects[action.id];
  if (
    ["cast", "play-land", "tap", "attack", "block", "pass"].includes(
      action.type,
    )
  ) {
    const reason = actionPermission(s, action.type, action.id, actor);
    if (reason) throw new Error(reason);
  }
  switch (action.type) {
    case "tap": {
      o.tapped = true;
      const p = player(s, actor);
      p.manaByColor[o.mana] = (p.manaByColor[o.mana] ?? 0) + 1;
      s.passes = [];
      return (
        o.name +
        " added one " +
        o.mana +
        " mana. Priority stays with " +
        p.name +
        "."
      );
    }
    case "play-land":
      move(s, o.id, "battlefield", actor);
      s.landsPlayed[actor] = (s.landsPlayed[actor] ?? 0) + 1;
      s.passes = [];
      return o.name + " played. This does not use the stack.";
    case "cast":
      if (o.targetKind && !legalSpellTarget(s, o, action.target))
        throw new Error("Choose a legal target before casting.");
      o.spellTarget = o.targetKind ? structuredClone(action.target) : null;
      paySampleCost(s, o, actor);
      move(s, o.id, "stack", actor);
      s.passes = [];
      return o.name + " cast. " + player(s, actor).name + " retains priority.";
    case "attack":
      if (action.defender === null || action.remove) delete s.attacks[o.id];
      else {
        const defender = action.defender ?? action.target;
        if (!living(s).some((p) => p.id === defender && p.id !== actor))
          throw new Error("Choose another player to attack.");
        s.attacks[o.id] = defender;
      }
      return (
        o.name +
        (s.attacks[o.id]
          ? " selected to attack " + player(s, s.attacks[o.id]).name
          : " removed from attackers") +
        ". Confirm attackers to finish the declaration."
      );
    case "block": {
      const target = action.attacker ?? null;
      const reason = blockPermission(s, o.id, target, actor);
      if (reason) throw new Error(reason);
      if (target === null) delete s.blocks[o.id];
      else s.blocks[o.id] = target;
      return (
        o.name +
        (target
          ? " blocks " + s.objects[target].name
          : " removed from blocking") +
        ". Confirm the full blocker declaration."
      );
    }
    case "assign-damage": {
      if (s.declaration !== "damage")
        throw new Error(
          "Damage assignments can only be edited in the damage step.",
        );
      const plan = s.damageAssignments.find((p) => p.source === action.id);
      if (!plan || s.objects[action.id]?.controller !== actor)
        throw new Error("Choose a damage source you control.");
      if (
        !Array.isArray(action.amounts) ||
        action.amounts.length !== plan.targets.length ||
        action.amounts.some((n) => !Number.isInteger(n) || n < 0)
      )
        throw new Error(
          "Enter one nonnegative whole amount per damage target.",
        );
      plan.targets.forEach((target, i) => (target.amount = action.amounts[i]));
      return "Damage split updated; confirm when the complete assignment is ready.";
    }
    case "pass": {
      s.passes.push(actor);
      if (s.passes.length === living(s).length)
        return s.zones.stack.length ? resolveTop(s) : advanceStep(s);
      s.priority = nextLiving(s, actor);
      return (
        player(s, actor).name +
        " passed. " +
        player(s, s.priority).name +
        " has priority."
      );
    }
    case "confirm-step": {
      if (s.step === "untap") return advanceStep(s);
      if (s.declaration === "attackers") {
        if (actor !== s.active)
          throw new Error("The active player declares attackers.");
        for (const id of Object.keys(s.attacks)) {
          const creature = s.objects[id];
          if (!/vigilance/i.test(creature.rules)) creature.tapped = true;
        }
        s.declaration = null;
        s.priority = s.active;
        s.passes = [];
        return (
          "Attackers declared together. " +
          player(s, s.active).name +
          " has priority."
        );
      }
      if (s.declaration === "blockers") {
        if (actor !== s.blockingPlayers[0])
          throw new Error("The indicated defender acts first.");
        for (const [blocker, attacker] of Object.entries(s.blocks))
          if (s.objects[blocker]?.controller === actor) {
            const reason = blockPermission(s, blocker, attacker, actor);
            if (reason) throw new Error(reason);
          }
        for (const [attacker, defender] of Object.entries(s.attacks))
          if (defender === actor) {
            const count = Object.values(s.blocks).filter(
              (id) => id === attacker,
            ).length;
            if (keyword(s.objects[attacker], "menace") && count === 1)
              throw new Error("Menace requires at least two blockers.");
            if (count) s.blockedAttackers[attacker] = true;
          }
        for (const blocker of Object.keys(s.blocks))
          if (s.objects[blocker]?.controller === actor)
            s.combatBlockers[blocker] = true;
        s.blockingPlayers.shift();
        if (!s.blockingPlayers.length) {
          s.declaration = null;
          s.priority = s.active;
        }
        return (
          player(s, actor).name +
          " confirmed blockers. " +
          (s.priority
            ? "The active player has priority."
            : "The next defending player declares blockers.")
        );
      }
      if (s.declaration === "damage") return dealCombatDamage(s);
      if (s.step === "cleanup") {
        if (s.zones[zoneKey("hand", s.active)].length > 7)
          throw new Error("Discard down to seven before finishing cleanup.");
        return advanceStep(s);
      }
      throw new Error("Pass priority to advance this step.");
    }
    case "discard":
      if (
        s.step !== "cleanup" ||
        actor !== s.active ||
        !o ||
        o.zone !== zoneKey("hand", actor) ||
        s.zones[o.zone].length <= 7
      )
        throw new Error("Discard only while cleaning up an oversized hand.");
      move(s, o.id, "graveyard");
      return o.name + " discarded for cleanup.";
    case "turn":
      throw new Error(
        "Finish the current steps and pass priority; turns cannot be skipped.",
      );
    case "resolve":
      throw new Error(
        "All players must pass priority to resolve the top spell.",
      );
    default:
      return null;
  }
}

export function reduce(state, action) {
  const s = structuredClone(state);
  let message = "";
  const o = action.id ? s.objects[action.id] : null;
  const who = action.player ? player(s, action.player) : player(s, s.viewer);
  const timed = turnInteraction(s, action);
  if (timed !== null) message = timed;
  else
    switch (action.type) {
      case "perspective":
        if (!player(s, action.player) || who.left)
          throw new Error("Choose a player still at the table.");
        s.viewer = action.player;
        message = `Viewing ${who.name}’s seat.`;
        break;
      case "life":
        who.life += action.delta;
        message = `${who.name}: ${who.life} life.`;
        break;
      case "mana":
        who.mana = Math.max(0, who.mana + action.delta);
        message = `${who.name}: ${who.mana} sample mana.`;
        break;
      case "draw": {
        const id = s.zones[zoneKey("library", s.viewer)][0];
        if (!id) throw new Error("The library is empty.");
        move(s, id, "hand");
        message = `${who.name} drew a card.`;
        break;
      }
      case "move":
        if (!o) throw new Error("Choose a card first.");
        if (
          o.zone.startsWith("library:") ||
          (o.zone.startsWith("hand:") && o.owner !== s.viewer)
        )
          throw new Error("This card is private.");
        move(s, o.id, action.zone);
        s.passes = [];
        message = `${o.name} → ${action.zone}. Sample move; costs and effects are not applied.`;
        break;
      case "counter":
        if (!o || o.zone !== "battlefield")
          throw new Error("Choose a permanent.");
        o.counters = Math.max(0, o.counters + action.delta);
        message = `${o.name}: ${o.counters} sample counters.`;
        break;
      case "control":
        if (!o || o.zone !== "battlefield" || !who || who.left)
          throw new Error("Choose a permanent and active controller.");
        if (o.controller !== who.id) o.controlledSinceTurn = s.turn;
        o.controller = who.id;
        removeFromCombat(s, o.id);
        message = `${who.name} now controls ${o.name}. Owner unchanged.`;
        break;
      case "sample-spells":
        for (const definition of ["spark", "demise"])
          addObject(s, definition, s.viewer, "hand");
        message =
          "Added Cinder Spark and Quiet Demise to your hand (prototype fixture).";
        break;
      case "token":
        s.objects[
          addObject(s, "token", s.viewer, "battlefield")
        ].controlledSinceTurn = s.turn;
        message = "Added a sample Saproling token.";
        break;
      default:
        throw new Error("Unknown interaction.");
    }
  const additionalDeaths = checkCreatureDeaths(s);
  if (
    s.declaration === "damage" &&
    ["move", "control", "counter", "token"].includes(action.type)
  )
    s.damageAssignments = buildCombatDamage(s);
  if (additionalDeaths.length)
    message += " Died: " + additionalDeaths.map((o) => o.name).join(", ") + ".";
  s.revision++;
  s.log.push(message);
  assertState(s);
  return s;
}
// Store owns notifications; views depend on this narrow command/subscription interface.
export class TableStore {
  #state;
  #listeners = new Set();
  constructor(state) {
    this.#state = state;
  }
  get state() {
    return this.#state;
  }
  subscribe(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
  dispatch(action) {
    this.#state = reduce(this.#state, action);
    for (const fn of this.#listeners) fn(this.#state);
  }
  reset(count, dense = false) {
    this.#state = createTable(count, dense);
    for (const fn of this.#listeners) fn(this.#state);
  }
}
