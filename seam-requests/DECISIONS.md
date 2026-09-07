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

# Seam decisions after wave 3

Requests from `seam-requests/E9b.md`, `E8b.md`, `E3b.md`, `E2b.md`, `V2.md`, `EINT2.md`, `P4.md`,
`E14.md` (carried over) and C2's report. "Now" landed in "Seams after wave 3".

| From | Request | Decision |
|---|---|---|
| E9b, EINT2 | `Wave2()` passes the interpreter to `ChoiceSystem` | **Now.** A card's question resumes through `IResumable`; the wave-2 exit test answers a "may" and draws. |
| coordinator | a resolution that stopped to ask was moved off the stack anyway | **Now.** `ResolveTop` leaves a halted object on the stack; `ICastingSystem.FinishResolving` does the leaving (608.2m/n) and the choice system calls it when an answer finishes the walk. |
| E2b | `ITurnSystem.AfterMulligans` and the call in `ChoiceSystem.Resume` | **Now.** The mulligan fixture's decorator is gone; `MulliganTests` run on `Wave2()`. |
| E2b | `TurnSystem` takes `ICharacteristics` | **Now.** Both compositions pass the layers: CR 510.4's first-strike step only when needed, CR 502.3 untap by computed controller. |
| E2b, E14 | `ICombatSystem.CouldAttack`/`CouldBlock` | **Now on the seam** (defaults throw; `CombatSystem` implements). `LegalActions` still has its own reading — **wave 4** switches it to the seam. |
| E2b | CR 103.5 simultaneity of mulligans | **No, for now.** One player at a time; revisit if a table notices. |
| E3b | `PlayLand.FaceIndex` + `IZoneSystem.Move(faceIndex)` for modal land faces (712.12) | **Wave 4 (E1b).** |
| E3b | multikicker count, escape's exile half, convoke/improvise/delve channel on `CastSpell` | **Wave 4 (V3 vocabulary, E3c casting).** |
| E9b | `IZoneSystem.CreateToken` taking copiable values; `GameObject.Components` for meld; a `Flip` effect record; a question asked outside a resolution; a halting verb's progress marker | **Wave 4** (E1b, state, V3, E9c). The copy-token arm emits `ObjectCreated` directly meanwhile. |
| EINT2 | **nothing offers a zone change to the replacement system** (614.1c): every "enters tapped / with counters / instead" is dead code in a game | **Wave 4, first (E1b).** `ZoneSystem` proposes its `ObjectMoved` (and `Draw` its `ObjectDrawn`) to `IReplacementSystem` through a handle set after construction, as the interpreter already does for damage; the interpreter proposes counters, life and tokens (E9c). |
| V2 | `StateBasedActions.Permanents.HasSubtype` and `ViewProjector` ignore `EveryCreatureType` | **Wave 4** (small; E5b, E14b). |
| V2 | `MtgEngine.Api` did not compile at `4e97e3f` | **Done** in `7e751ca`. |
| P4 | `Program.cs` registration of `CardImplementationService` | **Now.** |
| P4 | the pipeline cannot reach `EventLogSerializer.EngineHash()` | **Accepted**: the verify stage takes the hash as an argument; the `pipeline verify` command (wave 4, P7) passes it. |
| C2 | `ImplementationStatus` serialised as the enum name; coverage's disputed shape; a batch implementations endpoint | **Now for the names** (the API's JSON options already write enum names — verify at the first live render); batch endpoint **wave 4 (P7)**. |
| E9b | `tools/build.sh` appended `.Tests` to a test project's own name | **Now.** |

# Seam decisions after wave 4

Requests from `seam-requests/E1b.md`, `E9c.md`, `E14b.md`, `V3.md`, `P6.md`, `P6b.md` (P7's when it
lands). "Now" landed in "Seams after wave 4" (`9dc935d`).

| From | Request | Decision |
|---|---|---|
| E1b | `Wave2()` sets `zones.Replacements` | **Now.** Every move, draw and token is offered to the replacement system in a game (614.1c, 616.1). |
| E1b | `IZoneSystem.Move` with a face index (712.12) | **Now, as a second interface member** with every parameter required and a throwing default, so a three-argument call still binds to the old form and no fake changes; the zone system implements it. |
| E1b | `IZoneSystem.CreateToken(CopiableValues, …)` | **Now**, the same way. The interpreter's own copy-token arm becomes a call to it — **wave 5 (E9d)**. |
| E1b | `EffectInterpreter.Propose` never set `Halted` | **Done by E9c** in the same wave. |
| E9c | `IInterpreter.Run(…, int? abilityIndex)` | **Now, as a second member** defaulting to the three-argument walk; the interpreter keeps the index, the forwarding handle forwards it, and the replacement system passes the index of the `ReplacementAbility` it is running. |
| E9c | `PutPlayerCounters` and `AddMana` are not proposed | **Accepted.** No `ReplacedEvent` matches the first; mana needs both emitters to change together — **wave 5** with the mana subsystem. |
| E14b | `LegalActions` takes the combat system | **Now**, in both compositions. |
| V3 | retire `ManaProduction.Restriction`/`RestrictedTo` now that `AddMana.SpendOnlyOn` exists | **Wave 5.** The reference tells the translator to use `SpendOnlyOn`; the linter warns on the free-text field; the two pilot cards that carry prose there are re-translated when credits allow, not migrated by hand. |
| V3 | `PlayerCant` overlaps `CantLoseGame`/`CantGainLife`/`CantWinGame` | **Keep the three specific records as the spelling** for those three restrictions; the canonicaliser collapses `PlayerCant` of one of them into the specific record (**wave 5, P6c**); `PlayerCant` stays for the rest. |
| V3 | evaluator arms owed: query (`TimesKicked`, `InCombat`, `AttackedThisTurn`, `Devotion.ChosenColorKey`, `GiftPromised`, `IsPrepared`), casting (`PayManaCost`, `Repeatable`, `SpendOnlyOn`, `PayOnlyWith`, `KeywordAbility.Costs`, the gift promise), interpreter (`PayCost`, `Search.Zones`, `Prepare`/`Unprepare`, `DistributeCounters`, `ChangeTarget`, `Flip`), layers and SBA (`PlayerCant`, `PlayerProtectionFromEverything`) | **Wave 5** (E0b, E3c, E9d, E7b/E5c), one builder per folder, the pilot's Unsupported reasons as their test list. |
| P6, P6b | an `AnyTarget` spelling in the vocabulary | **Wave 5 (V4)**, with the canonical collapse P6b wrote down. |
| P6 | the pilot prints `AgreeReport.Of(results).Text()` | **Wave 5 (P7 or P8)**; the pilot files belong to P7 this wave. |
| coordinator | the 200-face agreement after two canonical passes is 87/200, 54% among both-clean; the rest is translator structure (step splitting, remember-versus-target, condition construct, unsupported-versus-real) | **The 80% bar is judged on canonical agreement after a re-translation with the "one spelling" rules now in the reference**, which needs API credits; no vocabulary redesign. |

# Seam decisions after wave 5

Requests from `seam-requests/E0b.md`, `E3c.md`, `E7b.md`, `E9d.md`, `V4.md`, `P8.md`, and C3's
report. "Now" landed in "Seams after wave 5".

| From | Request | Decision |
|---|---|---|
| E7b | `ICharacteristics.PlayerRestrictions` | **Now**, as a default of none; the interim interface is gone. |
| E7b | the replacement system drops damage and life changes a player's limits forbid | **Wave 6 (E8c).** |
| E3c | `CastSpell.CostObjects` / `ActivateAbility.CostObjects` (CR 601.2c) | **Now on the commands**; the casting system reads them in **wave 6 (E3d)**, refusing a choice it was not given meanwhile. |
| E3c | restricted mana on the pool (`AddMana.SpendOnlyOn`, CR 106.6a) | **Wave 6 (E3d, with the State seam it names).** Until then restricted mana is ordinary mana — the one gap that refuses nothing; it is written here so it is not forgotten. |
| E3c | `StackEntry.GiftPromisedTo` (CR 702.174b) | **Now.** The casting arm records it in wave 6 (E3d); the query reads it now. |
| E1b, E3b | `PlayLand.FaceIndex` (CR 712.12) | **Now on the command**; casting passes it to the face-index move in wave 6 (E3d). |
| E9d | `ObjectDesignated` and `StackTargetsChanged` events | **Now.** Prepare/unprepare, suspect, goad, monstrous, ring-bearer and `ChangeTarget` get their arms in **wave 6 (E9e)**. |
| E9d | `ICastingSystem` as a settable handle on the interpreter for `MayCast` (CR 601) | **Wave 6 (E9e)**, the handle pattern of `ForwardingInterpreter`, set in `Wave2()`. |
| E0b | `TurnHistory.Died`/`LeftBattlefield` keep only ids, so morbid and revolt are unqualified counts | **Wave 6 (state).** The history keeps the departed object (it is already on `ObjectMoved.Departed`). |
| E0b | damage this turn is totalled per source and per receiver, never per pair | **Wave 6 (state)**, a per-pair map. |
| E0b | `FirstTimeThisTurn` needs a per-turn resolution tally (also `OncePerTurn`) | **Wave 6 (state + E6b)**. |
| E0b | `Raid`/`AttackedThisTurn` attribute attackers to the active player | **Accepted** (CR 508.1). |
| V4 | `AnyTarget` needs a query arm and the damage arm reads whichever half the stack entry holds | **Wave 6 (E0c + E9e)** — and it matters now, because the canonical card files carry `AnyTarget`. |
| V4 | `PlayerRestriction` has no Win/GainLife member | **Accepted**; the specific records are the spelling. |
| P8 | a smoke scenario cannot answer the question a search asks; an Aura has nothing to enchant | **Wave 6 (P9)**: an "answer with the first legal option" step, and an enchant target placed from the Aura's `Enchant` filter. |
| C3 | `VerificationDto.WithoutScenarios`; coverage keys stay enum names; filtered coverage answers the whole DTO | **Wave 6 (P9)** for the field; the other two are confirmed as they are. |
| coordinator | the first full verify run over the 87 agreed pilot cards, on P8's base: 55 verified, 6 failed, 24 without a scenario, 2 crashed | **Recorded**; re-run on the wave-5 tip: 57 verified, 6 failed, 24 without a scenario, 0 crashed, committed with the card files. |

# Seam decisions after wave 6

Requests from `seam-requests/E8c.md`, `E0c.md`, `E9e.md`, `E3d.md` (P9's when it lands). "Now"
landed in "Seams after wave 6" (`44b83fe`).

| From | Request | Decision |
|---|---|---|
| E0c | `TurnHistory` keeps the departed `GameObject` for `Died`/`LeftBattlefield` | **Now.** Every reader retargeted (triggers, the seam test); morbid and revolt are qualified. |
| E0c | a per-pair damage map `DamageDealtToPlayer` | **Now.** |
| E0c | a per-turn resolution tally `AbilitiesResolvedThisTurn`, folded by `StackObjectResolved` | **Now.** `FirstTimeThisTurn` answers; `TriggeredAbility.OncePerTurn` reads it in **wave 7 (E6b)**. `TurnFacts` in the query is now redundant — delete it in wave 7. |
| E3d | `ManaPool.Restricted` and the `ManaChanged` fold (CR 106.6a) | **Now.** The two casting-side pieces (`ManaFrom` emitting a block, `ManaSymbols.Pay` reporting the block that paid) are **wave 7 (E3e)**. |
| E9e | `Wave2()` sets `interpreter.Casting` | **Now.** |
| E9e | `CastSpell.WithoutPayingManaCost`, `CastPlan` recording `"free"` | **Now on the seam**; the may-cast arm's switch to it and its test's flip are **wave 7 (E9f)**. |
| E9e | `RingTemptsYou`'s emblem has no spelling | **Wave 7 (V5)**: an `Emblem` effect record with the Ring's four abilities. |
| E8c | combat damage never reaches the replacement system | **Wave 7 (E4b), first**: a settable `IReplacementSystem` handle on `CombatSystem`, the per-event propose loop in `DealCombatDamage`, lifelink after the damage actually dealt (702.15b), set in both compositions. |
| coordinator | `GameEvent.cs` passed 500 lines | **Now**: the object-state events (`ObjectTurnedOver`, `ActiveEffectChanged`, `ObjectDesignated`, `StackTargetsChanged`) live in `Events/ObjectStateEvents.cs`. |
