import {
  TableStore,
  createTable,
  player,
  living,
  contents,
  seats,
  visibleContents,
  TURN_STEPS,
  currentStep,
  actionPermission,
  combatStats,
  blockPermission,
  validateDamage,
  legalSpellTarget,
} from "./model.js";
import {
  cardArea,
  moveSlot,
  slotAt,
  reorderHand,
  screenCardWidth,
  screenAreaSlots,
  resizeSlots,
  centerSlotOffset,
} from "./battlefield-layout.js";
import { Arrangements, stackSignature, dropStackKind } from "./arrangements.js";

const $ = (id) => document.getElementById(id);
const store = new TableStore(createTable(4));
const dialog = $("inspector");
let selected = null;
let focusOpponent = "p2";
let dense = false;
const collapsed = new Set();
const expandedZones = new Map();
const arrangements = new Arrangements();
const stackSettings = { lands: true, tokens: true, matching: false };
const separated = new Set();
const handOrders = new Map();
let selectedBlocker = null;
let pendingSpell = null;
let stackOpen = true;
function orderedHand(state) {
  const cards = contents(state, "hand", state.viewer);
  const previous = handOrders.get(state.viewer) ?? [];
  const ids = [
    ...previous.filter((id) => cards.some((card) => card.id === id)),
    ...cards
      .filter((card) => !previous.includes(card.id))
      .map((card) => card.id),
  ];
  handOrders.set(state.viewer, ids);
  return ids.map((id) => cards.find((card) => card.id === id));
}
function renderHandCards() {
  $("hand").innerHTML =
    orderedHand(store.state)
      .map((o) => cardMarkup(o))
      .join("") || '<p class="empty-row">Your hand is empty.</p>';
  for (const card of $("hand").querySelectorAll("[data-card]")) {
    const object = store.state.objects[card.dataset.card];
    const type =
      store.state.step === "cleanup" &&
      store.state.active === store.state.viewer
        ? "discard"
        : object.mana
          ? "play-land"
          : "cast";
    card.setAttribute(
      "aria-label",
      (type === "discard"
        ? "Discard "
        : type === "play-land"
          ? "Play "
          : "Cast ") + object.name,
    );
    const reason =
      type === "discard"
        ? null
        : actionPermission(store.state, type, object.id);
    card.title =
      reason ??
      (type === "cast"
        ? "Cast · sample cost: 1 generic mana"
        : type === "play-land"
          ? "Play a land"
          : "Discard for cleanup");
    card.dataset.available = String(!reason);
    card.setAttribute(
      "aria-description",
      "Drag to reorder your hand. Alt plus left or right arrow moves this card. Click to cast or play a land when allowed; right-click for options. Sample spells cost 1 generic mana.",
    );
  }
}
function syncStacks() {
  const buckets = new Map();
  for (const o of contents(store.state, "battlefield")) {
    if (
      [...arrangements.groups.values()].some(
        (g) => g.manual && g.ids.includes(o.id),
      )
    )
      continue;
    const kind = o.mana ? "lands" : o.token ? "tokens" : "matching";
    if (!stackSettings[kind] || separated.has(o.id)) continue;
    const signature = stackSignature(o);
    let batch = 0;
    while ((buckets.get(signature + ":" + batch)?.ids.length ?? 0) >= 3)
      batch++;
    const key = signature + ":" + batch;
    if (!buckets.has(key))
      buckets.set(key, { ids: [], controller: o.controller, kind });
    buckets.get(key).ids.push(o.id);
  }
  for (const [key, g] of arrangements.groups)
    if (
      g.autoKey &&
      (!buckets.has(g.autoKey) || buckets.get(g.autoKey).ids.length < 2)
    )
      arrangements.groups.delete(key);
  for (const [key, b] of buckets) {
    if (b.ids.length < 2) continue;
    let group = [...arrangements.groups.values()].find(
      (g) => g.autoKey === key || g.ids.some((id) => b.ids.includes(id)),
    );
    if (group) {
      group.ids = [
        ...group.ids.filter((id) => b.ids.includes(id)),
        ...b.ids.filter((id) => !group.ids.includes(id)),
      ];
      group.autoKey = key;
      group.kind = b.kind;
      group.controller = b.controller;
    } else {
      arrangements.capture(
        b.controller,
        b.ids.map((id) => ({ id, x: 0, y: 0 })),
      );
      const id = arrangements.group(b.controller, b.ids, b.kind);
      group = arrangements.groups.get(id);
      if (group) {
        group.autoKey = key;
        group.manual = false;
      }
    }
  }
}
let drag = null;
let suppressClickUntil = 0;
const battlefieldObserver = new ResizeObserver((entries) => {
  for (const entry of entries) layoutSurface(entry.target);
});
const slotOrders = new Map();
const slotCapacities = new Map();
const slotAlignments = new Map();
let settlingDrag = false;
function layoutSurface(surface) {
  const controller = surface.closest(".seat").dataset.seat;
  const cards = [...surface.querySelectorAll(".game-card")];
  const byId = new Map(cards.map((c) => [c.dataset.card, c]));
  const groups = [...arrangements.groups.values()].filter(
    (g) => g.controller === controller,
  );
  surface.style.minHeight = "0px";
  const opponent = surface.closest(".opponent") !== null;
  const cardWidth = screenCardWidth(
    surface.clientWidth,
    surface.clientHeight,
    opponent,
  );
  const fanStep = Math.min(32, cardWidth * 0.4);
  surface.querySelectorAll(".area-label,.drop-slot").forEach((e) => e.remove());
  surface.slotAreas = {};
  for (const area of ["creatures", "support", "mana"]) {
    const objects = cards
      .map((c) => store.state.objects[c.dataset.card])
      .filter((o) => o && cardArea(o) === area);
    const hidden = new Set(groups.flatMap((g) => g.ids.slice(1)));
    const keys = objects.map((o) => o.id).filter((id) => !hidden.has(id));
    const orderKey = controller + ":" + area;
    const gap = groups.some(
      (g) => cardArea(store.state.objects[g.ids[0]]) === area,
    )
      ? fanStep * 2 + 12
      : 12;
    const available = screenAreaSlots(
      area,
      0,
      surface.clientWidth,
      surface.clientHeight,
      cardWidth,
      opponent,
      gap,
    ).slots.length;
    const order = resizeSlots(
      slotOrders.get(orderKey) ?? [],
      keys,
      slotCapacities.get(orderKey),
      available,
    );
    slotCapacities.set(orderKey, available);
    slotOrders.set(orderKey, order);
    const geometry = screenAreaSlots(
      area,
      order.length,
      surface.clientWidth,
      surface.clientHeight,
      cardWidth,
      opponent,
      gap,
    );
    const signature =
      keys.join(",") + ":" + available + ":" + gap + ":" + cardWidth;
    const previousAlignment = slotAlignments.get(orderKey);
    let offset = previousAlignment?.offset ?? 0;
    if (!settlingDrag && previousAlignment?.signature !== signature) {
      offset =
        centerSlotOffset(order, geometry.slots, geometry.span) + geometry.left;
    }
    slotAlignments.set(orderKey, { signature, offset });
    for (const slot of geometry.slots) slot.x += offset;
    const crowded = keys.length * (cardWidth + gap) > geometry.span;
    const exposed = order.flatMap((id) => {
      if (!id) return [];
      return groups.find((g) => g.ids[0] === id)?.ids ?? [id];
    });
    surface.slotAreas[area] = geometry;
    const title = document.createElement("span");
    title.className = "area-label";
    title.textContent =
      area === "creatures"
        ? "Creatures"
        : area === "support"
          ? "Other permanents"
          : "Mana sources";
    title.style.top = geometry.top + "px";
    title.style.left = geometry.left + 5 + "px";
    surface.append(title);
    order.forEach((id, index) => {
      const box = geometry.slots[index];
      if (!box) return;
      const slot = document.createElement("span");
      slot.className = "drop-slot";
      slot.dataset.area = area;
      slot.dataset.slot = index;
      slot.setAttribute("aria-hidden", "true");
      slot.style.cssText =
        "left:" +
        box.x +
        "px;top:" +
        box.y +
        "px;width:" +
        box.width +
        "px;height:" +
        box.height +
        "px;";
      surface.append(slot);
      if (!id) return;
      const group = groups.find((g) => g.ids[0] === id);
      const ids = group ? group.ids : [id];
      const w = box.width;
      const h = w * 1.4;
      const step = group ? fanStep : 0;
      ids.forEach((member, i) => {
        const card = byId.get(member);
        if (!card) return;
        const rank = exposed.indexOf(member);
        const crowdedX =
          geometry.left +
          (exposed.length > 1
            ? (rank * Math.max(0, geometry.span - w)) / (exposed.length - 1)
            : (geometry.span - w) / 2);
        card.style.cssText =
          "left:" +
          (crowded
            ? crowdedX
            : box.x - ((ids.length - 1) * step) / 2 + i * step) +
          "px;top:" +
          (box.y + i * ((box.height - h) / Math.max(1, ids.length - 1))) +
          "px;width:" +
          w +
          "px;height:" +
          h +
          "px;z-index:" +
          (crowded
            ? rank + 1
            : group
              ? i + 1
              : (arrangements.positions.get(member)?.z ?? 1)) +
          ";--card-scale:" +
          Math.min(1, w / 70) +
          ";";
        card.dataset.area = area;
        card.dataset.slot = index;
        const object = store.state.objects[member];
        if (object?.mana && surface.closest(".self")) {
          card.setAttribute(
            "aria-label",
            `${object.tapped ? "Tapped" : "Tap"} ${object.name}`,
          );
          card.title =
            "Tap for mana when you have priority. Untaps at your untap step. Right-click for options.";
        }
        card.setAttribute(
          "aria-description",
          "Drag to another " +
            area +
            " slot. Arrow keys swap adjacent slots. Click your land to add mana with priority; Right-click opens options.",
        );
      });
    });
  }
  updateGroupHandles(surface);
  scheduleCombatLinks();
}
function captureSurface(surface) {
  const controller = surface.closest(".seat").dataset.seat;
  arrangements.capture(
    controller,
    [...surface.querySelectorAll(".game-card")].map((card) => ({
      id: card.dataset.card,
      x:
        (card.offsetLeft - 5) /
        Math.max(1, surface.clientWidth - card.offsetWidth - 10),
      y:
        (card.offsetTop - 5) /
        Math.max(1, surface.clientHeight - card.offsetHeight - 10),
    })),
  );
}
function updateGroupHandles(surface) {
  const controller = surface.closest(".seat").dataset.seat;
  const groups = [...arrangements.groups].filter(
    ([, g]) => g.controller === controller,
  );
  for (const handle of surface.querySelectorAll(".group-handle"))
    if (!arrangements.groups.has(handle.dataset.group)) handle.remove();
  for (const [id, group] of groups) {
    const first = surface.querySelector(`[data-card="${group.ids[0]}"]`);
    if (!first) continue;
    let handle = surface.querySelector(`[data-group="${id}"]`);
    if (!handle) {
      handle = document.createElement("button");
      handle.type = "button";
      handle.className = "group-handle";
      handle.dataset.group = id;
      surface.append(handle);
    }
    handle.textContent = `${group.ids.length} · ${group.kind === "lands" ? "Lands" : group.kind === "tokens" ? "Tokens" : "Cards"}`;
    handle.setAttribute(
      "aria-label",
      `Browse ${group.ids.length} ${group.kind}; drag to move the group`,
    );
    handle.style.left = `${Math.max(0, Math.min(first.offsetLeft, surface.clientWidth - handle.offsetWidth - 3))}px`;
    handle.style.top = `${Math.max(0, first.offsetTop - handle.offsetHeight - 3)}px`;
    handle.style.zIndex = String(arrangements.z + 2);
    if (surface.closest(".self"))
      handle.setAttribute(
        "aria-description",
        "Drag or use arrow keys to move this group. Enter opens individual members.",
      );
  }
}
let announcementTimer;
function previewCard(target) {
  const button = target.closest(".play-area [data-card]");
  const preview = $("hover-preview");
  const o = button && store.state.objects[button.dataset.card];
  if (!o || dialog.open || drag?.moving) {
    preview.hidden = true;
    return;
  }
  preview.innerHTML = `${cardMarkup(o, false, false)}<strong>${esc(o.name)}</strong><p>${esc(o.type)}</p><p>Owner: ${esc(name(o.owner))}</p><p>${esc(o.rules)}</p>`;
  preview.hidden = false;
}
const esc = (text) =>
  String(text).replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
const label = (kind) => kind.charAt(0).toUpperCase() + kind.slice(1);
const name = (id) => player(store.state, id)?.name ?? "Unknown";
const options = (players, current) =>
  players
    .map(
      (p) =>
        `<option value="${p.id}" ${p.id === current ? "selected" : ""}>${esc(p.name)} · seat ${p.seat + 1}</option>`,
    )
    .join("");
function announce(message) {
  clearTimeout(announcementTimer);
  $("announcement").textContent = message;
  $("announcement").classList.add("show");
  announcementTimer = setTimeout(
    () => $("announcement").classList.remove("show"),
    4500,
  );
}
function dispatch(action, reopen = false) {
  try {
    const previousCard =
      action.type === "tap"
        ? document.querySelector(`.play-area [data-card="${action.id}"]`)
        : null;
    const previousTransform = previousCard
      ? getComputedStyle(previousCard).transform
      : null;
    if (action.type === "tap") {
      const reason = actionPermission(
        store.state,
        "tap",
        action.id,
        action.player ?? store.state.viewer,
      );
      if (reason) throw new Error(reason);
      const group = [...arrangements.groups.values()].find((g) =>
        g.ids.includes(action.id),
      );
      if (group) {
        group.manual = true;
        delete group.autoKey;
      }
    }
    store.dispatch(action);
    const nextCard =
      previousCard &&
      document.querySelector(`.play-area [data-card="${action.id}"]`);
    if (nextCard && !matchMedia("(prefers-reduced-motion: reduce)").matches) {
      nextCard.animate(
        [
          { transform: previousTransform },
          {
            transform: store.state.objects[action.id].tapped
              ? "rotate(90deg)"
              : "none",
          },
        ],
        { duration: 260, easing: "cubic-bezier(0.2, 0.7, 0.2, 1)" },
      );
    }
    if (
      action.type === "tap" &&
      store.state.objects[action.id]?.tapped &&
      store.state.objects[action.id]?.mana
    )
      document
        .querySelector(
          `[data-seat="${store.state.objects[action.id].controller}"] .mana-pool`,
        )
        ?.classList.add("mana-gain");
    announce(store.state.log.at(-1));
    if (reopen && selected && store.state.objects[selected]) openCard(selected);
    else if (dialog.open) dialog.close();
  } catch (error) {
    announce(error.message);
    if (dialog.open) {
      let errorBox = $("dialog-error");
      if (!errorBox) {
        errorBox = document.createElement("p");
        errorBox.id = "dialog-error";
        errorBox.className = "inline-error";
        errorBox.role = "alert";
        $("dialog-content").append(errorBox);
      }
      errorBox.textContent = error.message;
    }
  }
}
function cardMarkup(o, compact = false, interactive = true) {
  const s = store.state;
  const attack = s.attacks[o.id];
  const blocking = s.blocks[o.id];
  const blockers = Object.entries(s.blocks)
    .filter(([, attacker]) => attacker === o.id)
    .map(([id]) => "B" + id.slice(1));
  const combatText = attack
    ? "A" +
      o.id.slice(1) +
      " → " +
      name(attack) +
      (blockers.length ? " · " + blockers.join(", ") : "")
    : blocking
      ? "B" + o.id.slice(1) + " blocks A" + blocking.slice(1)
      : "";
  const classes = `game-card ${o.theme} ${compact ? "land-card" : ""} ${o.tapped ? "tapped" : ""} ${attack ? "combat-attacker" : ""} ${blocking ? "combat-blocker" : ""}`;
  const attrs = interactive
    ? `type="button" data-card="${o.id}" aria-label="Inspect ${esc(o.name)}${o.tapped ? ", tapped" : ""}${combatText ? `, ${esc(combatText)}` : ""}${o.damage ? `, ${o.damage} damage marked` : ""}"`
    : "";
  return `<${interactive ? "button" : "div"} class="${classes}" ${attrs}>
    <span class="card-name">${esc(o.name)}</span><span class="card-art" aria-hidden="true"></span>
    <span class="card-foot"><span>${o.mana ? "Land" : o.token ? "Token" : o.type.split(" ·")[0].replace("Legendary creature", "Creature")}</span><strong>${esc(o.stats ? `${combatStats(o).power} / ${combatStats(o).toughness}` : (o.mana ?? "◇"))}</strong></span>
    ${o.tapped ? '<span class="tap-mark">TAPPED</span>' : ""}${o.counters ? `<span class="counter-mark">+${o.counters}</span>` : ""}
    ${o.owner !== o.controller && o.zone === "battlefield" ? `<span class="owner-mark">Owner: ${esc(name(o.owner))}</span>` : ""}
    ${combatText ? `<span class="combat-mark">${esc(combatText)}</span>` : ""}${o.damage ? `<span class="damage-mark">${o.damage} dmg</span>` : ""}
  </${interactive ? "button" : "div"}>`;
}
function zoneButton(kind, p) {
  const shared = ["command", "exile"].includes(kind);
  const cards = contents(store.state, kind, shared ? null : p.id).filter(
    (o) => !shared || o.owner === p.id,
  );
  return `<button class="zone-link" data-expand-zone="${kind}" data-player="${p.id}" aria-expanded="${expandedZones.get(p.id) === kind}" aria-label="Inspect ${esc(p.name)}’s ${kind}, ${cards.length} cards"><strong>${cards.length}</strong>${label(kind)} <span aria-hidden="true">${expandedZones.get(p.id) === kind ? "▾" : "▸"}</span></button>`;
}
function zoneDrawer(p) {
  const kind = expandedZones.get(p.id);
  if (!kind) return "";
  const personal = kind === "library" || kind === "graveyard";
  const projection = visibleContents(store.state, kind, personal ? p.id : null);
  const cards = projection.cards.filter((o) => personal || o.owner === p.id);
  return `<div class="zone-drawer"><span class="eyebrow">${label(kind)}</span>${projection.hidden ? `<p>${projection.count} hidden cards</p>` : cards.map((o) => cardMarkup(o)).join("") || "<p>Empty zone</p>"}<button data-browse-zone="${kind}" data-player="${p.id}">Browse ↗</button></div>`;
}
function manaMarkup(p) {
  const names = {
    W: "white",
    U: "blue",
    B: "black",
    R: "red",
    G: "green",
    C: "colorless",
  };
  const pool = { ...p.manaByColor, C: (p.manaByColor?.C ?? 0) + p.mana };
  const entries = Object.entries(pool).filter(([, amount]) => amount > 0);
  return (
    '<output class="mana-pool" role="status" aria-live="polite" aria-label="Available sample mana for ' +
    esc(p.name) +
    '" ' +
    (entries.length ? "" : "hidden") +
    '><span class="mana-caption">Mana</span>' +
    entries
      .map(
        ([color, amount]) =>
          '<span class="mana-color mana-' +
          esc(color) +
          '" aria-label="' +
          amount +
          " " +
          (names[color] ?? color) +
          '"><b>' +
          esc(color === "C" ? "◇" : color) +
          "</b> " +
          amount +
          "</span>",
      )
      .join("") +
    "</output>"
  );
}
function seatMarkup(p, position) {
  const s = store.state;
  const objects = contents(s, "battlefield").filter(
    (o) => o.controller === p.id,
  );
  const creatures = objects.filter((o) =>
    o.type.toLowerCase().includes("creature"),
  );
  const support = objects.filter(
    (o) => !o.type.toLowerCase().includes("creature") && !o.mana,
  );
  const lands = objects.filter((o) => o.mana);
  const self = p.id === s.viewer;
  return `<section class="seat ${position} ${p.accent} ${self ? "self" : "opponent"} ${s.priority === p.id ? "has-priority" : ""} ${collapsed.has(p.id) && !self ? "collapsed" : ""} ${p.id === focusOpponent ? "focused" : ""}" aria-label="${esc(p.name)}’s player area" data-seat="${p.id}">
    <div class="seat-head"><span class="avatar" aria-hidden="true">${esc(p.name.slice(0, 1))}</span><div class="seat-name"><h3><button class="player-name" data-player-info="${p.id}" aria-label="${esc(p.name)} player details">${esc(p.name)}</button> ${self ? ' <span style="color:var(--muted);font-weight:400">/ viewing</span>' : ""}</h3><small>Seat ${p.seat + 1}${s.active === p.id ? " · active turn" : ""}${s.priority === p.id ? " · priority" : ""}</small></div>
    <div class="life-control"><button data-life="-1" data-player="${p.id}" aria-label="Decrease ${esc(p.name)}’s life">−</button><strong aria-label="${p.life} life">${p.life}</strong><button data-life="1" data-player="${p.id}" aria-label="Increase ${esc(p.name)}’s life">+</button></div></div>
    <div class="seat-meta">${self ? "" : `<button data-zone="hand" data-player="${p.id}">Hand <strong>${contents(s, "hand", p.id).length}</strong></button>`}${self ? '<button class="add-token" data-action="token">+ Token</button>' : `<button data-collapse="${p.id}" aria-label="${collapsed.has(p.id) ? "Expand" : "Collapse"} ${esc(p.name)}’s battlefield" aria-expanded="${!collapsed.has(p.id)}">${collapsed.has(p.id) ? "+ Expand" : "− Hide"}</button>`}</div>
    <div class="play-area" role="region" aria-label="${esc(p.name)}’s permanents" tabindex="0">
    ${[...creatures, ...support].map((o) => cardMarkup(o)).join("")}${lands.map((o) => cardMarkup(o, true)).join("")}${!objects.length ? '<span class="empty-surface">No permanents</span>' : ""}
    </div><nav class="zone-links ${expandedZones.has(p.id) ? "is-open" : ""}" aria-label="${esc(p.name)}’s zone shortcuts">${["library", "graveyard", "exile", "command"].map((z) => zoneButton(z, p)).join("")}${zoneDrawer(p)}</nav>${manaMarkup(p)}
  </section>`;
}
function centerMarkup() {
  const s = store.state;
  return (
    '<section class="center" aria-label="Current action">' +
    (pendingSpell
      ? "<strong>Choose a target for " +
        esc(s.objects[pendingSpell]?.name) +
        '</strong><span>Click a highlighted creature or player.</span><button data-cancel-target="true">Cancel targeting</button>'
      : (s.declaration === "attackers"
          ? '<label class="attack-choice">Attack <select id="attack-defender">' +
            options(
              living(s).filter((p) => p.id !== s.active),
              living(s).find((p) => p.id !== s.active)?.id,
            ) +
            "</select></label>"
          : "") +
        (s.declaration === "damage"
          ? '<button data-edit-damage="true">Adjust damage</button>'
          : "") +
        '<span class="timing-note">' +
        (s.priority
          ? "Cast, activate, or pass priority."
          : s.declaration === "blockers"
            ? "Select a blocker, then its attacker."
            : s.declaration === "attackers"
              ? "Select attackers, then confirm above."
              : s.declaration === "damage"
                ? "Review combat assignments, then deal damage above."
                : s.step === "untap"
                  ? "Untap is automatic. Continue above."
                  : "Complete the current step above.") +
        "</span>") +
    "</section>"
  );
}
function renderStack() {
  const s = store.state,
    cards = contents(s, "stack").toReversed();
  $("stack-panel").classList.toggle("collapsed", !stackOpen);
  $("stack-panel").innerHTML =
    '<button class="stack-toggle" data-toggle-stack="true" aria-expanded="' +
    stackOpen +
    '">' +
    (stackOpen ? "›" : "‹") +
    " Stack · " +
    cards.length +
    "</button>" +
    (stackOpen
      ? '<div class="stack-items">' +
        (cards.length
          ? cards
              .map(
                (o, i) =>
                  "<article><small>" +
                  (i === 0 ? "Resolves next" : "Below") +
                  " · " +
                  esc(name(o.controller)) +
                  "</small>" +
                  cardMarkup(o, false, true) +
                  "<p>" +
                  esc(o.rules) +
                  "</p>" +
                  (o.spellTarget
                    ? "<p>Target: " +
                      esc(
                        o.spellTarget.kind === "player"
                          ? name(o.spellTarget.id)
                          : (s.objects[o.spellTarget.id]?.name ??
                              "Left battlefield"),
                      ) +
                      "</p>"
                    : "") +
                  "</article>",
              )
              .join("")
          : '<p class="stack-empty">Stack empty</p>') +
        "</div>"
      : "");
}
function chooseSpellTarget(target) {
  const spell = store.state.objects[pendingSpell];
  if (!legalSpellTarget(store.state, spell, target)) {
    announce("That is not a legal target.");
    return;
  }
  const id = pendingSpell;
  pendingSpell = null;
  dispatch({ type: "cast", id, target });
  render();
}
function applyTargetHints() {
  const s = store.state,
    spell = s.objects[pendingSpell];
  for (const el of document.querySelectorAll(
    ".play-area [data-card], [data-player-info]",
  )) {
    const target = el.dataset.card
      ? { kind: "object", id: el.dataset.card }
      : { kind: "player", id: el.dataset.playerInfo };
    el.classList.toggle(
      "spell-target",
      !!spell && legalSpellTarget(s, spell, target),
    );
  }
}
function render() {
  if (drag) finishDrag(true);
  if (
    store.state.declaration !== "blockers" ||
    !store.state.objects[selectedBlocker] ||
    store.state.objects[selectedBlocker]?.controller !== store.state.viewer
  )
    selectedBlocker = null;
  arrangements.prune(store.state.objects);
  syncStacks();
  $("hover-preview").hidden = true;
  battlefieldObserver.disconnect();
  const s = store.state;
  if (pendingSpell && actionPermission(s, "cast", pendingSpell))
    pendingSpell = null;
  $("priority-status").innerHTML =
    '<button data-focus-player="' +
    (s.priority ?? s.active) +
    '" title="View this player">' +
    (s.priority
      ? esc(name(s.priority)) + " has priority"
      : "No priority · step action") +
    "</button>";
  $("pass-priority").hidden = !s.priority;
  $("pass-priority").disabled = !!pendingSpell;
  $("pass-priority").dataset.player = s.priority ?? "";
  const ordered = seats(s);
  const opponents = ordered.slice(1);
  if (!opponents.some((p) => p.id === focusOpponent))
    focusOpponent = opponents[0].id;
  $("perspective").innerHTML = options(living(s), s.viewer);
  $("opponent-focus").innerHTML = options(opponents, focusOpponent);
  $("player-count").value = String(s.players.length);
  $("topology").textContent =
    `${s.players.length} players · ${Object.keys(s.zones).length} zones · Commander-style fixture`;
  $("active-name").textContent = `${name(s.active)}’s turn`;
  const step = currentStep(s);
  $("current-step").textContent =
    step.phase + (step.label === "Main phase" ? "" : " · " + step.label);
  const confirm = $("next-turn");
  confirm.hidden = !!s.priority;
  confirm.textContent =
    s.step === "untap"
      ? "Continue to upkeep →"
      : s.declaration === "attackers"
        ? "Confirm attackers →"
        : s.declaration === "blockers"
          ? "Confirm blocks: " + name(s.blockingPlayers[0])
          : s.declaration === "damage"
            ? "Deal " +
              (s.step === "first-damage" ? "first-strike" : "combat") +
              " damage →"
            : "Finish cleanup →";
  $("turn-number").textContent = s.turn;
  $("turn-order").innerHTML = s.players
    .map(
      (p) =>
        `<li class="${p.id === s.active ? "active" : ""}"><span>${p.seat + 1}</span>${esc(p.name)}</li>`,
    )
    .join("");
  $("table").className = `table count-${s.players.length}`;
  $("table").style.setProperty("--opponent-share", "0.48");
  $("table").innerHTML =
    `<div class="opponents" aria-label="Opponent battlefields">${opponents.map((p) => seatMarkup(p, "top")).join("")}</div>` +
    seatMarkup(ordered[0], "bottom") +
    centerMarkup();
  renderStack();
  applyTargetHints();
  $("density").setAttribute("aria-pressed", String(dense));
  const hand = orderedHand(s);
  $("hand-heading").textContent = `${name(s.viewer)}’s hand`;
  $("hand-count").textContent = `${hand.length} cards · visible to this seat`;
  renderHandCards();
  $("draw").disabled = true;
  $("draw").textContent = "Draw step: automatic";
  document.querySelectorAll(".play-area").forEach((surface) => {
    layoutSurface(surface);
    battlefieldObserver.observe(surface);
  });
}
function showDialog(kicker, title, html) {
  $("hover-preview").hidden = true;
  const previous = dialog.open ? document.activeElement : null;
  const focusId = previous?.id;
  const action = previous?.dataset?.action;
  $("dialog-kicker").textContent = kicker;
  $("dialog-title").textContent = title;
  $("dialog-content").innerHTML = html;
  if (!dialog.open) dialog.showModal();
  else if (focusId && $(focusId)) $(focusId).focus();
  else if (action) dialog.querySelector(`[data-action="${action}"]`)?.focus();
  else $("close-dialog").focus();
}
function openCard(id) {
  const s = store.state;
  const o = s.objects[id];
  if (
    !o ||
    o.zone.startsWith("library:") ||
    (o.zone.startsWith("hand:") && o.owner !== s.viewer)
  )
    return;
  selected = id;
  const zone = o.zone.split(":")[0];
  const onField = zone === "battlefield";
  const ownCreature =
    onField &&
    o.controller === s.viewer &&
    o.type.toLowerCase().includes("creature");
  const moves = [
    "battlefield",
    "hand",
    "graveyard",
    "exile",
    "command",
    "library",
    "stack",
  ].filter((z) => z !== zone);
  showDialog(
    "CARD INSPECTOR · SAMPLE OBJECT",
    o.name,
    `<div class="detail-preview">${cardMarkup(o, false, false)}<div><p>${esc(o.type)}</p><dl class="facts"><dt>Owner</dt><dd>${esc(name(o.owner))}</dd><dt>${onField || zone === "stack" ? "Controller" : "Zone"}</dt><dd>${onField || zone === "stack" ? esc(name(o.controller)) : label(zone)}</dd><dt>Location</dt><dd>${label(zone)}</dd><dt>Card identity</dt><dd>${o.card}</dd><dt>Object identity</dt><dd>${o.id}</dd>${onField ? `<dt>Status</dt><dd>${o.tapped ? "Tapped" : "Untapped"}</dd><dt>Sample counters</dt><dd>${o.counters}</dd>` : ""}</dl></div></div><p>${esc(o.rules)}</p>
    ${onField && o.controller === s.viewer ? `<section class="inspector-section"><h3>Arrangement</h3><div class="actions"><button data-front="${id}">Bring to front</button><button data-detach="${id}" ${arrangements.members(id).length < 2 ? "disabled" : ""}>Separate from group</button></div></section>` : ""}
    ${onField ? `<section class="inspector-section"><h3>Permanent state</h3><div class="actions"><button data-action="tap" data-id="${id}" ${actionPermission(s, "tap", id) ? "disabled" : ""}>Tap for mana</button><button data-action="counter" data-id="${id}" data-delta="1">+ Counter</button><button data-action="counter" data-id="${id}" data-delta="-1" ${o.counters ? "" : "disabled"}>− Counter</button></div><div class="actions move-row"><label class="field-label">Change controller<select id="new-controller">${options(living(s), o.controller)}</select></label><button data-control="${id}">Apply control</button></div></section>` : ""}
    ${
      ownCreature
        ? `<section class="inspector-section"><h3>Combat preview</h3><div class="actions"><label class="field-label">Defending player<select id="defender"><option value="">Not attacking</option>${options(
            living(s).filter((p) => p.id !== s.viewer),
            s.attacks[id],
          )}</select></label><button data-attack="${id}">Set defender</button></div><p class="dialog-note">Choose during the declare attackers action, then confirm the full declaration. Select attackers now; defenders choose blockers next. Damage is assigned and dealt in the combat damage step.</p></section>`
        : ""
    }
    <section class="inspector-section"><h3>Fixture editor · zone move</h3><div class="actions"><label class="field-label">Destination<select id="move-zone">${moves.map((z) => `<option value="${z}">${z === "stack" ? "Stack · fixture move" : z === "library" ? "Library · top" : label(z)}</option>`).join("")}</select></label><button class="primary" data-move="${id}">Move card →</button></div><p class="dialog-note">Personal zones use the card’s owner. A zone change creates a new object identity. These fixture edits bypass gameplay timing. Use left-click to cast or play a card under the turn rules.</p></section>`,
  );
}
function openZone(kind, who = null) {
  const s = store.state;
  const personal = ["library", "hand", "graveyard"].includes(kind);
  const projection = visibleContents(s, kind, personal ? who : null);
  const cards = projection.cards.filter(
    (o) => !who || personal || o.owner === who,
  );
  const count = projection.hidden ? projection.count : cards.length;
  const shared = personal ? "PLAYER ZONE" : "SHARED ZONE";
  const ownerTitle = who ? `${name(who)}’s ` : "";
  const definitions = {
    library:
      "Ordered, hidden deck. Counts are public; card faces and sequence are private.",
    hand: "Hand faces are visible only to the player holding them unless a rule grants access.",
    graveyard:
      "Public, ordered discard pile. Cards go to their owner’s graveyard.",
    battlefield:
      "One shared battlefield. Screen groups show the current controller, not a separate zone.",
    exile:
      "One shared zone. Owner filters are shortcuts; linked exile groups will be preserved by the engine.",
    command:
      "One shared zone for commanders, emblems, and supported variant objects.",
    stack: "One ordered stack, shared by everyone. The newest entry is on top.",
  };
  showDialog(
    `${shared} · ${count} ${count === 1 ? "OBJECT" : "OBJECTS"}`,
    `${ownerTitle}${label(kind)}`,
    `<p class="shared-definition">${definitions[kind]}${who && !personal ? " This view is filtered by owner." : ""}</p>${projection.hidden ? `<div class="privacy"><strong>${count} cards</strong><p>${kind === "library" ? "Library contents are hidden from this view." : "This hand is private to another seat."}</p></div>${kind === "library" && who === s.viewer ? '<div class="actions inspector-section"><button class="primary" data-action="draw">Draw top card</button></div>' : ""}` : `<div class="zone-browser">${(kind === "graveyard" || kind === "stack" ? cards.toReversed() : cards).map((o) => cardMarkup(o)).join("") || "<p>This zone is empty.</p>"}</div>`}<p class="dialog-note">${kind === "graveyard" ? "Top of graveyard first. Browsing never changes the underlying order." : kind === "library" ? "Sample library order is held in browser memory. Production hidden data must remain on the server." : "Select a card to inspect its state and available prototype controls."}</p>`,
  );
}
function openPlayer(id) {
  const p = player(store.state, id);
  const s = store.state;
  showDialog(
    `PLAYER · SEAT ${p.seat + 1}`,
    p.name,
    `<dl class="facts"><dt>Life</dt><dd>${p.life}</dd><dt>Participation</dt><dd>At the table</dd><dt>Turn status</dt><dd>${s.active === id ? "Active player" : "Nonactive player"}</dd><dt>Priority</dt><dd>${s.priority === id ? "Holding priority" : "Waiting"}</dd><dt>Available sample mana</dt><dd>${
      Object.entries(p.manaByColor ?? {})
        .filter(([, n]) => n > 0)
        .map(([color, n]) => `${n} ${color}`)
        .join(" · ") || (p.mana ? `${p.mana} ◇` : "None")
    }</dd><dt>Poison</dt><dd>0</dd><dt>Commander damage received</dt><dd>0 from each commander</dd><dt>Command-zone casts</dt><dd>0</dd></dl><p class="dialog-note">Commander counters are defined display placeholders in this fixture. The floating mana display follows sample land taps. Prototype timing and sample costs are enforced; full mana abilities and card effects are not implemented.</p><section class="inspector-section"><h3>Explore this player’s zones</h3><div class="actions">${["library", "hand", "graveyard", "exile", "command"].map((z) => `<button data-zone="${z}" data-player="${id}">${label(z)}</button>`).join("")}</div></section>`,
  );
}
function openArrangement() {
  const own = contents(store.state, "battlefield").filter(
    (o) => o.controller === store.state.viewer,
  );
  const lands = own.filter((o) => o.mana).length;
  const tokens = own.filter((o) => o.token).length;
  showDialog(
    "BATTLEFIELD ARRANGEMENT",
    "Arrange your board",
    `<div class="actions"><button data-stack-setting="lands" aria-pressed="${stackSettings.lands}">Auto-stack lands · ${stackSettings.lands ? "On" : "Off"}</button><button data-stack-setting="tokens" aria-pressed="${stackSettings.tokens}">Auto-group tokens · ${stackSettings.tokens ? "On" : "Off"}</button><button data-stack-setting="matching" aria-pressed="${stackSettings.matching}">Other same-name cards · ${stackSettings.matching ? "On" : "Off"}</button></div><p>Matching lands and tokens stack automatically. Other same-name cards can be grouped with the option above. New automatic groups match tapped states, counters and characteristics. Tapping a member of an existing pile keeps it in place; only dragging it out separates it. Card positions are centered on a stable grid across each area.</p><p>Drag a card to another slot within its own area. Creatures stay in creature slots, lands in mana slots, and other permanents in support slots. Occupied slots swap. Drag a count handle to move a whole pile.</p><div class="actions"><button data-pack="lands" ${lands < 2 ? "disabled" : ""}>Stack lands · ${lands}</button><button data-pack="tokens" ${tokens < 2 ? "disabled" : ""}>Group tokens · ${tokens}</button><button data-add-tokens="true">Add 5 sample tokens</button><button data-auto-arrange="true">Auto arrange my board</button></div><p>Drag an exposed edge to pull one card out of a pile. Click an untapped land to add mana when you have priority; Right-click opens its card menu. The count handle moves the whole pile and can still open its member browser. Tab reaches every card. Arrow keys move a focused card or pile to an adjacent slot in the same area. Enter performs the same primary action as left-click. Right-click opens card options. Escape cancels a drag.</p><p class="dialog-note">Arrangements last through table interactions and perspective changes. Reset, player-count changes, busy-board changes or reloading start a fresh layout. Switch perspective to arrange another player’s board.</p>`,
  );
}
function openGroup(key) {
  const group = arrangements.groups.get(key);
  if (!group) return;
  const own = group.controller === store.state.viewer;
  showDialog(
    "INDIVIDUAL GROUP MEMBERS",
    `${group.ids.length} ${group.kind}`,
    `<p>Every card below is a separate rules object. Select one to inspect it, bring it to the front, or separate it from this group.</p><div class="zone-browser">${group.ids
      .map((id) => store.state.objects[id])
      .filter(Boolean)
      .map(
        (o) =>
          `<div class="group-member">${cardMarkup(o)}<span>${o.id} · ${o.tapped ? "tapped" : "untapped"}${o.counters ? ` · ${o.counters} counters` : ""}</span></div>`,
      )
      .join(
        "",
      )}</div>${own ? `<div class="actions inspector-section"><button data-spread="${key}">Spread this group</button></div>` : ""}`,
  );
}
function packCards(kind) {
  const surface = document.querySelector(".self .play-area");
  captureSurface(surface);
  const own = contents(store.state, "battlefield").filter(
    (o) =>
      o.controller === store.state.viewer &&
      (kind === "lands" ? o.mana : o.token),
  );
  const partitions = new Map();
  for (const o of own) {
    const key = `${o.name}:${o.stats ?? ""}`;
    if (!partitions.has(key)) partitions.set(key, []);
    partitions.get(key).push(o.id);
  }
  let packed = 0;
  for (const ids of partitions.values()) {
    if (ids.length < 2) continue;
    const first = surface.querySelector(`[data-card="${ids[0]}"]`);
    const w = first.offsetWidth,
      h = first.offsetHeight;
    const maxX = Math.max(1, surface.clientWidth - w - 10),
      maxY = Math.max(1, surface.clientHeight - h - 10);
    const stepX = Math.min(18, maxX / (ids.length + 1));
    const stepY = Math.min(5, maxY / (ids.length + 1));
    const anchorX = Math.min(
      first.offsetLeft - 5,
      maxX - stepX * (ids.length - 1),
    );
    const anchorY = Math.min(
      first.offsetTop - 5,
      maxY - stepY * (ids.length - 1),
    );
    ids.forEach((id, i) => {
      arrangements.positions.set(id, {
        controller: store.state.viewer,
        x: Math.max(0, anchorX + i * stepX) / maxX,
        y: Math.max(0, anchorY + i * stepY) / maxY,
        z: arrangements.z++,
      });
    });
    arrangements.group(store.state.viewer, ids, kind);
    packed += ids.length;
  }
  layoutSurface(surface);
  dialog.close();
  announce(
    packed
      ? `${packed} ${kind} arranged into overlapping groups. Use a count handle to select any member.`
      : "At least two matching cards are needed for a group.",
  );
}
function finishDrag(cancel = false) {
  const current = drag;
  if (!current) return;
  settlingDrag = true;
  const before = new Map(
    [...current.surface.querySelectorAll("[data-card]")].map((card) => [
      card.dataset.card,
      card.getBoundingClientRect(),
    ]),
  );
  drag = null;
  const valid =
    current.moving &&
    !cancel &&
    current.targetSlot !== null &&
    current.targetSlot !== undefined;
  if (valid) {
    const orderKey =
      current.surface.closest(".seat").dataset.seat + ":" + current.area;
    const targetId = slotOrders.get(orderKey)?.[current.targetSlot];
    const targetGroup = [...arrangements.groups.values()].find((g) =>
      g.ids.includes(targetId),
    );
    const targetIds = targetGroup ? [...targetGroup.ids] : [targetId];
    const kind = !current.ids.includes(targetId)
      ? dropStackKind(
          store.state.objects[current.ids[0]],
          store.state.objects[targetId],
          stackSettings,
        )
      : null;
    const onTarget = targetIds.some((id) => {
      const card = current.surface.querySelector(`[data-card="${id}"]`);
      if (!card) return false;
      const bounds = card.getBoundingClientRect();
      const surfaceBounds = current.surface.getBoundingClientRect();
      const x = current.dropX + surfaceBounds.left;
      const y = current.dropY + surfaceBounds.top;
      return (
        x >= bounds.left &&
        x <= bounds.right &&
        y >= bounds.top &&
        y <= bounds.bottom
      );
    });
    if (kind && onTarget) {
      const ids = [...new Set([...targetIds, ...current.ids])];
      for (const id of ids) separated.delete(id);
      const key = arrangements.group(store.state.viewer, ids, kind);
      if (key) arrangements.groups.get(key).manual = true;
      current.stacked = true;
    }
    if (!current.stacked && !targetGroup?.ids.includes(current.ids[0])) {
      const destination =
        current.surface.slotAreas[current.area].slots[current.targetSlot];
      const key =
        current.surface.closest(".seat").dataset.seat + ":" + current.area;
      let order = [...slotOrders.get(key)];
      if (!current.group) {
        const oldGroup = [...arrangements.groups.values()].find((g) =>
          g.ids.includes(current.ids[0]),
        );
        const oldAnchor = oldGroup?.ids[0];
        arrangements.removeFromGroup(current.ids[0]);
        separated.add(current.ids[0]);
        if (oldGroup && oldAnchor === current.ids[0]) {
          const remaining = oldGroup.ids[0];
          order = order.map((id) => (id === oldAnchor ? remaining : id));
        }
      }
      if (order.includes(current.ids[0]))
        order = moveSlot(order, current.ids[0], current.targetSlot);
      else {
        const displaced = order[current.targetSlot];
        order[current.targetSlot] = current.ids[0];
        if (displaced) {
          const empty = order.indexOf(null);
          if (empty >= 0) order[empty] = displaced;
          else order.push(displaced);
        }
      }
      slotOrders.set(key, order);
      // Detaching the last pile can change the spacing. Remap the destination
      // by its actual location rather than reusing an index from the old grid.
      layoutSurface(current.surface);
      const target = slotAt(
        current.surface.slotAreas[current.area],
        destination.x + destination.width / 2,
        destination.y + destination.height / 2,
      );
      if (target !== null)
        slotOrders.set(
          key,
          moveSlot(slotOrders.get(key), current.ids[0], target),
        );
    }
  }
  current.button.classList.remove("is-dragging");
  for (const id of current.ids)
    current.surface
      .querySelector(`[data-card="${id}"]`)
      ?.classList.remove("is-dragging");
  current.surface.classList.remove("is-arranging", "invalid-drop");
  if (current.button.hasPointerCapture?.(current.pointer))
    current.button.releasePointerCapture(current.pointer);
  layoutSurface(current.surface);
  settlingDrag = false;
  if (
    current.moving &&
    !matchMedia("(prefers-reduced-motion: reduce)").matches
  ) {
    for (const card of current.surface.querySelectorAll("[data-card]")) {
      const old = before.get(card.dataset.card);
      const next = card.getBoundingClientRect();
      if (!old) continue;
      const rotation = store.state.objects[card.dataset.card].tapped
        ? " rotate(90deg)"
        : "";
      const dx = old.left - next.left,
        dy = old.top - next.top;
      if (Math.abs(dx) + Math.abs(dy) > 1)
        card.animate(
          [
            { transform: `translate(${dx}px, ${dy}px)${rotation}` },
            { transform: rotation || "none" },
          ],
          { duration: 180, easing: "cubic-bezier(0.2, 0.7, 0.2, 1)" },
        );
    }
  }
  if (current.moving) {
    suppressClickUntil = Date.now() + 350;
    announce(
      cancel
        ? "Drag cancelled."
        : valid
          ? current.stacked
            ? "Cards stacked. Use the count handle to select any member."
            : "Moved to a " + current.area + " slot."
          : "Keep this card in its " + current.area + " area.",
    );
  }
}

document.addEventListener("pointerdown", (event) => {
  suppressClickUntil = 0;
  const button = event.target.closest(
    ".self .play-area [data-card], .self .play-area [data-group]",
  );
  if (!button || event.button !== 0 || dialog.open || pendingSpell) return;
  const surface = button.closest(".play-area");
  captureSurface(surface);
  const group =
    button.dataset.group && arrangements.groups.get(button.dataset.group);
  const ids = group ? [...group.ids] : [button.dataset.card];
  const first = surface.querySelector(`[data-card="${ids[0]}"]`);
  for (const id of ids)
    surface
      .querySelector(`[data-card="${id}"]`)
      .getAnimations()
      .forEach((animation) => animation.cancel());
  drag = {
    button,
    surface,
    ids,
    group: !!group,
    area: first.dataset.area,
    targetSlot: null,
    pointer: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    grabX:
      event.clientX - surface.getBoundingClientRect().left - first.offsetLeft,
    grabY:
      event.clientY - surface.getBoundingClientRect().top - first.offsetTop,
    cardWidth: first.offsetWidth,
    cardHeight: first.offsetHeight,
    moving: false,
  };
  button.setPointerCapture(event.pointerId);
});
document.addEventListener("pointermove", (event) => {
  if (!drag || event.pointerId !== drag.pointer) return;
  if (
    !drag.moving &&
    Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < 6
  )
    return;
  drag.moving = true;
  event.preventDefault();
  $("hover-preview").hidden = true;
  drag.button.classList.add("is-dragging");
  drag.surface.classList.add("is-arranging");
  const bounds = drag.surface.getBoundingClientRect();
  const x = event.clientX - bounds.left,
    y = event.clientY - bounds.top;
  drag.dropX = x;
  drag.dropY = y;
  drag.targetSlot =
    x > drag.surface.clientWidth
      ? null
      : slotAt(
          drag.surface.slotAreas[drag.area],
          x - drag.grabX + drag.cardWidth / 2,
          y - drag.grabY + drag.cardHeight / 2,
        );
  drag.surface.classList.toggle("invalid-drop", drag.targetSlot === null);
  drag.surface
    .querySelectorAll(".drop-slot")
    .forEach((slot) =>
      slot.classList.toggle(
        "is-target",
        slot.dataset.area === drag.area &&
          Number(slot.dataset.slot) === drag.targetSlot,
      ),
    );
  for (const id of drag.ids) {
    const card = drag.surface.querySelector('[data-card="' + id + '"]');
    if (card) {
      card.classList.add("is-dragging");
      card.style.transform =
        "translate(" +
        (event.clientX - drag.startX) +
        "px," +
        (event.clientY - drag.startY) +
        "px)" +
        (store.state.objects[id].tapped ? " rotate(90deg)" : "");
    }
  }
});
document.addEventListener("pointerup", (event) => {
  if (drag?.pointer === event.pointerId) finishDrag();
});
document.addEventListener("pointercancel", (event) => {
  if (drag?.pointer === event.pointerId) finishDrag(true);
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && drag) {
    finishDrag(true);
    event.preventDefault();
    return;
  }
  const card = event.target.closest(
    ".self .play-area [data-card], .self .play-area [data-group]",
  );
  if (
    !card ||
    !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)
  )
    return;
  event.preventDefault();
  const surface = card.closest(".play-area");
  captureSurface(surface);
  const ids = card.dataset.group
    ? (arrangements.groups.get(card.dataset.group)?.ids ?? [])
    : [card.dataset.card];
  if (!ids.length) return;
  if (card.dataset.card) {
    arrangements.removeFromGroup(card.dataset.card);
    separated.add(card.dataset.card);
  }
  layoutSurface(surface);
  const first = surface.querySelector('[data-card="' + ids[0] + '"]');
  if (!first) return;
  const area = first.dataset.area,
    key = store.state.viewer + ":" + area,
    order = slotOrders.get(key);
  const delta = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
  const target = Math.max(
    0,
    Math.min(order.length - 1, order.indexOf(ids[0]) + delta),
  );
  slotOrders.set(key, moveSlot(order, ids[0], target));
  layoutSurface(surface);
  $("hover-preview").hidden = true;
  announce("Moved to adjacent " + area + " slot.");
});
function openGuide() {
  showDialog(
    "BOARD GUIDE",
    "Battlefield areas",
    "<p>Cards snap to defined slots: creatures, other permanents, and mana sources. Drag within an area to swap positions or use arrow keys. Invalid drops return to the starting slot.</p><p>Arrange board offers overlapping land piles and token groups. Every group member remains individually inspectable. This prototype uses sample state.</p>",
  );
}
document.addEventListener("click", (event) => {
  if (Date.now() < suppressClickUntil && event.detail > 0) {
    event.preventDefault();
    return;
  }
  const button = event.target.closest("button");
  if (!button || button.disabled) return;
  const d = button.dataset;
  if (d.combatReport) return openCombatReport();
  if (d.editDamage) return openDamageAssignments();
  if (d.saveDamage) return saveDamageAssignments();
  if (d.focusPlayer)
    return dispatch({ type: "perspective", player: d.focusPlayer });
  if (d.arrange) return openArrangement();
  if (d.stackSetting) {
    stackSettings[d.stackSetting] = !stackSettings[d.stackSetting];
    for (const [id, g] of arrangements.groups)
      if (g.kind === d.stackSetting) arrangements.groups.delete(id);
    separated.clear();
    render();
    openArrangement();
    return;
  }
  if (d.group) return openGroup(d.group);
  if (d.pack) return packCards(d.pack);
  if (d.autoArrange) {
    arrangements.resetController(store.state.viewer);
    for (const key of slotOrders.keys())
      if (key.startsWith(store.state.viewer + ":")) slotOrders.delete(key);
    render();
    dialog.close();
    announce("Your board was automatically arranged.");
    return;
  }
  if (d.addTokens) {
    for (let i = 0; i < 5; i++) store.dispatch({ type: "token" });
    openArrangement();
    announce("Created five separate Saproling tokens.");
    return;
  }
  if (d.front || d.detach) {
    const surface = document.querySelector(".self .play-area");
    captureSurface(surface);
    const id = d.front || d.detach;
    arrangements.front(id);
    if (d.detach) {
      arrangements.removeFromGroup(id);
      separated.add(id);
    }
    layoutSurface(surface);
    dialog.close();
    return;
  }
  if (d.spread) {
    const group = arrangements.groups.get(d.spread);
    if (group) {
      for (const id of group.ids) {
        arrangements.positions.delete(id);
        separated.add(id);
      }
      arrangements.groups.delete(d.spread);
    }
    render();
    dialog.close();
    return;
  }
  if (d.expandZone) {
    if (expandedZones.get(d.player) === d.expandZone)
      expandedZones.delete(d.player);
    else expandedZones.set(d.player, d.expandZone);
    render();
    return;
  }
  if (d.browseZone) return openZone(d.browseZone, d.player);
  if (d.collapse) {
    if (collapsed.has(d.collapse)) collapsed.delete(d.collapse);
    else collapsed.add(d.collapse);
    render();
    return;
  }
  if (d.toggleStack) {
    stackOpen = !stackOpen;
    renderStack();
    return;
  }
  if (d.cancelTarget) {
    pendingSpell = null;
    render();
    return;
  }
  if (pendingSpell && d.playerInfo)
    return chooseSpellTarget({ kind: "player", id: d.playerInfo });
  if (d.card) {
    if (pendingSpell) return chooseSpellTarget({ kind: "object", id: d.card });
    if (button.closest("#stack-panel")) {
      announce(
        "Right-click this spell for Card inspector. Its effect and target are listed below it.",
      );
      return;
    }
    const object = store.state.objects[d.card];
    if (
      object?.mana &&
      object.zone === "battlefield" &&
      object.controller === store.state.viewer &&
      button.closest(".play-area") &&
      !event.shiftKey
    ) {
      dispatch({ type: "tap", id: d.card });
      return;
    }
    selectForAction(d.card);
    return;
  }
  if (d.zone) return openZone(d.zone, d.player ?? null);
  if (d.playerInfo) return openPlayer(d.playerInfo);
  if (d.life)
    return dispatch({ type: "life", player: d.player, delta: Number(d.life) });

  if (d.control)
    return dispatch(
      { type: "control", id: d.control, player: $("new-controller").value },
      true,
    );
  if (d.attack)
    return dispatch(
      { type: "attack", id: d.attack, player: $("defender").value || null },
      true,
    );
  if (d.move)
    return dispatch({ type: "move", id: d.move, zone: $("move-zone").value });
  if (d.action)
    return dispatch(
      { type: d.action, id: d.id, player: d.player, delta: Number(d.delta) },
      ["tap", "counter"].includes(d.action),
    );
});
function resetFixture(count) {
  pendingSpell = null;
  handOrders.clear();
  separated.clear();
  arrangements.clear();
  slotOrders.clear();
  slotCapacities.clear();
  slotAlignments.clear();
  collapsed.clear();
  expandedZones.clear();
  store.reset(count, dense);
}
$("player-count").addEventListener("change", (event) =>
  resetFixture(Number(event.target.value)),
);
$("perspective").addEventListener("change", (event) =>
  dispatch({ type: "perspective", player: event.target.value }),
);
$("opponent-focus").addEventListener("change", (event) => {
  focusOpponent = event.target.value;
  render();
});
$("help").addEventListener("click", openGuide);
$("history").addEventListener("click", () =>
  showDialog(
    "TABLE ACTIVITY",
    "Recent interactions",
    `<ol class="history-list">${store.state.log
      .slice(-40)
      .toReversed()
      .map((x) => `<li>${esc(x)}</li>`)
      .join("")}</ol>`,
  ),
);
$("draw").addEventListener("click", () => dispatch({ type: "draw" }));
$("inspect-hand").addEventListener("click", () =>
  openZone("hand", store.state.viewer),
);
$("next-turn").addEventListener("click", () =>
  dispatch({
    type: "confirm-step",
    player:
      store.state.declaration === "blockers"
        ? store.state.blockingPlayers[0]
        : store.state.active,
  }),
);
$("reset").addEventListener("click", () =>
  showDialog(
    "SAMPLE TABLE",
    "Reset this table?",
    '<p>Restore sample state and clear arrangements.</p><div class="actions"><button id="confirm-reset" class="primary">Reset sample table</button><button id="cancel-reset">Keep exploring</button></div>',
  ),
);
dialog.addEventListener("click", (event) => {
  if (event.target.id === "confirm-reset") {
    resetFixture(store.state.players.length);
    dialog.close();
  }
  if (event.target.id === "cancel-reset") dialog.close();
});
$("close-dialog").addEventListener("click", () => dialog.close());
store.subscribe(render);
document.addEventListener("pointerover", (event) => previewCard(event.target));
document.addEventListener("focusin", (event) => {
  const card = event.target.closest(".play-area [data-card]");
  if (card && !drag && arrangements.positions.has(card.dataset.card)) {
    arrangements.front(card.dataset.card);
    layoutSurface(card.closest(".play-area"));
  }
  previewCard(event.target);
});
document.addEventListener("pointerout", (event) => {
  if (!event.relatedTarget?.closest(".play-area [data-card]"))
    $("hover-preview").hidden = true;
});
window.addEventListener("blur", () => {
  if (drag) finishDrag(true);
});
$("density").addEventListener("click", () => {
  dense = !dense;
  resetFixture(store.state.players.length);
  announce(
    dense ? "Loaded 24 permanents per player." : "Loaded the compact table.",
  );
});
let handGesture = null;
let handResize = null;
let handHeight = 90;
const handPanel = document.querySelector(".hand-panel");
function setHandHeight(value) {
  handHeight = Math.max(80, Math.min(260, innerHeight * 0.34, value));
  handPanel.style.setProperty("--hand-height", handHeight + "px");
  $("hand-resizer").setAttribute(
    "aria-valuenow",
    String(Math.round(handHeight)),
  );
}
$("toggle-hand").addEventListener("click", () => {
  const collapsed = handPanel.classList.toggle("is-collapsed");
  $("toggle-hand").textContent = collapsed ? "Show hand" : "Hide hand";
  $("toggle-hand").setAttribute("aria-expanded", String(!collapsed));
});
document.addEventListener("pointerdown", (event) => {
  if (event.button !== 0 || dialog.open) return;
  if (event.target.closest("#hand-resizer")) {
    handResize = {
      pointer: event.pointerId,
      y: event.clientY,
      height: handHeight,
    };
    $("hand-resizer").setPointerCapture(event.pointerId);
    event.preventDefault();
    return;
  }
  const card = event.target.closest("#hand > [data-card]");
  if (!card) return;
  handGesture = {
    card,
    id: card.dataset.card,
    pointer: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    moving: false,
    target: null,
  };
  card.setPointerCapture(event.pointerId);
});
document.addEventListener("pointermove", (event) => {
  if (handResize?.pointer === event.pointerId) {
    setHandHeight(handResize.height + handResize.y - event.clientY);
    event.preventDefault();
    return;
  }
  const gesture = handGesture;
  if (!gesture || gesture.pointer !== event.pointerId) return;
  const dx = event.clientX - gesture.x,
    dy = event.clientY - gesture.y;
  if (!gesture.moving && Math.hypot(dx, dy) < 6) return;
  gesture.moving = true;
  event.preventDefault();
  gesture.card.classList.add("is-hand-dragging");
  gesture.card.style.transform = "translate(" + dx + "px," + dy + "px)";
  const cards = [...$("hand").querySelectorAll("[data-card]")];
  let distance = Infinity;
  gesture.target = null;
  const bounds = $("hand").getBoundingClientRect();
  if (event.clientY >= bounds.top - 20 && event.clientY <= bounds.bottom + 20)
    cards.forEach((card, index) => {
      const r = card.getBoundingClientRect();
      if (card === gesture.card) return;
      const d = Math.abs(event.clientX - r.left - r.width / 2);
      if (d < distance) {
        distance = d;
        gesture.target = index;
      }
    });
  cards.forEach((card, index) =>
    card.classList.toggle("hand-drop-target", index === gesture.target),
  );
});
function endHandGesture(cancel = false) {
  if (handResize) {
    if (cancel) setHandHeight(handResize.height);
    const handle = $("hand-resizer");
    if (handle.hasPointerCapture(handResize.pointer))
      handle.releasePointerCapture(handResize.pointer);
    handResize = null;
  }
  const gesture = handGesture;
  handGesture = null;
  if (!gesture) return;
  if (gesture.card.hasPointerCapture(gesture.pointer))
    gesture.card.releasePointerCapture(gesture.pointer);
  if (gesture.moving) {
    if (!cancel && gesture.target !== null)
      handOrders.set(
        store.state.viewer,
        reorderHand(
          handOrders.get(store.state.viewer),
          gesture.id,
          gesture.target,
        ),
      );
    suppressClickUntil = Date.now() + 350;
    renderHandCards();
    announce(cancel ? "Hand move cancelled." : "Hand order updated.");
  }
}
document.addEventListener("pointerup", (event) => {
  if (
    handResize?.pointer === event.pointerId ||
    handGesture?.pointer === event.pointerId
  )
    endHandGesture();
});
document.addEventListener("pointercancel", (event) => {
  if (
    handResize?.pointer === event.pointerId ||
    handGesture?.pointer === event.pointerId
  )
    endHandGesture(true);
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && (handGesture || handResize)) {
    endHandGesture(true);
    event.preventDefault();
  }
  if (
    event.target.id === "hand-resizer" &&
    ["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)
  ) {
    event.preventDefault();
    setHandHeight(
      event.key === "Home"
        ? 80
        : event.key === "End"
          ? 260
          : handHeight + (event.key === "ArrowUp" ? 10 : -10),
    );
  }
  const card = event.target.closest("#hand > [data-card]");
  if (card && event.altKey && ["ArrowLeft", "ArrowRight"].includes(event.key)) {
    event.preventDefault();
    const ids = handOrders.get(store.state.viewer);
    handOrders.set(
      store.state.viewer,
      reorderHand(
        ids,
        card.dataset.card,
        ids.indexOf(card.dataset.card) + (event.key === "ArrowLeft" ? -1 : 1),
      ),
    );
    const id = card.dataset.card;
    renderHandCards();
    $("hand")
      .querySelector('[data-card="' + id + '"]')
      .focus();
    announce("Hand order updated.");
  }
});
window.addEventListener("blur", () => endHandGesture(true));

const cardMenu = document.createElement("div");
cardMenu.id = "card-menu";
cardMenu.role = "menu";
cardMenu.setAttribute("aria-label", "Card options");
cardMenu.hidden = true;
cardMenu.innerHTML =
  '<span id="card-menu-name"></span><button type="button" role="menuitem" id="menu-inspector">Card inspector</button>';
document.body.append(cardMenu);
let menuCard = null;
let menuOrigin = null;
function closeCardMenu(restore = false) {
  cardMenu.hidden = true;
  if (restore && menuOrigin?.isConnected) menuOrigin.focus();
}
function showCardMenu(card, x, y) {
  menuCard = card.dataset.card;
  menuOrigin = card;
  const object = store.state.objects[menuCard];
  if (!object) return;
  (dialog.open ? dialog : document.body).append(cardMenu);
  $("card-menu-name").textContent = object.name;
  cardMenu.hidden = false;
  cardMenu.style.left =
    Math.max(4, Math.min(x, innerWidth - cardMenu.offsetWidth - 6)) + "px";
  cardMenu.style.top =
    Math.max(4, Math.min(y, innerHeight - cardMenu.offsetHeight - 6)) + "px";
  $("hover-preview").hidden = true;
  $("menu-inspector").focus();
}
function selectForAction(id) {
  const s = store.state,
    object = s.objects[id];
  if (!object) return;
  if (pendingSpell) return chooseSpellTarget({ kind: "object", id });
  if (
    object.zone === "hand:" + s.viewer &&
    object.targetKind &&
    s.step !== "cleanup"
  ) {
    const reason = actionPermission(s, "cast", id);
    if (reason) {
      announce(reason);
      return;
    }
    pendingSpell = id;
    render();
    announce("Choose a highlighted target. Escape cancels.");
    return;
  }
  if (s.declaration === "blockers") {
    if (
      object.controller === s.viewer &&
      object.type.toLowerCase().includes("creature") &&
      object.zone === "battlefield"
    ) {
      const reason = blockPermission(s, id, null);
      if (reason) {
        announce(reason);
        return;
      }
      if (selectedBlocker === id) {
        selectedBlocker = null;
        if (s.blocks[id]) dispatch({ type: "block", id, attacker: null });
      } else selectedBlocker = id;
      applyCombatHints();
      announce(
        selectedBlocker
          ? object.name +
              " selected. Click an attacker aimed at you, or click this blocker again to remove it."
          : "Blocker deselected.",
      );
      return;
    }
    if (s.attacks[id]) {
      if (!selectedBlocker) {
        announce("Select your blocking creature first.");
        return;
      }
      dispatch({ type: "block", id: selectedBlocker, attacker: id });
      selectedBlocker = null;
      applyCombatHints();
      return;
    }
  }
  if (object.zone === "hand:" + s.viewer) {
    dispatch({
      type:
        s.step === "cleanup" && s.active === s.viewer
          ? "discard"
          : object.mana
            ? "play-land"
            : "cast",
      id,
    });
    return;
  }
  if (
    object.zone === "battlefield" &&
    object.controller === s.viewer &&
    object.type.toLowerCase().includes("creature")
  ) {
    const defender = $("attack-defender")?.value;
    dispatch({ type: "attack", id, defender: s.attacks[id] ? null : defender });
    return;
  }
  announce("Right-click this card for its inspector.");
}

document.addEventListener("contextmenu", (event) => {
  const card = event.target.closest("[data-card]");
  if (!card) return;
  event.preventDefault();
  showCardMenu(card, event.clientX, event.clientY);
});
$("menu-inspector").addEventListener("click", () => {
  const id = menuCard;
  closeCardMenu();
  openCard(id);
});
document.addEventListener("pointerdown", (event) => {
  if (!cardMenu.contains(event.target)) closeCardMenu();
});
document.addEventListener("keydown", (event) => {
  const card = event.target.closest("[data-card]");
  if (
    card &&
    (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10"))
  ) {
    event.preventDefault();
    const r = card.getBoundingClientRect();
    showCardMenu(card, r.left + 10, r.top + 10);
  }
  if (event.key === "Escape" && !cardMenu.hidden) {
    event.preventDefault();
    event.stopPropagation();
    closeCardMenu(true);
  }
  if (event.key === "Tab") closeCardMenu();
});
window.addEventListener("resize", () => {
  closeCardMenu();
  if (innerHeight >= 400) setHandHeight(handHeight);
});

$("step-details").addEventListener("click", () => {
  const s = store.state;
  showDialog(
    "PROTOTYPE TURN SEQUENCE",
    "Phases and steps",
    '<ol class="step-list">' +
      TURN_STEPS.map(
        (step) =>
          "<li " +
          (step.id === s.step ? 'aria-current="step"' : "") +
          "><strong>" +
          step.phase +
          "</strong> · " +
          step.label +
          (step.id === s.step ? " ← current" : "") +
          "</li>",
      ).join("") +
      '</ol><p>All players passing resolves the top stack item, or advances an empty-stack step. No priority during untap, declaration choices or normal cleanup.</p><p class="dialog-note">Sample spells cost 1 generic mana. Blockers are chosen by clicking a defending creature then an attacker. Damage splits can be reviewed before simultaneous damage and lethal creatures move to graveyards. Arbitrary card effects, prevention, replacement effects and full state-based actions are not implemented.</p>',
  );
});

let combatFrame = null;
function applyCombatHints() {
  const s = store.state;
  for (const card of document.querySelectorAll(".play-area [data-card]")) {
    const id = card.dataset.card;
    card.classList.toggle("block-selected", id === selectedBlocker);
    card.classList.toggle(
      "block-target",
      !!selectedBlocker &&
        s.attacks[id] &&
        !blockPermission(s, selectedBlocker, id),
    );
    if (
      s.declaration === "blockers" &&
      s.objects[id]?.controller === s.viewer &&
      s.objects[id]?.type.toLowerCase().includes("creature")
    )
      card.setAttribute(
        "aria-label",
        "Choose " + s.objects[id].name + " as blocker",
      );
  }
  scheduleCombatLinks();
}
function scheduleCombatLinks() {
  if (combatFrame !== null) return;
  combatFrame = requestAnimationFrame(() => {
    combatFrame = null;
    drawCombatLinks();
  });
}
function drawCombatLinks() {
  const table = $("table"),
    bounds = table.getBoundingClientRect(),
    s = store.state;
  let svg = $("combat-links");
  if (!svg) {
    svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.id = "combat-links";
    svg.setAttribute("aria-hidden", "true");
    table.append(svg);
  }
  svg.setAttribute("viewBox", "0 0 " + bounds.width + " " + bounds.height);
  function cardPoint(id) {
    const card = table.querySelector('.play-area [data-card="' + id + '"]');
    if (!card) return null;
    const r = card.getBoundingClientRect();
    return r.width && r.height
      ? { x: r.x + r.width / 2 - bounds.x, y: r.y + r.height / 2 - bounds.y }
      : null;
  }
  const paths = [];
  for (const [id, defender] of Object.entries(s.attacks)) {
    const from = cardPoint(id),
      seat = table.querySelector('[data-seat="' + defender + '"] .player-name');
    if (!from || !seat) continue;
    const r = seat.getBoundingClientRect();
    const to = {
      x: r.x + r.width / 2 - bounds.x,
      y: r.y + r.height / 2 - bounds.y,
    };
    paths.push(
      '<path class="attack-link" d="M ' +
        from.x +
        " " +
        from.y +
        " Q " +
        from.x +
        " " +
        (from.y + to.y) / 2 +
        " " +
        to.x +
        " " +
        to.y +
        '"/>',
    );
  }
  for (const [blocker, attacker] of Object.entries(s.blocks)) {
    const from = cardPoint(blocker),
      to = cardPoint(attacker);
    if (!from || !to) continue;
    paths.push(
      '<path class="block-link" d="M ' +
        from.x +
        " " +
        from.y +
        " Q " +
        ((from.x + to.x) / 2 + 20) +
        " " +
        (from.y + to.y) / 2 +
        " " +
        to.x +
        " " +
        to.y +
        '"/>',
    );
  }
  svg.innerHTML = paths.join("");
  for (const card of table.querySelectorAll(".play-area [data-card]")) {
    const id = card.dataset.card;
    card.classList.toggle("block-selected", id === selectedBlocker);
    card.classList.toggle(
      "block-target",
      !!selectedBlocker &&
        !!s.attacks[id] &&
        !blockPermission(s, selectedBlocker, id),
    );
  }
}
function openCombatReport() {
  const s = store.state;
  const current = Object.entries(s.attacks)
    .map(
      ([id, defender]) =>
        "<li>A" +
        id.slice(1) +
        " " +
        esc(s.objects[id]?.name) +
        " → " +
        esc(name(defender)) +
        "; blockers: " +
        (Object.entries(s.blocks)
          .filter(([, attacker]) => attacker === id)
          .map(([b]) => "B" + b.slice(1) + " " + esc(s.objects[b]?.name))
          .join(", ") || "none") +
        "</li>",
    )
    .join("");
  const reports = s.combatHistory
    .slice(-4)
    .map(
      (report) =>
        "<h3>Turn " +
        report.turn +
        " · " +
        (report.step === "first-damage"
          ? "First-strike damage"
          : "Combat damage") +
        "</h3><ul>" +
        report.events
          .map(
            (e) =>
              "<li>" +
              esc(e.sourceName) +
              " → " +
              esc(e.targetName) +
              ": " +
              e.amount +
              (e.lifelink ? " · lifelink" : "") +
              "</li>",
          )
          .join("") +
        "</ul><p>" +
        (report.deaths.length
          ? "Died: " +
            report.deaths
              .map(
                (d) =>
                  esc(d.name) +
                  " → " +
                  esc(name(d.owner)) +
                  (d.token ? " · token ceased to exist" : " graveyard"),
              )
              .join("; ")
          : "No creatures died.") +
        "</p>",
    )
    .join("");
  showDialog(
    "COMBAT",
    "Attacks, blocks and damage",
    "<h3>Current combat</h3><ul>" +
      (current || "<li>No attackers.</li>") +
      "</ul>" +
      reports +
      '<p class="dialog-note">Sample creatures and common combat keywords are supported. Arbitrary card effects, prevention/replacement effects and player elimination are not implemented.</p>',
  );
}
function openDamageAssignments() {
  const s = store.state;
  showDialog(
    "COMBAT DAMAGE",
    "Review damage split",
    s.damageAssignments
      .map(
        (plan) =>
          '<section class="damage-plan"><h3>' +
          esc(s.objects[plan.source]?.name) +
          " · " +
          plan.power +
          " damage</h3>" +
          (!plan.targets.length
            ? "<p>No legal damage recipients.</p>"
            : plan.targets
                .map(
                  (target, i) =>
                    "<label>" +
                    esc(
                      target.kind === "player"
                        ? name(target.target)
                        : s.objects[target.target]?.name,
                    ) +
                    '<input type="number" min="0" max="' +
                    plan.power +
                    '" step="1" value="' +
                    target.amount +
                    '" data-damage-source="' +
                    plan.source +
                    '" data-target-index="' +
                    i +
                    '" ' +
                    (plan.targets.length === 1 ? "readonly" : "") +
                    "></label>",
                )
                .join("")) +
          "</section>",
      )
      .join("") +
      '<p>Each source must assign its full power among its listed recipients. Trample requires lethal damage to each blocker before assigning any to the player.</p><button class="primary" data-save-damage="true">Save damage split</button><p class="dialog-note">Damage is simultaneous. Saving does not deal it; use Deal damage after reviewing the assignments.</p>',
  );
}
function saveDamageAssignments() {
  try {
    const updates = store.state.damageAssignments.map((plan) => ({
      id: plan.source,
      player: store.state.objects[plan.source].controller,
      amounts: plan.targets.map((_, i) =>
        Number(
          dialog.querySelector(
            '[data-damage-source="' +
              plan.source +
              '"][data-target-index="' +
              i +
              '"]',
          ).value,
        ),
      ),
    }));
    if (
      updates.some((u) => u.amounts.some((n) => !Number.isInteger(n) || n < 0))
    )
      throw new Error("Use nonnegative whole numbers.");
    const candidate = structuredClone(store.state);
    for (const update of updates)
      candidate.damageAssignments
        .find((plan) => plan.source === update.id)
        .targets.forEach(
          (target, index) => (target.amount = update.amounts[index]),
        );
    validateDamage(candidate);
    for (const update of updates)
      store.dispatch({ type: "assign-damage", ...update });
    dialog.close();
    announce("Damage split saved. Confirm Deal damage when ready.");
  } catch (error) {
    announce(error.message);
  }
}
document.addEventListener("pointermove", () => {
  if (drag?.moving) scheduleCombatLinks();
});

render();

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && pendingSpell) {
    pendingSpell = null;
    render();
    announce("Targeting canceled; no mana spent.");
  }
});
$("spell-samples").addEventListener("click", () =>
  dispatch({ type: "sample-spells" }),
);
