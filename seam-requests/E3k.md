# E3k — seam requests (wave 16: the casting system reads the two V9 permission flags)

## 1. The interpreter has no `MayPlay` arm, so hideaway's play never reaches the casting system (V9 row 1, CR 702.75a)

`MayPlay` (`MtgEngine.Cards/Effects.Verbs.cs:217`) is read nowhere under `MtgEngine.Rules/`: a grep for
`MayPlay[^F]` over the project finds only `MayPlayFromZone`. The may-cast road
(`Interpreter/EffectInterpreter.Retarget.cs`, `Permission(run, may, cards, until)`) turns a `MayCast`
with an `Until` into a lasting `MayPlayFromZone` for the cards the resolution named, from the zone they
are in now, carrying `WithoutPayingManaCost`; nothing does that for `MayPlay`. So the permission the
brief asked me to find does not exist yet, and the casting half is implemented against the record it
will be: `MayPlayFromZone`, which is the one permission `CastPlan.Permission` reads for both a
`CastSpell` and a `PlayLand` (`CastingSystem.Playable`).

**Ask (E9-series, `Interpreter/**`)**: a `MayPlay` arm that does what `Permission(...)` does for
`MayCast` — `new MayPlayFromZone(may.Who ?? new You(), from, WithoutPayingManaCost: may.WithoutPayingManaCost)`
applying to the objects `What` resolves to and to the player, for `may.Until ?? new UntilEndOfTurn()` —
and never a cast on the spot: "play" includes a land, and a land is a special action the player takes
with `PlayLand` (CR 305.1, 116.2a), so the permission has to outlive the resolution for either kind of
card. Nothing else is needed on the casting side: `CastPlan.Alternative` waives the mana cost off
`WithoutPayingManaCost` (wave 8), `PlayLand` spends the turn's land play whatever the permission says
(CR 305.2), and both are asserted this wave in `tests/MtgEngine.Rules.Tests/CastingTests.PermissionCosts.cs`.

## 2. Nothing else

- `MayPlayFromZone.TopOfLibraryOnly` is now honoured in `CastPlan.Permission` rather than granting
  nothing: `GameState.Zones` keeps every library top-first, so the permission holds for the first card of
  the caster's library and for no other. This is what `cards/2bd111bb-ce02-414c-b5b7-e0e037d8d96b.json`'s
  permission needs once its third ability is re-read as the `PayLifeEqualToManaValueInstead` rider (V9 ask 5).
- The life rider is recorded on the stack entry's `OptionalCostsPaid` as `"life"`
  (`CastPlan.PaidLifeForManaValue`), beside `"free"` for a waived cost. A query condition that wants
  "if it was cast this way" can read it there; none is asked for now.
