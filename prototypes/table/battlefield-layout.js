// Fits discrete objects into a continuous surface. No rules state is changed.
export function cardArea(object) {
  return object.mana
    ? "mana"
    : object.type.toLowerCase().includes("creature")
      ? "creatures"
      : "support";
}
export function screenCardWidth(width, height, opponent = false) {
  return Math.max(
    24,
    Math.min(
      opponent ? 86 : 110,
      (height / 2 - (opponent ? 16 : 19)) / 1.4,
      opponent ? width * 0.19 : width * 0.24,
    ),
  );
}
export function screenAreaSlots(
  area,
  count,
  width,
  height,
  preferred,
  mirrored = false,
  gap = 12,
) {
  const front = mirrored ? height / 2 : 0;
  const top = area === "mana" ? (mirrored ? 0 : height / 2) : front;
  const left = area === "support" ? width * 0.72 : 0;
  const span =
    area === "mana" ? width : area === "support" ? width * 0.28 : width * 0.72;
  const footprint = preferred + gap;
  let capacity = Math.max(1, Math.floor(span / footprint));
  if (capacity % 2 === 0) capacity--;
  const total = Math.max(count, capacity);
  const pitch =
    total > 1
      ? Math.min(
          footprint,
          Math.max(0, span - preferred - gap + 12) / (total - 1),
        )
      : 0;
  const start = left + (span - preferred - pitch * (total - 1)) / 2;
  const slots = Array.from({ length: total }, (_, i) => ({
    x: start + i * pitch,
    y: top + (mirrored ? 14 : 16),
    width: preferred,
    height: preferred * 1.4,
  }));
  return {
    area,
    top,
    height: height / 2,
    left,
    span,
    slots,
  };
}
export function reorderHand(order, id, index) {
  if (!order.includes(id)) return [...order];
  const result = order.filter((key) => key !== id);
  result.splice(Math.max(0, Math.min(index, result.length)), 0, id);
  return result;
}
export function reconcileSlots(previous, keys, capacity = 4) {
  if (!previous.length) {
    const size = Math.max(capacity, keys.length);
    const order = Array(size).fill(null);
    const first = Math.floor((size - keys.length) / 2);
    keys.forEach((key, i) => (order[first + i] = key));
    return order;
  }
  const seen = new Set();
  const order = previous.map((key) =>
    keys.includes(key) && !seen.has(key) ? (seen.add(key), key) : null,
  );
  for (const key of keys)
    if (!seen.has(key)) {
      const gap =
        capacity > 4
          ? (order
              .map((value, index) => (value === null ? index : -1))
              .filter((index) => index >= 0)
              .sort(
                (a, b) =>
                  Math.abs(a - (capacity - 1) / 2) -
                  Math.abs(b - (capacity - 1) / 2),
              )[0] ?? -1)
          : order.indexOf(null);
      if (gap < 0) order.push(key);
      else order[gap] = key;
      seen.add(key);
    }
  while (order.length < Math.max(capacity, keys.length)) order.push(null);
  while (
    order.length > Math.max(capacity, keys.length) &&
    order.at(-1) === null
  )
    order.pop();
  return order;
}
export function moveSlot(order, key, index) {
  const next = [...order];
  const from = next.indexOf(key);
  if (from < 0 || index < 0 || index >= next.length) return next;
  [next[from], next[index]] = [next[index], next[from]];
  return next;
}
export function battlefieldColumns(width, preferred = 110, gap = 28) {
  const count = Math.max(1, Math.floor((width - 10) / (preferred + gap)));
  return count % 2 === 0 ? count - 1 : count;
}
export function resizeSlots(previous, keys, previousCapacity, capacity) {
  if (previousCapacity === undefined || previousCapacity === capacity)
    return reconcileSlots(previous, keys, capacity);
  const retained = previous.filter((key) => keys.includes(key));
  return reconcileSlots(
    [],
    [...retained, ...keys.filter((key) => !retained.includes(key))],
    capacity,
  );
}
export function centerSlotOffset(order, slots, width) {
  const occupied = order.flatMap((id, index) =>
    id && slots[index] ? [slots[index]] : [],
  );
  if (!occupied.length) return 0;
  return (
    width / 2 -
    (Math.min(...occupied.map((slot) => slot.x)) +
      Math.max(...occupied.map((slot) => slot.x + slot.width))) /
      2
  );
}
export function areaSlots(
  area,
  count,
  width,
  height,
  preferred = 72,
  mirrored = false,
  gap = 28,
) {
  const bands = {
    creatures: [0, 1 / 3],
    support: [1 / 3, 1 / 3],
    mana: [2 / 3, 1 / 3],
  };
  let [start, part] = bands[area];
  if (mirrored) start = 1 - start - part;
  const top = height * start,
    bandHeight = height * part;
  const natural = Math.max(8, preferred);
  const columns = battlefieldColumns(width, natural, gap);
  const rows = Math.max(1, Math.ceil(count / columns));
  const size = natural;
  const pitch = natural + gap;
  const left = (width - columns * pitch) / 2;
  const slots = Array.from({ length: columns * rows }, (_, i) => ({
    x: left + (i % columns) * pitch + (pitch - size) / 2,
    y: top + 15 + Math.floor(i / columns) * (size * 1.4 + 4),
    width: size,
    height: size * 1.4,
  }));
  return { area, top, height: bandHeight, slots };
}
export function slotAt(area, x, y) {
  if (y < area.top || y >= area.top + area.height || x < 0) return null;
  if (area.left !== undefined && (x < area.left || x > area.left + area.span))
    return null;
  if (!area.slots.length) return null;
  let best = 0,
    distance = Infinity;
  area.slots.forEach((p, i) => {
    const d = Math.hypot(x - p.x - p.width / 2, y - p.y - p.height / 2);
    if (d < distance) {
      best = i;
      distance = d;
    }
  });
  return best;
}
export function fitGroup(count, width, height, preferredWidth = 78) {
  if (!count || width <= 0 || height <= 0) return [];
  const gap = Math.min(4, Math.sqrt((width * height) / (count * 1.4)) / 4);
  let best = { size: 0, columns: 1 };
  for (let columns = 1; columns <= count; columns++) {
    const rows = Math.ceil(count / columns);
    const size = Math.min(
      preferredWidth,
      (width - gap * (columns - 1)) / columns,
      (height - gap * (rows - 1)) / (rows * 1.4),
    );
    if (size > best.size) best = { size, columns };
  }
  const size = Math.max(1, best.size);
  return Array.from({ length: count }, (_, index) => ({
    x: (index % best.columns) * (size + gap),
    y: Math.floor(index / best.columns) * (size * 1.4 + gap),
    width: size,
    height: size * 1.4,
  }));
}
export function arrangeBattlefield(
  front,
  mana,
  width,
  height,
  preferredWidth = 78,
) {
  const split = front && mana ? 0.61 : front ? 1 : 0;
  return [
    ...fitGroup(front, width, height * split, preferredWidth),
    ...fitGroup(mana, width, height * (1 - split), preferredWidth).map((p) => ({
      ...p,
      y: p.y + height * split,
    })),
  ];
}
