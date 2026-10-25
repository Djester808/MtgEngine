// Presentation state only. Object identities and rules state are never mutated.
const clamp = (value) => Math.max(0, Math.min(1, value));
export function stackSignature(o) {
  return JSON.stringify([
    o.controller,
    o.mana ? "lands" : o.token ? "tokens" : "matching",
    o.name,
    o.type,
    o.stats,
    o.tapped,
    o.counters,
  ]);
}
export function dropStackKind(source, target, settings = {}) {
  if (
    !source ||
    !target ||
    source.id === target.id ||
    source.controller !== target.controller
  )
    return null;
  if (source.mana && target.mana) return "lands";
  if (source.token && target.token && source.name === target.name)
    return "tokens";
  if (
    settings.matching &&
    source.name === target.name &&
    source.type === target.type
  )
    return "matching";
  return null;
}
export function fanGroup(
  count,
  anchorX,
  anchorY,
  width,
  height,
  cardWidth,
  cardHeight,
) {
  const maxX = Math.max(0, width - cardWidth - 10);
  const maxY = Math.max(0, height - cardHeight - 10);
  const dx = Math.min(18, maxX / (count + 1));
  const dy = Math.min(5, maxY / (count + 1));
  const x = Math.max(0, Math.min(anchorX, maxX - dx * (count - 1)));
  const y = Math.max(0, Math.min(anchorY, maxY - dy * (count - 1)));
  return Array.from({ length: count }, (_, i) => ({
    x: x + i * dx,
    y: y + i * dy,
  }));
}
export class Arrangements {
  positions = new Map();
  groups = new Map();
  nextGroup = 1;
  z = 1;
  clear() {
    this.positions.clear();
    this.groups.clear();
    this.z = 1;
  }
  capture(controller, cards) {
    for (const card of cards)
      if (!this.positions.has(card.id))
        this.positions.set(card.id, {
          controller,
          x: clamp(card.x),
          y: clamp(card.y),
          z: this.z++,
        });
  }
  move(ids, dx, dy) {
    const entries = ids.map((id) => this.positions.get(id)).filter(Boolean);
    if (!entries.length) return;
    // Apply one bounded delta so a group keeps its shape at the edge.
    dx = Math.max(
      -Math.min(...entries.map((p) => p.x)),
      Math.min(dx, 1 - Math.max(...entries.map((p) => p.x))),
    );
    dy = Math.max(
      -Math.min(...entries.map((p) => p.y)),
      Math.min(dy, 1 - Math.max(...entries.map((p) => p.y))),
    );
    for (const p of entries) {
      p.x += dx;
      p.y += dy;
      p.z = this.z++;
    }
  }
  front(id) {
    const p = this.positions.get(id);
    if (p) p.z = this.z++;
  }
  removeFromGroup(id) {
    for (const [key, group] of this.groups) {
      group.ids = group.ids.filter((member) => member !== id);
      if (group.ids.length < 2) this.groups.delete(key);
    }
  }
  group(controller, ids, kind) {
    ids = [...new Set(ids)].filter(
      (id) => this.positions.get(id)?.controller === controller,
    );
    if (ids.length < 2) return null;
    for (const id of ids) this.removeFromGroup(id);
    let first;
    for (let i = 0; i < ids.length; i += 3) {
      const members = ids.slice(i, i + 3);
      if (members.length < 2) continue;
      const key = `g${this.nextGroup++}`;
      first ??= key;
      this.groups.set(key, { controller, ids: members, kind, manual: true });
    }
    return first;
  }
  prune(objects) {
    for (const [id, p] of this.positions)
      if (
        objects[id]?.zone !== "battlefield" ||
        objects[id]?.controller !== p.controller
      )
        this.positions.delete(id);
    for (const [key, group] of this.groups) {
      group.ids = group.ids.filter((id) => this.positions.has(id));
      if (group.ids.length < 2) this.groups.delete(key);
    }
  }
  resetController(controller) {
    for (const [id, p] of this.positions)
      if (p.controller === controller) this.positions.delete(id);
    for (const [key, group] of this.groups)
      if (group.controller === controller) this.groups.delete(key);
  }
  members(id) {
    return (
      [...this.groups.values()].find((g) => g.ids.includes(id))?.ids ?? [id]
    );
  }
  snapshot() {
    return structuredClone([...this.positions]);
  }
  restore(snapshot) {
    this.positions = new Map(structuredClone(snapshot));
  }
}
