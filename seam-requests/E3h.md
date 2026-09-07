# E3h — the casting gaps EINT3 found, and what a payment records

Nothing on a seam had to change: `ICastingSystem.CastInstructed`, `StackEntry.ManaProvenanceSpent`,
`ManaChanged.SpentOn` and `ActivateAbility.ManaColors` were all landed by the wave-9 seam commit and
this wave fills them in. Four notes for the coordinator, three of which are other builders' business.

## 1. E9i must switch the interpreter, or a may-cast that was accepted is now refused

`CastSpell.WithoutPayingManaCost` on a command now grants nothing (CR 601.3 with 118.9): the zone
check holds unless a `MayPlayFromZone` permission or the instructed path grants it, and the mana cost
is waived only by one of the same two. `EffectInterpreter.Retarget.cs` builds its may-cast command
with `WithoutPayingManaCost: may.WithoutPayingManaCost` and still calls `ICastingSystem.Cast`
(two call sites, around lines 206 and 281). Until **E9i** points those at `CastInstructed`:

- a `MayCast(WithoutPayingManaCost: true)` whose card is in a hand is refused naming 118.9;
- one whose card is anywhere else is refused naming 601.3;
- and the old refusal, 117.1a, still stands in front of both, because a spell cast during a
  resolution has no priority holder.

That is the whole of EINT3 § 3 and § 4, and both halves are only closed when the two land together.
`InterpreterTests`/`InterpreterDesignationTests` do not fail today because they assert the command
the interpreter builds rather than casting through the system, so nothing red will warn E9i.

## 2. CR 704.5m's Aura arm is no longer reachable through a cast

An Aura whose enchant ability names an `ObjectFilter` and no `PlayerFilter` is refused at
announcement now (CR 601.2c with 702.5b) instead of being cast, resolved and buried on the next
state-based action check. E5c's CR 704.5m arm is still right and still needed — an Aura can stop
being legally attached long after it resolved — but the road EINT3 § 1 walked to it is closed, and
`WaveEightIntegrationTests` asserts the refusal in its place. Nothing in `StateBasedActions` changed.

## 3. What a payment records, for E6d

- `ManaChanged.SpentOn` is set by the payment: for a cast it is **the spell on the stack**, not the
  card in the hand, because a zone change makes a new object (CR 400.7) and "when that mana is spent
  to cast a spell" asks about the spell. The payment's events therefore learn the id after the move.
  For an activation it is the ability's source. Production leaves it null.
- `StackEntry.ManaProvenanceSpent` carries the blocks that paid, **positive**. `ManaSources.Spend`
  reports them negative because the same list empties the pool; the sign is turned once, in
  `CastingSystem.CastCore`, and nowhere else.
- An activation's entry carries the same list, so an ability's payment is readable too.

## 4. `ActivateAbility.ManaColors` is read, one letter per mana

CR 608.2d: the nth mana an "add one mana of any color" instruction adds reads the nth letter, so an
ability that adds two reads `"WU"`. A missing letter is refused naming 608.2d as before; a letter
that names no colour is refused too rather than falling through to a default. `CostChoices` still
answers behind it, so nothing that named its colour there broke. **P13** still owes `ActivateStep`
generating the letters for a scenario.

## 5. Where the code went

`Casting/CastingSystem.cs` was at 499 lines, so `Cast` moved into a new partial,
`Casting/CastingSystem.Instructed.cs`, beside `CastInstructed`; both are one body (`CastCore`) with a
flag, so the two roads can never disagree about what a spell costs. `RequireCastingTiming` and
`SorceryTimingRule` moved with it. `CastPlan.For` and `CastPlan.Alternative` take the flag.
