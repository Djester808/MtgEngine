# E5l — wave 28: the firing's two keys, V16's four amounts, and the one number nobody carries

Wave 28. Owned: `MtgEngine.Rules/Query/**`, `tests/MtgEngine.Rules.Tests/Query*.cs`.
Landed: `Query/Query.Wave28.cs` (new partial), six arms in `Query/Query.Amounts.cs`, the `NotHas`
line in `Query/Query.cs`, ten tests in `tests/MtgEngine.Rules.Tests/QueryTests.Wave28.cs`
(`QueryTests` anchor **76**, up from 66). Build clean, format clean on both projects.

## 1. Ask — `Replacement/**` must carry the prevention's own amount (CR 615)

`PreventedDamage` ("for each 1 damage prevented this way") is the one V16 amount that has nothing to
read. `ReplacementSystem.Spend` (`Replacement/ReplacementSystem.Applying.cs`) computes the number —
`prevented = min(available, damage.Amount)` — and drops it; `ReplacementSystem.Prevent` computes the
same for a `PreventThatDamage`; and `ReplacementSystem.Context(source, controller, proposed)`
(`ReplacementSystem.cs:211`) hands the interpreter an **empty `Chosen`**, so no walk of the effect
can see it.

**Ask.** When a prevention is applied to a `DamageDealt`, run the rest of that effect with the amount
in the context under **`MtgEngine.Rules.Query.Query.PreventedDamageAmount`** (`"prevented-damage"`,
public, declared and documented in `Query/Query.Wave28.cs`) — the decimal, culture-invariant text
`TriggerSystem` writes its own keys as. Name the constant, never re-spell the string.

Three things the writer must get right, all of which the reader relies on:

- The amount is **what this application removed from the event**, `min(shield, damage.Amount)` — 2 for
  a shield that meets 2 of 5. It is not the damage the event would have dealt and not what is left.
- **Write it even when it is zero.** A shield that meets no damage prevented none, and that is a real
  answer the card can read ("for each 1 damage prevented" then does nothing). The reader **refuses** a
  missing key rather than answering 0, precisely so "nothing prevented" and "no prevention has run"
  cannot look alike — the same rule E6j § 2 set for the missing mana key.
- CR 615.12: a prevention applied to unpreventable damage prevents nothing, so it writes `0`, not the
  amount it would have prevented. (E9v § 2 asks the same road for that reading.)

This is the only one of my six that is refused today. It is a whole clause on a card, not a shape the
engine can approximate, so the arm refuses with the rule (`RefusedException`, CR 615) and nothing
plays a wrong number in the meantime.

## 2. Recorded — `TriggeringPreventedDamage` reads 0 outside a firing, and that is deliberate

E6j § 2's rule holds: the reader names `TriggerSystem.TriggeringPreventedDamageAmount` and **never**
falls back to `TriggeringDamageAmount`. A copy of such an ability made outside a firing carries no key
and reads 0, exactly as `TriggeringDamage` does — no prevention happened, which is what the number
then stands for. The dealt key is a different event's number and is never consulted; the test asserts
the no-fallback half with both keys present and with only the dealt one.

`ManaSpentOnTriggering` is the opposite case and refuses. The **total** key
(`TriggerSystem.ManaSpentOnTriggeringAmount`) is written for **every** `SpellCast` firing including as
`"0"`, so its absence means the ability watched no cast and is refused (CR 400.7d); a **colour** key is
written only for a colour actually paid, so a colour with no key is the honest 0 once the total proves
a cast was watched. Neither reader touches `StackEntry.ManaSpent` on the source — that is
`ManaSpentOnThis`, a different spell, and the triggering spell has usually ceased to exist (CR 400.7).

## 3. Recorded — `GreatestDamageDealtThisTurn` with `By` reads the pair map, and its seats

`GreatestDamageTo` folds with a **max** over the players the `PlayerFilter` admits, never a sum: a
table that spread 7 damage over three opponents totals 7 and contains no opponent who was dealt 7, and
in a duel the two folds agree — so the test is a three-player world, because a two-player one cannot
tell the spellings apart. With `By` named it sums `History.DamageDealtToPlayer` per player and then
maxes; with `By` null it reads `History.DamageReceivedByPlayer`. The seats are
`state.Players.Where(p => !p.HasLost)`, the same set `EachPlayer` resolves to (CR 800.4a) — a player
who has left the game is no longer an opponent. No admitted player is 0, not a refusal.

## 4. Recorded — `CreaturesDiedThisTurn` refuses two filter shapes

`TurnHistory.Died` already keeps the departed object (folded by `ObjectMoved` for a
battlefield → graveyard move only), so CR 700.4 is answered by counting the move and the graveyard is
never consulted. The filter is read against the departed object's printed characteristics through
`ICharacteristics.PrintedOf`, the look-back `CreatureDied` and `WasAsCreated` already use. Two shapes
are refused rather than guessed, matching V16's L045:

- a `Matching` with `In` set — where the card is *now* is not what "died" counts;
- a `Matching` with `Also` set — a predicate cannot be asked of an object the state no longer holds.

## 5. Recorded — `GreatestSameNameCount` and `ObjectFilter.NotHas`

`GreatestSameName` groups the admitted objects by the name the layers report (Ordinal) and returns the
largest group; **a nameless object is a group of one** and never joins another nameless one
(CR 201.2a), so nameless permanents cannot fake a group. A filter carrying `Named` is refused — the
card says "the same name", not a name, and answering it would make that spelling read as a plain
`CountOf`. The amount takes no player: every printed shape puts the controller in the filter.

`NotHas` negates the same `Characteristics.Has` lookup `Has` uses (CR 702.1), so a **granted** keyword
is had and the object is refused — it is not a subtype list. Both fields set to one keyword admit
nothing, which the test asserts in the engine as well as V16's L045 asserts in the linter.

## 6. Not done in this box

- **No corpus card was played through `verify`.** Every claim here is from the arms and their tests;
  the box is the Rules project with a `QueryTests` filter and never loads the corpus. The four V16
  amounts are **unverified against a real card file** — the redo V16 § "Asks" (4) calls for is what
  would exercise them end to end.
- **`PreventedDamage` cannot be verified at all** until the ask in § 1 lands; any card of that shape
  refuses today and will report as unsupported rather than playing a wrong number.
