# Seam decisions after wave 1

Every request a wave-1 builder filed, with the coordinator's decision. "Now" means applied in
the seam commit that follows the wave-1 merges; "wave 2" means the builder named will do it
with the seam already in place; "no" means the workaround stands and why.

Every "Now" row landed in commit `0f47563` ("Seams after wave 1"), tested by
`tests/MtgEngine.Rules.Tests/SeamTests.cs`; P5's two requests are in `seam-requests/P5.md`, the
first applied in `588d4a8`, the second accepted as written. Wave-2 requests go in a second table
below when their builders file them.

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

# Seam decisions after wave 2

Every request a wave-2 builder filed (`seam-requests/E6.md`, `E8.md`, `E9.md`, `E12.md`,
`E13.md`, `E14.md`, `E15.md`, `EINT.md`, `V1.md`). "Now" landed in the "Seams after wave 2"
commit, tested by `tests/MtgEngine.Rules.Tests/WaveTwoSeamTests.cs`; "wave 3" names the
builder that does it with the seam in place.

| From | Request | Decision |
|---|---|---|
| E9, E6 | `events.txt` lines (ContinuousEffectCreated, DelayedTriggerCreated, ExtraTurnGranted, TriggerFired) | **Done** at their merges. |
| E9, E6 | `StackEntry.Remembered`, `RememberedPlayers`, `Triggering` | **Now.** Defaulted fields; the trigger system fills them when it stacks a trigger, the interpreter reads them into the context (CR 603.3d). |
| E9 | `CreateToken` taking a `CombatTarget` | **No.** The vocabulary carries only `bool Attacking`; the wave-1 row E1 is closed. |
| E6 | `ObjectMoved` carries the departed object | **Now.** `GameObject? Departed = null`; `ZoneSystem.Move` and `Draw` set it (CR 603.10 look-back; `ZoneChange` from a non-battlefield zone can match now — E6's matcher reads it in wave 3). |
| E6 | `trigger-order` choice halts the game | **Accepted.** CR 603.3b is a choice; it resumes through `IResumable`. Revisit if tables find it noisy. |
| E6 | events for the ~30 triggers with nothing to match | **Wave 3, as they are needed.** Each comes from the subsystem that performs the action. |
| E15 | `Layers.Printed` reads `FaceRules.ActiveFace` / `FaceDown` | **Now.** |
| E15 | casting chooses among `CastableFaces`, the stack carries the chosen face, `ResolvesTo` | **Wave 3 (E3b).** |
| E15 | interpreter verbs Transform/Convert/Flip/Meld, copies and copy tokens | **Wave 3 (E9b).** |
| E15 | `ObjectTurnedOver(Object, FaceIndex, TurnKind)` event | **Now.** Folds `FaceIndex` and `FaceDown`. |
| E15 | melded permanent components | **No, for now.** When a meld card is translated. |
| E12 | `IsThisObject`, `DiscardSelfCost`, `AddTypes.AllCreatureTypes` in the vocabulary | **Done by V1.** |
| V1 | `Query` answers `IsThisObject`; `CostPayment` pays `DiscardSelfCost` | **Now.** |
| V1 | `Layers` applies `AddTypes.AllCreatureTypes` | **Wave 3 (V2).** It needs the creature-type list of CR 205.3m generated from `comprehensive-rules.txt` like the keyword enums, so the engine holds rules data and no card data; the flag then lives on `Characteristics` and the query reads it. |
| E12 | `CostModifier` matching this spell (affinity) | **No.** Casting reads the affinity keyword directly (wave 3, E3b); affinity moves to `Expands: false`. |
| E12 | riot is a replacement | **Noted.** E8 runs it once the expander emits a `ReplacementAbility`. |
| E8 | `ActiveEffectChanged(int Id, ContinuousEffect? Effect)` | **Now.** Null ends the effect. |
| E8 | the proposed event beside a `replacement-order` choice | **Now.** `GameEvent? Event = null` on `ChoiceContext`. |
| E8 | `Shuffled` distinct from `ZoneReordered` | **Now.** `bool Shuffle = false` on `ZoneReordered`, set by `ZoneSystem.Shuffle`. |
| E13 | hoist `IResumable`/`Resumption` into `Engine/Seams.cs`; the interpreter implements it | **Now** for the hoist; the implementation is **wave 3 (E9b)**, after which `Subsystems.Wave2()` passes the interpreter to the choice system. |
| E13 | `ITurnSystem.OpenHands` (CR 103.5) from `Game.Create` | **Now on the seam, wave 3 (E2b) in the turn system.** A default returns the state unchanged; `GameSetup.OfferMulligans = false` until E2b, which also begins turn 1 after the last keep. |
| E13 | bottoming is one `ObjectMoved` per card | **Accepted.** |
| E14 | `ICombatSystem.CouldAttack`/`CouldBlock` | **Wave 3 (E2b, with the combat folder).** One reading of CR 508.1a. |
| E14 | wire `ViewProjector` and `LegalActions` | **Now**, in both `Wave1()` and `Wave2()`, with an `enforced` lookup the session supplies. |
| EINT | `Wave1()` passes the combat system to `TurnSystem` | **Now.** The test fixture's rewrap is gone. |
| EINT | `ObjectId`/`PlayerId` as dictionary keys in the log | **Now.** Ids are bare numbers in the log; the exit test round-trips the whole log. |
| EINT | CR 510.4 first-strike step always walked; CR 704.3 SBA after `Advance` | **Wave 3 (E2b).** |
| coordinator | `Game.Settle` never saw the command's own events, so a cast spell's triggers were never collected | **Now.** `Settle(state, since)` collects them first (CR 603.3); `WaveTwoSeamTests` plays an enters-the-battlefield trigger to resolution through `Wave2()`. |
