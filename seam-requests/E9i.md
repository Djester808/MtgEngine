# E9i — seam requests

All four items landed on seams wave 9 had already opened, so **nothing new is asked for**. What
follows is the half of each that another folder still owes, so it is not built to a different shape.

## 1. CR 608.2d — the store is written and read; two other builders of a context still seed nothing

`ContextFor` now seeds a resolution's `Chosen` from `GameObject.Chosen` of the source and then from
`StackEntry.Chosen` (the entry wins a clash, because a firing chose later than the object did), and
the `ChooseValue` arm emits `ValueChosen(source, key, value)` when the resolving source is a
permanent. Reading a stored value back emits nothing and asks nobody, so the event fires once per
choice and the fold is idempotent.

E6c §1 named three context builders, and only the interpreter's is mine. `TriggerSystem.Context`
and the layer system's still build `Chosen` empty, so a trigger or a static ability asked outside a
resolution reads no colour even though the object is keeping one — **E6d**'s, as decided. The other
half of E6c §2 is also still owed: nothing writes `StackEntry.Chosen`, so `"tapped-for-mana-color"`
has no writer. The interpreter reads whatever is put there.

A spell keeps nothing: the object that chose is on the stack, not the battlefield, so CR 400.7
leaves the choice with the object that made it. That is deliberate and tested.

## 2. CR 608.2g — the may-cast announces through `CastInstructed`

`Casting.Cast` is no longer called from the interpreter at all; every announcement the walk makes
goes through `ICastingSystem.CastInstructed`. The default is still `Cast`, so nothing changes for a
casting system that has not split them — **E3h** makes it skip CR 117.1a's timing check, which is
EINT3 §3's gap. Both interpreter fakes record the two doors separately, so a regression that routes
an instructed cast through the player's door fails here.

## 3. CR 106.6 — one place builds a provenance block

The `AddMana` arm builds its delta with `Mana.ManaSources.Added(pool, mana.SpendOnlyOn, source,
mana.Key)`, which is E3g §3 as written, and the interpreter's own `new ManaProvenance(...)` is gone:
a keyed instruction asks `ManaSources.Produced` for one block per single mana under `"key#n"`
instead of spelling a block itself. Two consequences worth recording:

- An `AddMana` carrying `SpendOnlyOn` now produces a **restricted** block (CR 106.6b). It did not
  before, and the vocabulary could already say it, so a card that spells "spend this mana only on
  ..." behaves differently from wave 9 — correctly, but differently.
- Mana with no source still remembers nothing, so a `PayOnlyFrom` restriction refuses it. That is
  E3g's stated honest failure and is unchanged.

## 4. CR 707.12 — `MayCast(AsCopy)` casts the copy, where a zone system makes one

The arm asks `IZoneSystem.CopyInPlace(state, card, owner)`, takes the **last `ObjectCreated`** in
that outcome as the copy and announces that through `CastInstructed`; the original is never named.
The seam's default throws, and the arm catches `NotImplementedException` and re-raises the named CR
707.12 refusal, so a real game is exactly where wave 9 left it until a zone system builds the copy.

Still owed, both outside `Interpreter/`:

- **`Zones.ZoneSystem.CopyInPlace`** — the decision allowed "a zone builder in wave 10 if the box
  allows". A test fake builds one, so the interpreter's half is proved; nothing in a real game
  reaches it yet.
- **CR 704.5e** as a state-based action: a copy of a card in a zone that is not the stack or the
  battlefield ceases to exist. That is the rules system's, and without it a copy the player declines
  to cast would linger.

A lasting permission (`MayCast(Until: …)`) with `AsCopy` still refuses naming CR 118.9, unchanged:
CR 601.3a's permission has no half to say "a copy" with, and no pilot face wants one.
