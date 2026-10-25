import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createTable,
  contents,
  visibleContents,
  reduce,
  zoneKey,
  seats,
  assertState,
  TableStore,
} from "./model.js";

function atStep(state, step) {
  for (let i = 0; state.step !== step && i < 100; i++)
    state = reduce(
      state,
      state.priority
        ? { type: "pass", player: state.priority }
        : {
            type: "confirm-step",
            player:
              state.declaration === "blockers"
                ? state.blockingPlayers[0]
                : state.active,
          },
    );
  assert.equal(state.step, step);
  return state;
}
function passRound(state) {
  const count = state.players.filter((p) => !p.left).length;
  for (let i = 0; i < count; i++)
    state = reduce(state, { type: "pass", player: state.priority });
  return state;
}

for (const count of [2, 3, 4])
  test(`${count} players have the correct zone topology`, () => {
    const s = createTable(count);
    assert.equal(Object.keys(s.zones).length, 3 * count + 4);
    assert.equal(contents(s, "command").length, count);
    assert.equal(contents(s, "stack").length, 0);
    assertState(s);
  });
test("invalid topology is refused", () => {
  assert.throws(() => createTable(5));
  assert.throws(() => zoneKey("hand"));
  assert.throws(() => zoneKey("battlefield", "p0"));
});
test("private projections contain no card identities", () => {
  const s = createTable();
  assert.deepEqual(visibleContents(s, "hand", "p1"), {
    count: 4,
    cards: [],
    hidden: true,
  });
  assert.equal(visibleContents(s, "hand", "p0").cards.length, 4);
  assert.deepEqual(visibleContents(s, "library", "p0"), {
    count: 12,
    cards: [],
    hidden: true,
  });
});
test("perspective rotates display without changing seats or zone contents", () => {
  const s = createTable();
  const next = reduce(s, { type: "perspective", player: "p2" });
  assert.deepEqual(
    seats(next).map((p) => p.id),
    ["p2", "p3", "p0", "p1"],
  );
  assert.deepEqual(next.players, s.players);
  assert.deepEqual(next.zones, s.zones);
});
test("control change retains object, ownership, and shared battlefield; death uses owner graveyard", () => {
  const s = createTable();
  const o = contents(s, "battlefield").find((o) => o.owner === "p0");
  const controlled = reduce(s, { type: "control", id: o.id, player: "p1" });
  assert.equal(controlled.objects[o.id].controller, "p1");
  assert.equal(controlled.objects[o.id].owner, "p0");
  assert.equal(controlled.objects[o.id].zone, "battlefield");
  const dead = reduce(controlled, {
    type: "move",
    id: o.id,
    zone: "graveyard",
  });
  assert.equal(dead.objects[o.id], undefined);
  const newObject = contents(dead, "graveyard", "p0").find(
    (x) => x.card === o.card,
  );
  assert.ok(newObject);
  assert.notEqual(newObject.id, o.id);
  assert.equal(
    contents(dead, "graveyard", "p1").some((x) => x.card === o.card),
    false,
  );
});
test("zone changes reset permanent state and preserve card identity", () => {
  let s = atStep(createTable(), "main1");
  const o = contents(s, "battlefield").find((o) => o.mana);
  s = reduce(s, { type: "tap", id: o.id });
  s = reduce(s, { type: "counter", id: o.id, delta: 1 });
  s = reduce(s, { type: "move", id: o.id, zone: "hand" });
  const moved = contents(s, "hand", o.owner).find((x) => x.card === o.card);
  assert.equal(moved.tapped, false);
  assert.equal(moved.counters, 0);
});
test("draw moves exact top card while retaining other library order", () => {
  const s = createTable();
  const before = contents(s, "library", "p0");
  const next = reduce(s, { type: "draw" });
  assert.deepEqual(
    contents(next, "library", "p0").map((o) => o.id),
    before.slice(1).map((o) => o.id),
  );
  assert.equal(contents(next, "hand", "p0").at(-1).card, before[0].card);
  assert.equal(contents(s, "hand", "p0").length, 4);
});
test("all-player passing resolves only the top entry", () => {
  let s = atStep(createTable(), "main1");
  s = reduce(s, {
    type: "move",
    id: contents(s, "graveyard", "p0")[0].id,
    zone: "stack",
  });
  const hand = contents(s, "hand", "p0")[0];
  s = reduce(s, { type: "move", id: hand.id, zone: "stack" });
  const bottom = contents(s, "stack")[0].id;
  s = passRound(s);
  assert.equal(contents(s, "stack").length, 1);
  assert.equal(contents(s, "stack")[0].id, bottom);
  assert.equal(contents(s, "graveyard", "p0").at(-1).card, hand.card);
});
test("attacker relationship survives perspective change; control change clears it", () => {
  let s = atStep(createTable(), "attackers");
  const id = contents(s, "battlefield")[0].id;
  s = reduce(s, { type: "attack", id, defender: "p3" });
  s = reduce(s, { type: "perspective", player: "p2" });
  assert.equal(s.attacks[id], "p3");
  s = reduce(s, { type: "control", id, player: "p1" });
  assert.equal(s.attacks[id], undefined);
});
test("permanent resolving retains its controller when viewed from another seat", () => {
  let s = atStep(createTable(), "main1");
  const creature = contents(s, "hand", "p0").find((o) =>
    o.type.startsWith("Creature"),
  );
  s = reduce(s, { type: "move", id: creature.id, zone: "stack" });
  s = reduce(s, { type: "perspective", player: "p2" });
  s = passRound(s);
  assert.equal(
    contents(s, "battlefield").find((o) => o.card === creature.card).controller,
    "p0",
  );
});
test("token leaving battlefield ceases to exist", () => {
  let s = reduce(createTable(), { type: "token" });
  const token = contents(s, "battlefield").at(-1);
  s = reduce(s, { type: "move", id: token.id, zone: "graveyard" });
  assert.equal(
    Object.values(s.objects).some((x) => x.card === token.card),
    false,
  );
});
test("priority cycles all four seats and advances an empty-stack step", () => {
  let s = atStep(createTable(), "upkeep");
  for (const next of ["p1", "p2", "p3", "p0"]) {
    s = reduce(s, { type: "pass", player: s.priority });
    assert.equal(s.priority, next);
  }
  assert.equal(s.passes.length, 0);
  assert.equal(contents(s, "stack").length, 0);
  assert.equal(s.step, "draw");
});
test("store notifies only after valid transitions", () => {
  const store = new TableStore(createTable());
  let notifications = 0;
  store.subscribe(() => notifications++);
  assert.throws(() =>
    store.dispatch({ type: "move", id: "missing", zone: "hand" }),
  );
  assert.equal(notifications, 0);
  store.dispatch({ type: "draw" });
  assert.equal(notifications, 1);
});
for (const count of [2, 3, 4])
  test(`${count}-player busy fixture retains all zone invariants`, () => {
    const state = createTable(count, true);
    for (const seat of state.players)
      assert.equal(
        contents(state, "battlefield").filter((o) => o.controller === seat.id)
          .length,
        24,
      );
    assertState(state);
  });

// Layout checks assert geometry rather than DOM implementation details.
const { arrangeBattlefield } = await import("./battlefield-layout.js");
for (const [front, mana, width, height] of [
  [2, 3, 250, 150],
  [10, 14, 190, 150],
  [80, 120, 400, 180],
]) {
  test(`continuous layout fits ${front + mana} cards into ${width} by ${height}`, () => {
    const boxes = arrangeBattlefield(front, mana, width, height);
    assert.equal(boxes.length, front + mana);
    for (const b of boxes) {
      assert.ok(b.x >= 0 && b.y >= 0 && b.width > 0 && b.height > 0);
      assert.ok(b.x + b.width <= width + 0.001);
      assert.ok(b.y + b.height <= height + 0.001);
    }
  });
}
test("empty battlefield produces no positions", () =>
  assert.deepEqual(arrangeBattlefield(0, 0, 250, 150), []));

const { Arrangements } = await import("./arrangements.js");
test("authored layout survives captures and moves without changing model state", () => {
  const board = new Arrangements();
  const state = createTable();
  const before = structuredClone(state);
  board.capture("p0", [
    { id: "o1", x: 0.2, y: 0.3 },
    { id: "o2", x: 0.5, y: 0.5 },
  ]);
  board.move(["o1"], 0.1, 0.1);
  board.capture("p0", [{ id: "o1", x: 0, y: 0 }]);
  assert.ok(Math.abs(board.positions.get("o1").x - 0.3) < 0.00001);
  assert.deepEqual(state, before);
});
test("group movement clamps at edges without distorting relative offsets", () => {
  const board = new Arrangements();
  board.capture("p0", [
    { id: "a", x: 0.2, y: 0.3 },
    { id: "b", x: 0.4, y: 0.5 },
  ]);
  board.group("p0", ["a", "b"], "lands");
  board.move(["a", "b"], 2, -2);
  assert.equal(board.positions.get("b").x, 1);
  assert.equal(board.positions.get("a").y, 0);
  assert.ok(
    Math.abs(board.positions.get("b").x - board.positions.get("a").x - 0.2) <
      0.00001,
  );
  assert.ok(
    Math.abs(board.positions.get("b").y - board.positions.get("a").y - 0.2) <
      0.00001,
  );
});
test("group membership retains individual identities and separating one preserves others", () => {
  const board = new Arrangements();
  board.capture(
    "p0",
    ["a", "b", "c"].map((id) => ({ id, x: 0.2, y: 0.2 })),
  );
  const key = board.group("p0", ["a", "b", "b", "c"], "tokens");
  assert.deepEqual(board.groups.get(key).ids, ["a", "b", "c"]);
  board.removeFromGroup("b");
  assert.deepEqual(board.groups.get(key).ids, ["a", "c"]);
  assert.ok(board.positions.has("b"));
});
test("zone and controller changes remove stale arrangements and dissolve invalid groups", () => {
  const board = new Arrangements();
  board.capture("p0", [
    { id: "a", x: 0.1, y: 0.1 },
    { id: "b", x: 0.2, y: 0.2 },
  ]);
  board.group("p0", ["a", "b"], "lands");
  board.prune({
    a: { zone: "battlefield", controller: "p1" },
    b: { zone: "battlefield", controller: "p0" },
  });
  assert.equal(board.positions.has("a"), false);
  assert.equal(board.positions.has("b"), true);
  assert.equal(board.groups.size, 0);
});
test("cancelling movement restores exact authored positions and ordering", () => {
  const board = new Arrangements();
  board.capture("p0", [{ id: "a", x: 0.1, y: 0.2 }]);
  const snapshot = board.snapshot();
  board.move(["a"], 0.5, 0.5);
  board.restore(snapshot);
  assert.deepEqual([...board.positions], snapshot);
});
test("resetting one controller preserves other players arrangements", () => {
  const board = new Arrangements();
  board.capture("p0", [{ id: "a", x: 0.1, y: 0.2 }]);
  board.capture("p1", [{ id: "b", x: 0.3, y: 0.4 }]);
  board.resetController("p0");
  assert.equal(board.positions.has("a"), false);
  assert.equal(board.positions.has("b"), true);
});
const { fanGroup } = await import("./arrangements.js");
for (const width of [192, 1100])
  test(`group stays overlapped and contained at ${width}px`, () => {
    const fan = fanGroup(5, 40, 15, width, 230, 70, 98);
    assert.equal(fan.length, 5);
    for (const p of fan) {
      assert.ok(p.x >= 0 && p.x + 70 <= width);
      assert.ok(p.y >= 0 && p.y + 98 <= 230);
    }
    assert.ok(fan[1].x - fan[0].x <= 18);
  });
const { cardArea, reconcileSlots, moveSlot, areaSlots, slotAt } =
  await import("./battlefield-layout.js");
test("cards are assigned to their permitted battlefield areas", () => {
  assert.equal(cardArea({ mana: "G", type: "Land creature" }), "mana");
  assert.equal(cardArea({ type: "Token creature" }), "creatures");
  assert.equal(cardArea({ type: "Artifact" }), "support");
});
test("slot movement swaps occupied slots and preserves empty spots", () => {
  assert.deepEqual(moveSlot(["a", "b", null, null], "a", 1), [
    "b",
    "a",
    null,
    null,
  ]);
  assert.deepEqual(moveSlot(["a", null, null, null], "a", 1), [
    null,
    "a",
    null,
    null,
  ]);
});
test("new objects fill gaps without moving existing arranged objects", () => {
  assert.deepEqual(reconcileSlots([null, "a", null, "b"], ["a", "b", "c"]), [
    "c",
    "a",
    null,
    "b",
  ]);
});
test("drops outside the permitted area are rejected", () => {
  const area = areaSlots("creatures", 4, 600, 240);
  assert.equal(slotAt(area, 100, 230), null);
  assert.equal(slotAt(area, -1, 20), null);
  assert.equal(slotAt(area, area.slots[1].x + 5, area.slots[1].y + 5), 1);
});
test("slot geometry stays fixed when occupancy changes within capacity", () => {
  const empty = areaSlots("creatures", 0, 1000, 300);
  assert.ok(empty.slots.length > 4);
  const occupied = areaSlots("creatures", empty.slots.length, 1000, 300);
  assert.deepEqual(empty.slots, occupied.slots);
});
test("initial slots center the cards in the available board width", () => {
  const order = reconcileSlots([], ["a"], 14);
  assert.equal(order.length, 14);
  assert.equal(order.indexOf("a"), 6);
  const added = reconcileSlots(order, ["a", "b"], 14);
  assert.equal(added.indexOf("a"), 6);
  assert.equal(added.indexOf("b"), 7);
});
const { stackSignature } = await import("./arrangements.js");
test("automatic stacking compares visible state while preserving individual identities", () => {
  const a = {
    id: "a",
    controller: "p0",
    name: "Forest",
    type: "Land",
    mana: "G",
    tapped: false,
    counters: 0,
  };
  assert.equal(stackSignature(a), stackSignature({ ...a, id: "b" }));
  assert.notEqual(stackSignature(a), stackSignature({ ...a, tapped: true }));
  assert.notEqual(stackSignature(a), stackSignature({ ...a, counters: 1 }));
  assert.notEqual(
    stackSignature(a),
    stackSignature({ ...a, controller: "p1" }),
  );
});
const { dropStackKind } = await import("./arrangements.js");
test("dropping one land onto another stacks even when names differ", () => {
  const a = { id: "a", controller: "p0", mana: "G", name: "Forest" },
    b = { id: "b", controller: "p0", mana: "U", name: "Island" };
  assert.equal(dropStackKind(a, b), "lands");
  assert.equal(dropStackKind(a, { ...b, controller: "p1" }), null);
});
test("matching tokens stack on drop and other same-name cards obey the setting", () => {
  const a = { id: "a", controller: "p0", token: true, name: "Saproling" },
    b = { ...a, id: "b" };
  assert.equal(dropStackKind(a, b), "tokens");
  const c = { id: "c", controller: "p0", name: "Relic", type: "Artifact" },
    d = { ...c, id: "d" };
  assert.equal(dropStackKind(c, d), null);
  assert.equal(dropStackKind(c, d, { matching: true }), "matching");
});

test("battlefield card size does not shrink with crowding or viewport height", () => {
  for (const count of [1, 8, 30]) {
    const area = areaSlots("mana", count, 900, 240, 110);
    assert.ok(
      area.slots.every((slot) => slot.width === 110 && slot.height === 154),
    );
  }
});
test("oversized piles split into groups of at most three without losing identities", () => {
  const layout = new Arrangements();
  const ids = Array.from({ length: 8 }, (_, i) => "land" + i);
  layout.capture(
    "p0",
    ids.map((id) => ({ id, x: 0, y: 0 })),
  );
  layout.group("p0", ids, "lands");
  const groups = [...layout.groups.values()];
  assert.deepEqual(
    groups.map((g) => g.ids.length),
    [3, 3, 2],
  );
  assert.deepEqual(
    groups.flatMap((g) => g.ids),
    ids,
  );
});

const { resizeSlots } = await import("./battlefield-layout.js");
test("resizing recenters slots instead of retaining stale columns", () => {
  const previous = [null, null, "a", null, null];
  assert.deepEqual(resizeSlots(previous, ["a"], 5, 3), [null, "a", null]);
  assert.deepEqual(resizeSlots([null, "a", null], ["a"], 3, 7), [
    null,
    null,
    null,
    "a",
    null,
    null,
    null,
  ]);
});
test("opponents mirror battlefield rows with lands above creatures", () => {
  const mana = areaSlots("mana", 1, 600, 570, 110, true);
  const creatures = areaSlots("creatures", 1, 600, 570, 110, true);
  assert.ok(mana.top < creatures.top);
  assert.equal(mana.slots[0].width, 110);
});
test("card columns have compact fixed spacing and a centered middle slot", () => {
  const area = areaSlots("creatures", 1, 1400, 570, 110);
  assert.equal(area.slots[1].x - area.slots[0].x, 138);
  const middle = area.slots[Math.floor(area.slots.length / 2)];
  assert.equal(middle.x + middle.width / 2, 700);
});

const { centerSlotOffset } = await import("./battlefield-layout.js");
test("even and odd card counts center as a group across board widths", () => {
  for (const width of [800, 1200, 1600])
    for (const count of [1, 2, 3, 4]) {
      const area = areaSlots("creatures", 0, width, 1140, 110);
      const keys = Array.from({ length: count }, (_, i) => "c" + i);
      const order = reconcileSlots([], keys, area.slots.length);
      const geometry = areaSlots("creatures", order.length, width, 1140, 110);
      const offset = centerSlotOffset(order, geometry.slots, width);
      const occupied = order.flatMap((id, i) =>
        id ? [geometry.slots[i]] : [],
      );
      const midpoint =
        (Math.min(...occupied.map((s) => s.x)) +
          Math.max(...occupied.map((s) => s.x + s.width))) /
          2 +
        offset;
      assert.equal(midpoint, width / 2);
    }
});

const { screenCardWidth, screenAreaSlots } =
  await import("./battlefield-layout.js");
test("screen-fit geometry keeps player cards inside both battlefield rows", () => {
  for (const height of [200, 260, 380]) {
    const size = screenCardWidth(1000, height);
    for (const area of ["creatures", "support", "mana"]) {
      const geometry = screenAreaSlots(area, 3, 1000, height, size);
      for (const slot of geometry.slots) {
        assert.ok(slot.y >= 0 && slot.y + slot.height <= height);
        assert.ok(
          slot.x >= geometry.left &&
            slot.x + slot.width <= geometry.left + geometry.span + 0.01,
        );
      }
    }
  }
});
test("opponent thumbnails grow with available panel width and stay smaller", () => {
  const small = screenCardWidth(260, 122, true);
  const expanded = screenCardWidth(540, 170, true);
  const own = screenCardWidth(1000, 258);
  assert.ok(expanded > small);
  assert.ok(expanded < own);
  for (const area of ["creatures", "support", "mana"]) {
    const geometry = screenAreaSlots(area, 1, 260, 122, small, true);
    assert.ok(geometry.slots[0].y + geometry.slots[0].height <= 122);
  }
});
test("screen-fit drops respect side-by-side battlefield areas", () => {
  const creatures = screenAreaSlots("creatures", 2, 1000, 260, 76);
  assert.equal(slotAt(creatures, 950, 40), null);
  assert.notEqual(slotAt(creatures, 300, 40), null);
});

test("screen-fit opponents keep lands above the creature row facing the player", () => {
  const size = screenCardWidth(280, 190, true);
  const mana = screenAreaSlots("mana", 3, 280, 190, size, true);
  const creatures = screenAreaSlots("creatures", 3, 280, 190, size, true);
  assert.ok(mana.slots[0].y + mana.slots[0].height <= creatures.top);
  assert.ok(creatures.slots[0].y > mana.slots[0].y);
  assert.equal(mana.span, 280);
  for (const region of [mana, creatures])
    for (const slot of region.slots) assert.ok(slot.y + slot.height <= 190);
});

const { reorderHand } = await import("./battlefield-layout.js");
test("hand reorder preserves identities, bounds, and the source order", () => {
  const ids = ["a", "b", "c", "d"];
  assert.deepEqual(reorderHand(ids, "a", 3), ["b", "c", "d", "a"]);
  assert.deepEqual(reorderHand(ids, "d", -1), ["d", "a", "b", "c"]);
  assert.deepEqual(reorderHand(ids, "missing", 1), ids);
  assert.deepEqual(ids, ["a", "b", "c", "d"]);
});
test("land taps require its controller priority and cannot undo mana by untapping", () => {
  let state = atStep(createTable(2), "upkeep");
  state = reduce(state, { type: "control", id: "o3", player: "p1" });
  assert.throws(() => reduce(state, { type: "tap", id: "o3" }), /control/);
  state = reduce(state, { type: "pass", player: "p0" });
  state = reduce(state, { type: "tap", id: "o3", player: "p1" });
  assert.equal(state.players[1].manaByColor.G, 1);
  assert.throws(
    () => reduce(state, { type: "tap", id: "o3", player: "p1" }),
    /tapped/,
  );
  assert.deepEqual(state.players[0].manaByColor, {});
});

test("untap grants no priority; upkeep opens the first action window", () => {
  let s = createTable(2);
  assert.equal(s.priority, null);
  assert.throws(() => reduce(s, { type: "tap", id: "o3" }), /priority/);
  assert.throws(() => reduce(s, { type: "cast", id: "o6" }), /priority/);
  s = reduce(s, { type: "confirm-step" });
  assert.equal(s.step, "upkeep");
  assert.equal(s.priority, "p0");
});
test("instant casting consumes sample mana and retains priority until all pass", () => {
  let s = atStep(createTable(3), "upkeep");
  assert.throws(() => reduce(s, { type: "cast", id: "o7" }), /main phase/);
  assert.throws(() => reduce(s, { type: "cast", id: "o6" }), /mana/);
  s = reduce(s, { type: "tap", id: "o3" });
  s = reduce(s, { type: "cast", id: "o6" });
  assert.equal(s.players[0].manaByColor.G, 0);
  assert.equal(s.priority, "p0");
  assert.equal(s.zones.stack.length, 1);
  s = passRound(s);
  assert.equal(s.step, "upkeep");
  assert.equal(s.priority, "p0");
  assert.equal(s.zones.stack.length, 0);
  assert.equal(contents(s, "graveyard", "p0").at(-1).card, "card-o6");
});
test("main-phase land plays are once per turn and do not use the stack", () => {
  let s = atStep(createTable(3), "main1");
  s = reduce(s, { type: "play-land", id: "o8" });
  assert.equal(s.zones.stack.length, 0);
  assert.equal(s.landsPlayed.p0, 1);
  assert.equal(s.priority, "p0");
  const other = contents(s, "hand", "p0").find((o) => o.mana);
  if (other)
    assert.throws(
      () => reduce(s, { type: "play-land", id: other.id }),
      /already/,
    );
  const extra = contents(s, "battlefield").find(
    (o) => o.mana && o.owner === "p0",
  );
  s = reduce(s, { type: "move", id: extra.id, zone: "hand" });
  assert.throws(
    () =>
      reduce(s, {
        type: "play-land",
        id: contents(s, "hand", "p0").find((o) => o.card === extra.card).id,
      }),
    /already/,
  );
});
test("off-turn priority permits instants but not creatures; actions reset passing", () => {
  let s = atStep(createTable(3), "upkeep");
  s = reduce(s, { type: "pass", player: "p0" });
  s = reduce(s, { type: "perspective", player: "p1" });
  const land = contents(s, "battlefield").find(
    (o) => o.controller === "p1" && o.mana,
  );
  s = reduce(s, { type: "tap", id: land.id });
  assert.equal(s.passes.length, 0);
  const hand = contents(s, "hand", "p1");
  assert.throws(
    () =>
      reduce(s, {
        type: "cast",
        id: hand.find((o) => o.type.includes("Creature")).id,
      }),
    /main phase/,
  );
  s = reduce(s, {
    type: "cast",
    id: hand.find((o) => o.type.includes("Instant")).id,
  });
  assert.equal(s.priority, "p1");
  s = passRound(s);
  assert.equal(s.priority, "p0");
});
test("creatures cast this turn cannot attack; declarations close before priority", () => {
  let s = atStep(createTable(2), "main1");
  s = reduce(s, { type: "tap", id: "o3" });
  s = reduce(s, { type: "cast", id: "o7" });
  s = passRound(s);
  const newCreature = contents(s, "battlefield").find(
    (o) => o.card === "card-o7",
  );
  s = atStep(s, "attackers");
  assert.equal(s.priority, null);
  assert.throws(
    () => reduce(s, { type: "attack", id: newCreature.id, defender: "p1" }),
    /since/,
  );
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  assert.equal(s.objects.o1.tapped, false);
  s = reduce(s, { type: "confirm-step", player: "p0" });
  assert.equal(s.objects.o1.tapped, true);
  assert.equal(s.priority, "p0");
  assert.throws(
    () => reduce(s, { type: "attack", id: "o1", defender: null }),
    /declare attackers/,
  );
});
test("no attackers skips blockers and damage; no-block choice precedes priority", () => {
  let s = atStep(createTable(2), "attackers");
  s = reduce(s, { type: "confirm-step", player: "p0" });
  s = passRound(s);
  assert.equal(s.step, "combat-end");
  let combat = atStep(createTable(2), "attackers");
  combat = reduce(combat, { type: "attack", id: "o1", defender: "p1" });
  combat = reduce(combat, { type: "confirm-step", player: "p0" });
  combat = passRound(combat);
  assert.equal(combat.step, "blockers");
  assert.equal(combat.priority, null);
  assert.throws(
    () => reduce(combat, { type: "pass", player: "p0" }),
    /priority/,
  );
  combat = reduce(combat, { type: "confirm-step", player: "p1" });
  assert.equal(combat.priority, "p0");
  combat = passRound(combat);
  assert.equal(combat.step, "damage");
});
test("mana empties at step changes, and lands wait for their controller untap", () => {
  let s = atStep(createTable(2), "upkeep");
  s = reduce(s, { type: "tap", id: "o3" });
  s = passRound(s);
  assert.deepEqual(s.players[0].manaByColor, {});
  assert.equal(s.objects.o3.tapped, true);
  s = atStep(s, "cleanup");
  s = reduce(s, { type: "confirm-step" });
  assert.equal(s.active, "p1");
  assert.equal(s.objects.o3.tapped, true);
  s = atStep(s, "cleanup");
  s = reduce(s, { type: "confirm-step", player: "p1" });
  assert.equal(s.active, "p0");
  assert.equal(s.objects.o3.tapped, false);
});
test("first draw is skipped in two-player games but kept in multiplayer", () => {
  assert.equal(
    contents(atStep(createTable(2), "draw"), "hand", "p0").length,
    4,
  );
  assert.equal(
    contents(atStep(createTable(3), "draw"), "hand", "p0").length,
    5,
  );
});
test("cleanup requires discarding to seven and rolls into the next player untap", () => {
  let s = createTable(2);
  for (let i = 0; i < 5; i++) s = reduce(s, { type: "draw" });
  s = atStep(s, "cleanup");
  assert.equal(s.priority, null);
  assert.throws(() => reduce(s, { type: "confirm-step" }), /Discard/);
  while (contents(s, "hand", "p0").length > 7)
    s = reduce(s, { type: "discard", id: contents(s, "hand", "p0")[0].id });
  s = reduce(s, { type: "confirm-step" });
  assert.equal(s.active, "p1");
  assert.equal(s.step, "untap");
  assert.equal(s.turn, 2);
});
test("first-strike damage timing is included only when an attacker needs it", () => {
  let s = atStep(createTable(2), "attackers");
  s.objects.o1.rules = "First strike.";
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  s = reduce(s, { type: "confirm-step", player: "p0" });
  s = passRound(s);
  s = reduce(s, { type: "confirm-step", player: "p1" });
  s = passRound(s);
  assert.equal(s.step, "first-damage");
  s = reduce(s, { type: "confirm-step", player: s.active });
  s = passRound(s);
  assert.equal(s.step, "damage");
});

test("new tokens obey summoning sickness, while vigilance avoids attack tapping", () => {
  let s = atStep(createTable(2), "main1");
  s = reduce(s, { type: "token" });
  const token = contents(s, "battlefield").at(-1).id;
  s = atStep(s, "attackers");
  assert.throws(
    () => reduce(s, { type: "attack", id: token, defender: "p1" }),
    /since/,
  );
  s.objects.o1.rules = "Vigilance.";
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  s = reduce(s, { type: "confirm-step", player: "p0" });
  assert.equal(s.objects.o1.tapped, false);
});

function fight({
  power = "2 / 2",
  rules = "",
  blockers = [{ stats: "2 / 2", rules: "" }],
  count = 2,
} = {}) {
  let s = atStep(createTable(count), "attackers");
  s.objects.o1.stats = power;
  s.objects.o1.rules = rules;
  const ids = [];
  if (blockers.length) {
    ids.push(
      contents(s, "battlefield").find(
        (o) =>
          o.controller === "p1" && o.type.toLowerCase().includes("creature"),
      ).id,
    );
    for (let i = 1; i < blockers.length; i++) {
      s = reduce(s, { type: "perspective", player: "p1" });
      s = reduce(s, { type: "token" });
      ids.push(contents(s, "battlefield").at(-1).id);
    }
    blockers.forEach((data, i) => {
      s.objects[ids[i]].stats = data.stats;
      s.objects[ids[i]].rules = data.rules ?? "";
    });
  }
  s = reduce(s, { type: "perspective", player: "p0" });
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  s = reduce(s, { type: "confirm-step", player: "p0" });
  s = passRound(s);
  for (const id of ids)
    s = reduce(s, { type: "block", id, attacker: "o1", player: "p1" });
  s = reduce(s, { type: "confirm-step", player: "p1" });
  s = passRound(s);
  return { s, ids };
}
const deal = (s) => reduce(s, { type: "confirm-step", player: s.active });
test("blocked creatures exchange damage simultaneously and both die to owner graveyards", () => {
  let { s, ids } = fight();
  s = deal(s);
  assert.equal(s.objects.o1, undefined);
  assert.equal(s.objects[ids[0]], undefined);
  assert.ok(contents(s, "graveyard", "p0").some((o) => o.card === "card-o1"));
  assert.ok(
    contents(s, "graveyard", "p1").some((o) => o.card === "card-" + ids[0]),
  );
  assert.equal(s.players[1].life, 40);
  assert.equal(s.combatHistory.at(-1).events.length, 2);
  assert.equal(s.combatHistory.at(-1).deaths.length, 2);
});
test("unblocked attacks damage the defending player", () => {
  let { s } = fight({ power: "4 / 4", blockers: [] });
  s = deal(s);
  assert.equal(s.players[1].life, 36);
  assert.equal(s.objects.o1.damage, 0);
});
test("blockers must be untapped and can block only attackers aimed at their controller", () => {
  let s = atStep(createTable(3), "attackers");
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  s = reduce(s, { type: "confirm-step", player: "p0" });
  s = passRound(s);
  const b = contents(s, "battlefield").find(
    (o) => o.controller === "p1" && o.stats,
  ).id;
  s.objects[b].tapped = true;
  assert.throws(
    () => reduce(s, { type: "block", id: b, attacker: "o1", player: "p1" }),
    /tapped/,
  );
  s.objects[b].tapped = false;
  const other = contents(s, "battlefield").find(
    (o) => o.controller === "p2" && o.stats,
  ).id;
  assert.throws(
    () => reduce(s, { type: "block", id: other, attacker: "o1", player: "p1" }),
    /control/,
  );
  s = reduce(s, { type: "block", id: b, attacker: "o1", player: "p1" });
  assert.equal(s.objects[b].tapped, false);
  s = reduce(s, { type: "block", id: b, attacker: null, player: "p1" });
  assert.equal(s.blocks[b], undefined);
});
test("flying needs flying or reach, and menace needs two blockers on confirmation", () => {
  assert.throws(
    () =>
      fight({ rules: "Flying.", blockers: [{ stats: "2 / 2", rules: "" }] }),
    /flying or reach/,
  );
  assert.doesNotThrow(() =>
    fight({
      rules: "Flying.",
      blockers: [{ stats: "2 / 2", rules: "Reach." }],
    }),
  );
  assert.throws(() => fight({ rules: "Menace." }), /two blockers/);
  assert.doesNotThrow(() =>
    fight({
      rules: "Menace.",
      blockers: [{ stats: "1 / 1" }, { stats: "1 / 1" }],
    }),
  );
});
test("multiple-blocker damage can be divided freely rather than enforcing a damage order", () => {
  let { s, ids } = fight({
    power: "4 / 5",
    blockers: [{ stats: "2 / 3" }, { stats: "1 / 1" }],
  });
  const source = s.damageAssignments.find((p) => p.source === "o1");
  assert.equal(source.targets.length, 2);
  s = reduce(s, {
    type: "assign-damage",
    id: "o1",
    player: "p0",
    amounts: [2, 2],
  });
  s = deal(s);
  assert.equal(s.objects[ids[0]].damage, 2);
  assert.equal(s.objects[ids[1]], undefined);
  assert.equal(s.objects.o1.damage, 3);
  assert.equal(s.players[1].life, 40);
});
test("invalid damage totals are rejected without partially dealing damage", () => {
  let { s } = fight({
    power: "4 / 5",
    blockers: [{ stats: "2 / 3" }, { stats: "1 / 1" }],
  });
  s = reduce(s, {
    type: "assign-damage",
    id: "o1",
    player: "p0",
    amounts: [1, 1],
  });
  assert.throws(() => deal(s), /exactly 4/);
  assert.equal(s.objects.o1.damage, 0);
  assert.equal(s.combatHistory.length, 0);
});
test("first-strike blockers deal damage first and a killed attacker cannot hit back", () => {
  let { s, ids } = fight({
    power: "2 / 2",
    blockers: [{ stats: "2 / 2", rules: "First strike." }],
  });
  assert.equal(s.step, "first-damage");
  s = deal(s);
  assert.equal(s.objects.o1, undefined);
  assert.equal(s.objects[ids[0]].damage, 0);
  s = passRound(s);
  assert.equal(s.step, "damage");
  s = deal(s);
  assert.equal(s.objects[ids[0]].damage, 0);
  assert.equal(s.players[1].life, 40);
});
test("double strike deals in both waves and a dead blocker does not make the attacker unblocked", () => {
  let { s, ids } = fight({
    power: "3 / 4",
    rules: "Double strike.",
    blockers: [{ stats: "1 / 1" }],
  });
  s = deal(s);
  assert.equal(s.objects[ids[0]], undefined);
  assert.equal(s.blockedAttackers.o1, true);
  s = passRound(s);
  s = deal(s);
  assert.equal(s.players[1].life, 40);
  let unblocked = fight({
    power: "3 / 4",
    rules: "Double strike.",
    blockers: [],
  }).s;
  unblocked = deal(unblocked);
  unblocked = passRound(unblocked);
  unblocked = deal(unblocked);
  assert.equal(unblocked.players[1].life, 34);
});
test("trample damage requires lethal assignments, with deathtouch and lifelink applied", () => {
  let { s, ids } = fight({
    power: "5 / 5",
    rules: "Trample. Deathtouch. Lifelink.",
    blockers: [{ stats: "2 / 4" }],
  });
  assert.deepEqual(
    s.damageAssignments
      .find((p) => p.source === "o1")
      .targets.map((t) => t.amount),
    [1, 4],
  );
  const invalid = reduce(s, {
    type: "assign-damage",
    id: "o1",
    player: "p0",
    amounts: [0, 5],
  });
  assert.throws(() => deal(invalid), /lethal/);
  s = deal(s);
  assert.equal(s.objects[ids[0]], undefined);
  assert.equal(s.players[1].life, 36);
  assert.equal(s.players[0].life, 45);
  assert.equal(s.objects.o1.damage, 2);
});
test("indestructible survives lethal damage and marked damage clears in cleanup", () => {
  let { s, ids } = fight({
    power: "4 / 5",
    blockers: [{ stats: "2 / 2", rules: "Indestructible." }],
  });
  s = deal(s);
  assert.equal(s.objects[ids[0]].damage, 4);
  assert.equal(s.objects.o1.damage, 2);
  s = atStep(s, "cleanup");
  assert.equal(s.objects[ids[0]].damage, 0);
  assert.equal(s.objects.o1.damage, 0);
});
test("dying tokens cease to exist and stolen creatures die to the owner's graveyard", () => {
  let { s, ids } = fight({
    power: "4 / 5",
    blockers: [{ stats: "1 / 1" }, { stats: "1 / 1" }],
  });
  const token = ids[1];
  s = deal(s);
  assert.equal(
    Object.values(s.objects).some((o) => o.card === "card-" + token),
    false,
  );
  let stolen = atStep(createTable(2), "attackers");
  stolen.objects.o1.owner = "p1";
  stolen.objects.o1.stats = "1 / 1";
  stolen = reduce(stolen, { type: "attack", id: "o1", defender: "p1" });
  stolen = reduce(stolen, { type: "confirm-step", player: "p0" });
  stolen = passRound(stolen);
  const b = contents(stolen, "battlefield").find(
    (o) => o.controller === "p1" && o.stats,
  ).id;
  stolen = reduce(stolen, {
    type: "block",
    id: b,
    attacker: "o1",
    player: "p1",
  });
  stolen = reduce(stolen, { type: "confirm-step", player: "p1" });
  stolen = passRound(stolen);
  stolen = deal(stolen);
  assert.ok(
    contents(stolen, "graveyard", "p1").some((o) => o.card === "card-o1"),
  );
});
test("multiple defending players choose blockers in turn order, then damage reaches each defender", () => {
  let s = atStep(createTable(4), "attackers");
  s = reduce(s, { type: "token" });
  const second = contents(s, "battlefield").at(-1).id;
  s.objects[second].controlledSinceTurn = 0;
  s = reduce(s, { type: "attack", id: "o1", defender: "p1" });
  s = reduce(s, { type: "attack", id: second, defender: "p3" });
  s = reduce(s, { type: "confirm-step", player: "p0" });
  s = passRound(s);
  assert.deepEqual(s.blockingPlayers, ["p1", "p3"]);
  assert.throws(
    () => reduce(s, { type: "confirm-step", player: "p3" }),
    /first/,
  );
  s = reduce(s, { type: "confirm-step", player: "p1" });
  assert.equal(s.priority, null);
  s = reduce(s, { type: "confirm-step", player: "p3" });
  assert.equal(s.priority, "p0");
  s = passRound(s);
  s = deal(s);
  assert.equal(s.players[1].life, 38);
  assert.equal(s.players[3].life, 39);
});

test("tapping a blocker after declaration does not remove it from combat", () => {
  let { s, ids } = fight();
  s.objects[ids[0]].tapped = true;
  s = deal(s);
  assert.equal(s.objects.o1, undefined);
  assert.equal(s.objects[ids[0]], undefined);
});
test("creatures with zero toughness die even with indestructible", () => {
  let s = atStep(createTable(2), "upkeep");
  s.objects.o1.stats = "2 / 0";
  s.objects.o1.rules = "Indestructible.";
  s = reduce(s, { type: "pass", player: "p0" });
  assert.equal(s.objects.o1, undefined);
});

test("multiplayer blocker declarations wrap around from the active seat", () => {
  let s = createTable(4);
  s.active = "p2";
  s.viewer = "p2";
  s.turn = 3;
  s = atStep(s, "attackers");
  s = reduce(s, { type: "token" });
  const extra = contents(s, "battlefield").at(-1).id;
  s.objects[extra].controlledSinceTurn = 0;
  s = reduce(s, { type: "attack", id: "o49", defender: "p3" });
  s = reduce(s, { type: "attack", id: extra, defender: "p0" });
  s = reduce(s, { type: "confirm-step", player: "p2" });
  s = passRound(s);
  assert.deepEqual(s.blockingPlayers, ["p3", "p0"]);
});
// Targeted sample effects use object identity and recheck targets on resolution.
function spellFixture(name) {
  let s = reduce(createTable(2), { type: "confirm-step", player: "p0" });
  s = reduce(s, { type: "sample-spells" });
  s = reduce(s, { type: "tap", id: "o3" });
  const spell = contents(s, "hand", "p0").find((o) => o.name === name);
  return { s, id: spell.id };
}
test("sample damage targets players and resolves after all players pass", () => {
  let { s, id } = spellFixture("Cinder Spark");
  s = reduce(s, { type: "cast", id, target: { kind: "player", id: "p1" } });
  assert.equal(s.players[1].life, 40);
  s = passRound(s);
  assert.equal(s.players[1].life, 37);
  assert.equal(s.zones.stack.length, 0);
});
test("spell damage marks creatures and lethal damage kills them", () => {
  let { s, id } = spellFixture("Cinder Spark");
  s = reduce(s, { type: "cast", id, target: { kind: "object", id: "o25" } });
  s = passRound(s);
  assert.equal(s.objects.o25, undefined);
  assert.ok(
    contents(s, "graveyard", "p1").some((o) => o.name === "Cloudwing Drake"),
  );
});
test("invalid spell targets reject casting atomically", () => {
  const { s, id } = spellFixture("Quiet Demise"),
    before = structuredClone(s);
  assert.throws(
    () => reduce(s, { type: "cast", id, target: { kind: "player", id: "p1" } }),
    /legal target/,
  );
  assert.throws(
    () =>
      reduce(s, { type: "cast", id, target: { kind: "object", id: "o27" } }),
    /legal target/,
  );
  assert.deepEqual(s, before);
});
test("destroy removes a creature but respects indestructible", () => {
  for (const protectedCreature of [false, true]) {
    let { s, id } = spellFixture("Quiet Demise");
    if (protectedCreature) s.objects.o25.rules = "Indestructible.";
    s = passRound(
      reduce(s, { type: "cast", id, target: { kind: "object", id: "o25" } }),
    );
    assert.equal(!!s.objects.o25, protectedCreature);
  }
});
test("a target leaving and returning is a new object and the spell does not resolve", () => {
  let { s, id } = spellFixture("Quiet Demise");
  s = reduce(s, { type: "cast", id, target: { kind: "object", id: "o25" } });
  const card = s.objects.o25.card;
  s = reduce(s, { type: "move", id: "o25", zone: "graveyard" });
  const grave = contents(s, "graveyard", "p1").find((o) => o.card === card);
  s = reduce(s, { type: "move", id: grave.id, zone: "battlefield" });
  s = passRound(s);
  assert.match(s.log.at(-1), /did not resolve/);
  assert.ok(contents(s, "battlefield").some((o) => o.card === card));
});
test("draw effect draws for its caster even after changing perspective", () => {
  let { s, id } = spellFixture("Sudden Insight");
  const before = contents(s, "hand", "p0").length;
  s = reduce(s, { type: "cast", id });
  s = reduce(s, { type: "perspective", player: "p1" });
  s = passRound(s);
  assert.equal(contents(s, "hand", "p0").length, before + 1);
  assert.equal(contents(s, "library", "p0").length, 10);
});
test("hexproof rejects opponent targets but allows their own controller", () => {
  let { s, id } = spellFixture("Quiet Demise");
  s.objects.o25.rules = "Hexproof.";
  assert.throws(
    () =>
      reduce(s, { type: "cast", id, target: { kind: "object", id: "o25" } }),
    /legal target/,
  );
  s.objects.o1.rules = "Hexproof.";
  s = reduce(s, { type: "cast", id, target: { kind: "object", id: "o1" } });
  assert.equal(s.zones.stack.length, 1);
});
