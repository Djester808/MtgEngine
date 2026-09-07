# Seam decisions after wave 1

Every request a wave-1 builder filed, with the coordinator's decision. "Now" means applied in
the seam commit that follows the wave-1 merges; "wave 2" means the builder named will do it
with the seam already in place; "no" means the workaround stands and why.

| From | Request | Decision |
|---|---|---|
| E1 | `ObjectDrawn(PlayerId, ObjectId)` event, folded into `ObjectsDrawnThisTurn` | **Now.** Added to `Events/GameEvent.cs` and `events.txt`; `ZoneSystem.Draw` emits it after the move. |
| E1 | `GameSetup.StartingPlayer` | **Now.** `int StartingPlayer = 0`; `Create` deals that seat first and `Begin` starts with it. |
| E1 | `IZoneSystem.MoveAll` for simultaneous zone changes | **Now, as a default interface method** that loops `Move`, so E4 and E5 can call it today; E1's batch semantics (one event per object, one board for all of them) land in wave 2 with the leaves-the-battlefield triggers that need them. |
| E1 | `CreateToken` with a `CombatTarget?` instead of a bool | **Wave 2 (E9).** The interpreter is the only caller that knows the defender; the seam changes when it is written. |
| E2 | three `events.txt` lines | **At E2's merge.** |
| E2 | `PriorityGranted` should keep the succession record | **No.** `PriorityGranted` means a fresh round (step start, resolution) and clearing is right there; `PriorityHandedTo` is the mid-round handoff. Two events, two meanings; both stay. |
| E2 | `TurnBegan` never sets `TurnOrderIndex` | **Now.** `TurnBegan.Apply` sets it from the active player's seat. |
| E2 | untap reads the stored controller | **Wave 2.** `ITurnSystem` takes `ICharacteristics` once E7 is wired; E2's slice 2. |
| E3 | `EmptyPools` on `ICastingSystem` | **No.** E2's turn system already empties pools at each step end with its own `ManaPoolEmptied` events; two writers of the same rule is the drift the plan forbids. E3's method stays as a helper and is not on the seam. |
| E3 | `Move` cannot carry a `StackEntry` | **No, for now.** `StackZone` building the stack move itself is acceptable; revisit if a second caller appears. |
| E3 | `AbilityActivated.StackObject` nullable | **Now.** Null means the ability did not use the stack (CR 605.3a); `CastingSystem` passes null for mana abilities. |
| E5 | `GameObject.DealtDeathtouchDamage` + a way to clear it | **Now.** `DamageDealt` gains `bool Deathtouch = false` and sets the flag; `ObjectChanged` gains `bool? DealtDeathtouchDamage` to clear it. E4 sets it from the source's characteristics; E5 implements 704.5h and clears after reading. |
| E5 | `DrewFromEmptyLibrary` never cleared | **Now.** `ObjectChanged` is for objects; `PlayerLost` is where it stops mattering. The SBA clears it with a `PlayerFlagCleared` event when the loss is applied, so a replaced loss (wave 2, E8) does not repeat. |
| E5 | Auras attached to players | **Wave 2.** `AttachedToPlayer` on `GameObject` when the first "enchant player" card is translated. |
| E5 | format not in state | **Now.** `GameStarted.Apply` keeps `Format` on `GameState.Format`. |
| E0 | `GameObject.IsCommander` surviving zone changes | **Now.** E1 sets it at creation from `CommandZoneOracleIds`; `ObjectMoved` carries the new object, so the flag travels with it. |
| E0 | `GameState.TurnHistory` | **Now.** The record E0 specified, folded by `ObjectMoved` (entered, died, left), `SpellCast`, `AttackersDeclared`, `DamageDealt`, and reset by `TurnBegan`. |
| E0 | `StackEntry.ManaSpent`, `CastFrom`, optional costs | **Now, as fields with defaults.** E3 fills them in wave 2. |
| E0 | designations, monarch, initiative, city's blessing | **Now, as state.** `GameObject.Designations`, `GameState.Monarch`, `GameState.Initiative`, `PlayerState.Designations`; the effects that set them are wave 2's interpreter. |
| E0 | `EffectContext.RememberedPlayers` | **Now.** |
| E0 | subtypes are a disjunction | **Accepted as the semantics.** The translator prompt says so; a conjunction is two filters. |
| E7 | wire `LayerSystem` | **Now.** |
| E7 | `StaticAbility.While` needs the query layer | **Wave 2.** `LayerSystem` gains a settable condition evaluator the coordinator wires to `IQuery.Holds` after both exist; until then `While` statics are skipped, which is the honest behaviour. |
| E7 | amounts arrive pre-evaluated | **Accepted.** E9 evaluates every `Amount` into `Fixed` when it creates an `ActiveEffect`. |
| P1, P3 | `SmokeTests.cs` unnecessary using | **Done** (coordinator's defect). |
| P3 | `Pilot.Evaluate` lints a single reading as `Disputed` and can never agree | **Now.** The pilot lints readings as `Translated`; the pilot numbers gathered before this were wrong by construction, and no decision was read from them. |
| P1 | the prompt is duplicated between `Translator.cs` and `Translate/TranslationPrompt.cs` | **Now.** The pilot uses the stage's prompt; `Translator.cs` keeps only what the pilot still needs until it is retired. |
