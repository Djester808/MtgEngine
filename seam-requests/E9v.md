# E9v — wave 27: the two failures, and the four asks outside `MtgEngine.Rules/Interpreter/**`

Built here (all with tests, `InterpreterTests` 128/128 green): `MoveCounters` read at all — the record
had **no arm anywhere in `MtgEngine.Rules`** before this wave — with `All`, a printed `N`, and
`AnyNumber` asked through the choice road (CR 122.5); `Counter.Where` plumbed through `Leave` so a
countered spell is put at the placement the record names (CR 701.6a); and the two continuous effects
the `Apply` arm silently dropped because they name no object, `DamageCantBePrevented` (CR 615.12) and
`PlayWithTopCardRevealed` (CR 401.5), now created — the first against every player in the game
(CR 102.1), the second against the player whose library it is.

## 1. Both wave-26 failures are in `cards/`, not in the interpreter

Read from the card files and the arms, **not** run through `verify` (the box ended first — the
per-card check is unverified, the reasoning below is from the definition, the scenario and the code).

**Lotus Field `134d5b82-7940-4b33-a922-7f9d1f403e50` — the scenario is unwinnable as generated.**
Its trigger is `Sacrifice { Choice: { types: [Land] }, N: 2 }` and the scenario's setup gives player A
an **empty battlefield**. The land plays, enters, and the only land its own trigger admits is Lotus
Field itself, so it sacrifices itself — which is the card's real ruling (CR 701.16b: a player
sacrifices as many as they can) — and the single expectation `InZone Lotus Field Battlefield` cannot
hold. The engine played it right. **Ask (folder `cards/`, P-series / the smoke generator):** the
setup needs two other lands on A's battlefield before the land is played, or the expectation has to
be the card's actual outcome. A "this enters, sacrifice N of its own type" card generated with an
empty board is a class, not one card — worth a generator rule.

**Gamble `a54f0869-94c8-42af-9080-166efb9486a4` — the expectations are arithmetically impossible,
and the engine also ignores `by: Random`.** The scenario expects, after the resolution, hand 4,
graveyard 1 and `find-1` in hand. The card discards a card, so the graveyard must hold Gamble **and**
the discarded card (2) and the hand must be 3. No correct play satisfies that scenario.
**Ask (folder `cards/`):** regenerate it — hand 3, graveyard 2, and `find-1` **not** asserted to be
anywhere, because a random discard may take it (that is the card).

**Open in my own folder, not done in this box:** `EffectInterpreter.Actions.cs`'s `Discard` arm reads
neither `DiscardChooser` (`Random`, `Controller`) nor `Discard.What`; every discard is asked of the
owner as a choice. CR 701.9a with 104.x: a discard at random is not a choice at all. Whoever takes it
needs a **seeded random source** in the interpreter — there is none; the shuffle's is the zone
system's — so it is an ask to that folder as much as a build: expose the scenario seed's source to
the interpreter (`IZoneSystem` or a small `IRandomness` seam), then the arm picks the card.

## 2. Ask — the prevention road must read `DamageCantBePrevented` (CR 615.12)

Folder: the **replacement/prevention system** (`MtgEngine.Rules/Replacement/**`), not mine. The effect
is now created and carries `From` and `Combat`; the reading it needs is the one CR 615.12 spells out
and no other: damage the effect admits is unpreventable however it is dealt, an applicable prevention
effect is **still applied to it once** (615.12a), prevents nothing, whatever **else** that effect does
still happens, and an existing shield is **not** spent. It is not `PreventDamage` inverted.

## 3. Ask — the view must reveal the top card (CR 401.5)

Folder: **`MtgEngine.Rules/Views/**`** (`ViewProjector`). The effect is created against the player
named. CR 401.5: while it applies, that library's top card is revealed to **every** player — that is
the whole difference from `MayLookAtTopCard`, which shows it to one player. CR 401.6 makes it a new
object each time it stops being revealed and is revealed again. It grants no permission to play it.

## 4. Ask — `MoveCounters` onto more than one object

The arm refuses (`RefusedException`, CR 122.5) when the `To` selector names more than one object: the
vocabulary cannot say how the counters are divided. If a corpus card wants it, that is a V-series
field (a division), not a guess in the interpreter.
