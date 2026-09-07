# E5e — seam requests

E5d's request landed: `IQuery.PlayerMatches` is on the wave-10 base, `StateBasedActions`'s private
copy is deleted, and 704.5m asks the seam. Three things could not be done inside `Sba/`,
`Replacement/` and `Views/`.

---

## 1. `Query.PlayerMatches` is not implemented yet, and one test on the base is red because of it

Not a request — a merge order. `MtgEngine.Rules/Engine/Seams.cs:330` still throws, E0e implements
it this wave, and `WaveEightIntegrationTests.An_Aura_that_enchants_a_player_resolves_attached_to_
them_and_survives_the_settle` runs the real query through `Game.Apply`, so it throws
`NotImplementedException` on this branch alone. Nothing else in the suite touches it: 667 of 668
pass here, and the fake query in `StateBasedActionTests.Fakes.cs` answers the seam with the
reading the seam's own summary names (candidate's point of view for `Also`).

**Gate E5e with E0e, or after it.** No change of mine can make that test green: `Query/` is E0e's.

## 2. `DamageDealt` carries its own life loss, so "your life total can't change" has to drop the damage

`MtgEngine.Rules/Events/GameEvent.cs:322-330`. `DamageDealt.Apply` subtracts `Amount` from the
player's life itself. CR 119.2 makes damage to a player a life loss, and CR 119.8 with 101.2 says
a life total that can't change cannot take that loss — but the event is one fold, so the only way
to honour the "can't" from the replacement system is to drop the whole event, which
`ReplacementSystem.Limits.Undealable` now does (`Replacement/ReplacementSystem.Limits.cs`,
`ReplacementEffectTests.PlayerLimits.cs` asserts it).

That is wider than the rules take it. CR 119.8 stops the life loss, not the dealing: the damage
still happens, so "whenever a source deals damage to a player" should still trigger and lifelink
(CR 702.15b) should still gain. Under the drop, none of that fires.

The record that would close it — the life loss as its own event, folded beside the damage:

```csharp
// Events/GameEvent.cs: DamageDealt.Apply stops touching Life, and whoever deals damage to a
// player emits the LifeChanged that CR 119.2 makes of it, through the replacement system, so a
// player's limits are read on the life half alone.
```

Then `Forbidden`'s `DamageDealt` arm goes back to `CantBeDealtDamage` only (CR 120.3, 702.16j),
and `LifeTotalCantChange` bites on the `LifeChanged` it already bites on. It is an event-shape
change plus a damage-dealing caller, so it is named here rather than made here. Until it lands,
"can't" wins (CR 101.2) and the whole event is dropped.

## 3. What targeting and casting must read for CR 702.16 on a player

The brief asked me to say it, and both are other agents' files.

- **Targeting, CR 702.16c with 115.6.** `ICharacteristics.PlayerRestrictions(state, player)
  .CantBeTargeted` is already the union of the card-text flag (`PlayerRestriction.BeTargeted`) and
  protection from everything, so **the query's target legality has one thing to read and no
  special case for protection**. It has to be read twice: where targets are chosen (CR 601.2c) and
  again on resolution (CR 608.2b), because the protection can arrive between the two and a spell
  whose only target has become illegal doesn't resolve.
- **Enchanting, CR 702.16d.** Two halves, and only one of them is mine. The state-based action is
  done: `EnchantsAPlayer` refuses a protected player before it looks at the enchant ability, so an
  Aura attached to one is buried by 704.5m however wide its filter is (protection from everything
  is protection from each object, CR 702.16b, so their own Aura is refused too). **The casting
  system's half is not**: `CastPlan`/`ICastingSystem` should refuse an Aura cast *at* a protected
  player at target-choosing time, reading the same `CantBeTargeted`, so the Aura is never cast
  rather than cast and buried on the settle.

## 4. The view carries the protection and nothing else of CR 613.10

`Views/GameView.cs`: `PlayerView.ProtectionFromEverything`, filled by the projector from
`PlayerRestrictions`, asserted in `HiddenInformationTests`. It is public (CR 400.2) and it is the
one limit whose effect a table sees on every damage event, which is why it is there and the other
four fields of `PlayerLimits` are not. If a client needs "can't lose" or a frozen life total
drawn, add them then — a field the view does not need is a field it does not carry.

## 5. Not asked for: `PlayerLimits.LifeTotalCantChange` cites a rule that does not say it

`Characteristics/PlayerLimits.cs` is not mine. Its `LifeTotalCantChange` and `CantGainLifeNow`
carry `[RuleRef("119.3")]`, and 119.3 is "if an effect causes a player to gain or lose life, that
player's life total is adjusted accordingly" — the opposite reading. The comprehensive rules
contain no sentence "life total can't change"; it is card text, and its two halves are **CR 119.7**
(can't gain: no exchange upward, a life-gain cost can't be paid, a replacement that would replace
the gain does nothing) and **CR 119.8** (can't lose: no exchange downward, a life payment can't be
paid). `ReplacementSystem.Limits` now cites those two. Whoever next touches `PlayerLimits.cs`
should move its refs with it.
