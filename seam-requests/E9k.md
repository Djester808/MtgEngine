# E9k — one road for a delayed ability, and the player half of the resolution re-check

Wave 12. **No seam change is asked for.** Everything below landed inside
`MtgEngine.Rules/Interpreter/**` and the interpreter's own test files. Rules suite: 768 tests,
all green (763 at the wave-11 tip, plus six of mine, less the one test that pinned the road that
is now gone).

## 1. CR 603.7 — the fallback is deleted, and the front door is tested

`EffectInterpreter.EffectOf` no longer reads `state.DelayedTriggers` for a negative
`StackEntry.AbilityIndex`. E6e made the firing carry its ability onto the stack object's
definition at an appended index (CR 603.7a), so `AbilityIndex` is never negative for a delayed
firing any more and the fallback was a second road to the same instruction — one that could only
ever have answered for a repeating ability, because CR 603.7b removes a once-only one from the
state as it fires. `EffectOf` lost its `GameState` parameter with it; the three call sites
(`Resolve`, `Continue`, `Retarget`) pass the object and the entry.

`InterpreterTests.Delayed.cs` plays the case through `Game.Apply` rather than by handing the
interpreter a stack entry: a delayed ability with **no source object at all** fires on a life
loss, the trigger system stacks it, both players pass, and the body gains the life. That is the
join E6e and the interpreter make together, and it is the only test in this anchor that needed a
whole game to say anything.

`EffectInterpreter.Resume.cs`'s `Tree` still reads `state.DelayedTriggers`, and that reading is
**not** dead: it is how a question asked by an effect run *off* the stack (`Run`, CR 614.1a) finds
its tree again, where there is no stack object and so no index. Nothing was changed there.

## 2. CR 608.2b — a player target is re-checked at resolution

`LegalTargetPlayers` is `LegalTargets`' sibling: it drops a player whose limits say they can't be
targeted by this object's controller, and `Resolve` counts the survivors with the objects when
CR 608.2b asks whether every target is illegal. The two prohibitions are read exactly as
`CastingSystem.RequireLegalPlayerTargets` reads them at announcement, from the same point of view,
so the two roads cannot disagree about who may be named:

- `PlayerRestriction.BeTargeted` is the card-text form ("by spells or abilities your opponents
  control"), so it is read from the resolving object's controller and does not stop them naming
  themselves;
- `PlayerLimits.ProtectionFromEverything` (CR 702.16j) carries no such qualifier, so no source
  targets through it, its own controller included.

`PlayerLimits.CantBeTargeted` merges the two, so — as E3i wrote — it is not the property to read
here. A gift's promised player (CR 115.10) travels on `StackEntry.TargetPlayers` and is not a
target: they are neither re-checked nor counted, which is the same skip `RequireLegalPlayerTargets`
makes for `CastPlan.GiftPromisedTo`.

The re-check happens in `Continue` as well as `Resolve`, because a resumed resolution rebuilds its
context from the entry and already re-checked the object half there.

`Query.Targets.cs`'s `MatchesAnyTarget(GameState, PlayerId)` is still uncalled: E3i suggested it
could be this reading once it takes the source's controller, but it would have to grow that
overload and then answer the same two prohibitions separately, so this box read
`ICharacteristics.PlayerRestrictions` directly and the static stays E0's to delete or extend.

## 3. Recorded, not asked for: the choice kinds that still have no constant

`ChoiceKinds.Value` existed already, so the two `"value"` literals in
`EffectInterpreter.Structure.cs` and `EffectInterpreter.Backlog.cs` are now that constant. Three
kinds the interpreter raises still have no constant and are spelled as literals, one place each —
for **E4g** (wave 12) or whoever owns `Choices/`:

| Kind | Where it is raised |
|---|---|
| `"may-pay"` | `EffectInterpreter.Backlog.cs`, the `MayPay` arm |
| `"may-cast"` | `EffectInterpreter.Retarget.cs`, the `MayCast` arm |
| `"cast-cost"` | `EffectInterpreter.Retarget.cs`, the `CastCost` private const |

Nothing turns on it: the strings are what the state carries and a constant would not change a
single answer. When they land, `Retarget.cs`'s own `CastCost` const should go with them.

## 4. No assertion of anyone else's flips

The whole Rules project is green on my branch, so nothing in `WaveNineIntegrationTests*` or any
other anchor was asserting either behaviour the old way. No file outside my folder was touched.
