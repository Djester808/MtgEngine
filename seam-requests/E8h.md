# E8h — wave 30: a damage replacement reads its recipient, a permanent enters prepared, and Karoo is a scenario

Owned: `MtgEngine.Rules/Replacement/**`, `tests/MtgEngine.Rules.Tests/ReplacementEffectTests*.cs`,
`tests/MtgEngine.Rules.Tests/ZoneReplacementTests*.cs`. Landed: `Replacement/ReplacementSystem.Wave30.cs`
(new partial), one clause in `ReplacementSystem.Matching.cs`, one arm in `ReplacementSystem.Applying.cs`,
and eight tests in `tests/MtgEngine.Rules.Tests/ReplacementEffectTests.Wave30.cs` (`ReplacementEffectTests`
anchor **54**, up from 46). Build clean and format clean on both projects; the whole Rules project is
**1243 green**, up from the 1235 at the wave-29 tip.

## 1. Done — `WouldDealDamage.To` / `.ToPlayer` (V18 row 9, CR 614.1)

`Matches`'s `WouldDealDamage` arm read the source and the combat flag and nothing about where the damage
landed, so "damage to an opponent or a permanent an opponent controls" replaced damage to *anything*.
The arm now ends in `Recipient`, which is a **union and not an intersection**: a damage event has exactly
one recipient (CR 119.3 for a player, CR 120.3 for a permanent), so `To` admits the object half,
`ToPlayer` the player half, and both together are the printed "or". Read as an intersection the card
would match neither half and do nothing.

**Neither rider means every recipient**, and that default is load-bearing rather than tidy: every stored
card carrying a bare `WouldDealDamage` was written before the two fields existed and means "damage from
this source, wherever it lands". A default of "matches nothing" would silently stop all of them; the
sixth test pins it.

Six tests, one per half: the object half admits an object and passes over a player, the player half
admits an opponent and passes over its own controller (CR 102.1 with 109.5 — the filter is read from the
ability's controller, so it is a real filter and not a spelling of "any player"), both halves together
admit either, and no rider admits both. The second of those asserts **3** where the arm before this
produced 6, which is the whole of the defect.

## 2. Done, and one half is not mine — `EntersPrepared` (V18 row 7, CR 722.3a with 614.1c)

`Perform` gained an `EntersPrepared` arm beside `EntersTapped`: the designation rides on the object the
move carries, so it is part of the entry and not a later event something could respond to. The older
spelling — a `Prepare` wrapped in a replacement, which three stored files still use — was already there
and now reads the same helper, so the two spellings cannot disagree.

CR 722.3a's first refusal is enforced: **a permanent can't gain the designation unless it has a prepare
spell**, which is a face of its own card with `FaceRole.PrepareSpell` (CR 722.2a). A permanent that has
none still enters — only the designation is refused, not the entry. The rule's second refusal (a
permanent already prepared can't gain it again) has nothing to refuse on this road: an entering permanent
is a new object (CR 400.7) and the designation is a set, so it is not observable until the copy below
exists and the question becomes how many copies were made. It is documented in the arm and deliberately
untested rather than tested against itself.

Its CR reference was also wrong: the `Prepare` arm cited **702.180**, which is harmonize. Prepared is
**722.3a**. Corrected in place.

### Ask — `Zones/**` (E1-series): CR 722.3c, the copy in exile

As a permanent with a prepare spell gains the designation, its controller **creates a copy of that object
in exile** carrying only the prepare spell's characteristics; the copy stays in exile for as long as the
permanent is on the battlefield and prepared (an explicit exception to CR 704.5e); the controller may
cast it; and the permanent **loses the designation at the time that spell becomes cast** (CR 601.2i).

That is a second object in a second zone. The replacement road may only modify the event it was offered
(CR 614.1) — it cannot create one, and inventing a `Sequence` of extra events here would put the copy on
a road no other replacement uses. The zone system already makes copies (`ZoneSystem.Copies.cs` with the
`CopyOfCard` designation), so it is the one place that can. Three pieces it needs and the replacement
road cannot give it: the copy is made **as the designation is gained** (so the zone system has to watch
for the designation arriving, on this road as an `ObjectMoved` whose `To` carries it, and on the
interpreter's as an `ObjectDesignated`); it is made from the **`FaceRole.PrepareSpell` face** and not the
main one; and it is **removed when the permanent stops being prepared or leaves the battlefield**.

Until it lands, "enters prepared" gives a real designation that `IsPrepared` reads and nothing to cast.
That is a card played incompletely, not wrongly.

### Ask — the definition builder (P-series): a preparation card with no inset face

`cards/58b0c737-…` is a preparation card whose one replacement is "This creature enters prepared", and
its stored definition carries **no `PrepareSpell` face** — only `cards/e5152e8d-…` has one. By CR 722.3a
that card can never gain the designation, and the refusal is correct at this end: the gap is that the
definition does not carry the card's inset frame. It costs no verify today (neither file has a scenario
that asserts prepared), so it is a data ask and not a blocker.

## 3. Recorded — Karoo (`d4e875d9`, 1/2) is a scenario defect, not a replacement one

Neither road the brief named put the land in the graveyard. **Both roads are innocent**: CR 613.11's
`Forbidden` arm never fires (no `CantEnterBattlefield` is in force in that scenario), and the entering
road runs its one replacement correctly — the `EntersTapped` of the card's first ability — and the land
does arrive on the battlefield.

What the smoke scenario then does is play the card's *second* ability, which is right: "when this enters,
sacrifice it unless you return a Plains you control to its owner's hand". The scenario's setup gives
seat A an **empty battlefield**, so there is no Plains to return, the cost cannot be paid, and the
`Unless` sacrifices the land — into the graveyard, which is exactly what `InZone was Graveyard` reports.
The engine played the card correctly and the generated expectation (`InZone Karoo Battlefield`) is the
thing that is wrong.

**Ask — P-series (the scenario generator).** A smoke scenario for a permanent whose enters-trigger can
sacrifice it needs either the setup its trigger asks for (here a Plains on the battlefield, and the
expectation then holds as written) or an expectation that does not assume the permanent survives its own
arrival. This is a family and not one card: every "enters, sacrifice it unless you …" land reads the
same way. The second scenario on the file passes, so the card is 1/2 for this reason alone.

**Unverified end to end.** `verify cards --only d4e875d9` was not run: nothing is built under
`MtgEngine.Cards.Pipeline/bin` in this worktree and the corpus is not reachable from it, so the box could
not play the card. The diagnosis above is read off the stored card file — its definition, its scenario
setup and steps, and the verification record `passed 1, total 2` at engine `0f74a03de66e7d38` — and not
off a run.

## 4. Not done in this box

- **`Admits`'s one-line `InAnyOf` rewrite is untouched**, as the brief says: it is deleted next wave,
  after E5m's plural zone read lands in `Query/**`.
- **No mutation run.** The eight tests each assert a number or an absence the code before this could not
  produce (3 where the recipient read was missing and the old arm doubled; a designation on a move the
  old code sent down CR 614.1a's "instead" road instead, which drops the entry), but none was watched
  failing against the old build.
- **No corpus card was played.** The box is the Rules project with a `ReplacementEffectTests` filter; it
  never loads the corpus. What is proved is the arms, not a card.
