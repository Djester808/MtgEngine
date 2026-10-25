# Two player Magic execution checklist

This is the implementation checklist for an ordinary two-player game, with optional formats called out separately. Rule references use the repository's Comprehensive Rules effective August 7, 2026. It describes required behavior and the observed audit status, rather than certifying the engine against every subrule. The complete numbered-rule index is [RULES_REGISTER.md](RULES_REGISTER.md); individual audit reproductions are in [results/probes.json](results/probes.json).

Status meanings: **Demonstrated** means an identified executed test/probe establishes a bounded behavior; **Partial** means code and some passing tests exist but a material gap remains; **Failed probe** means a saved independent check observed different behavior; **Unassessed** means no sufficient conformance check was completed. Nothing is considered complete merely because it has a class or a rule citation.

## 1 Before the first turn

| Requirement | Rules | Current assessment and needed work |
| --- | --- | --- |
| Choose the game format and enforce its construction rules | 100, 903 where applicable | Partial. Format and starting life are inputs. The game-session path admits cards by implementation status; format-specific deck legality is not established by that check. Test deck size, copies, card legality, sideboards and variant restrictions separately. |
| Establish exactly two seats, ownership, starting life and relevant designations | 102, 103, 104 | Basic records exist and integration suites pass. Product session code permits more than two seats; validate the selected format rather than assuming every GameSetup is a legal game. |
| Determine starting player and handle subsequent games in a match | 103.1 | StartingPlayer is an input to GameSetup. A complete play/draw decision and match-level process is unassessed. |
| Put decks in libraries and format-specific cards in the correct zones | 103.2, 401, 903 | Basic creation, commander designation and zones are implemented. Companion, conspiracy and other optional pregame effects require separate coverage. |
| Shuffle with reproducible randomness for replay | 103.3, 401 | Shuffle and RNG state exist. Existing tests pass; fairness of all random operations and cross-version replay are not certified. |
| Draw opening hands | 103.4 | Demonstrated through creation/mulligan tests for the supported path. Opening-hand size is configurable. |
| Offer London mulligans in the correct declaration rounds | 103.5 | Partial. Mulligan/bottoming tests pass, but the implementation explicitly completes one player's mulligans before moving to the next. It does not model the prescribed rounds of declarations and simultaneous mulligans. |
| Bottom the correct number in the selected order | 103.5 | Supported by existing tests. Interactions with replacement opening hands, maximum mulligans and variant free mulligans need a declared scope. |
| Execute pregame actions and effects before turn one | 103.6 onward | Unassessed/partial. Do not infer support for starting-battlefield cards, companions or arbitrary pregame effects from ordinary creation. |
| Begin the first turn and apply the two-player first-draw exception | 103.8a | **Failed probe.** The engine suppresses the first draw action but still enters a Draw step and offers priority. The entire first draw step must be skipped. |

The API's NewGameRequest does not expose the engine's OfferMulligans option and the inspected session creation uses GameSetup's default false. Thus engine-level mulligan tests do not establish a normal product game starts with mulligans.

## 2 The recurring priority checkpoint

This checkpoint occurs whenever someone would receive priority. It is not a once-per-turn action.

1. Finish the current turn-based action or resolving instruction. Do not let players respond halfway through it unless a rule explicitly provides a decision or mana window.
2. Perform applicable state-based actions simultaneously. Repeat until none apply.
3. Put waiting triggered abilities on the stack in active-player/nonactive-player order. Each controller orders their own simultaneous triggers and makes required modes, targets and other announcement choices.
4. Repeat state-based actions and trigger placement as required before granting priority.
5. Give priority to the player the rules specify. Distinguish retaining priority after an action from receiving it after a resolution or at a new step.
6. Allow the player to take a permitted action or pass. An action breaks the sequence of passes. The next player cannot inherit a pass that happened before that action.
7. If both players pass consecutively with no action between, resolve only the top stack object. If the stack is empty, advance the step or phase.

Rules: 117, 603.3, 704.3. The engine has these subsystems, and ordinary passing/top-object resolution is demonstrated. **A response after an earlier pass does not reset the pass history correctly.** A mana activation also fails the saved pass-history check. These are immediate core blockers.

The exact state-based-action/trigger ordering also requires an interaction audit: Game.Settle collects command triggers before its SBA loop and TriggerSystem.Collect immediately places triggers. Source inspection identifies an ordering risk; this pass did not prove every consequence of that risk. Distinguish collecting a trigger when it happens from placing it when priority would be granted.

## 3 Beginning phase

| Step or responsibility | Rules | Assessment |
| --- | --- | --- |
| Untap step: phase objects in/out as required, determine which permanents untap, apply restrictions | 502, 702.26 | Untapping and phasing-related code/tests exist. Optional untapping, phasing interactions and all continuous restrictions need targeted cases. |
| No priority in untap; defer waiting triggers | 502.4, 117.3a | Ordinary no-priority behavior is tested. Complete deferral of all untap triggers is unassessed. |
| Upkeep: create beginning-of-upkeep triggers, settle, grant priority | 503 | Basic phase/step traversal and trigger machinery exist. Mixed APNAP, source departure and intervening-if cases need stronger integration checks. |
| Draw step: draw as a turn-based action, then settle and grant priority | 504, 121 | Drawing and empty-library loss have tests. First-turn skipping is wrong at step level, as noted above. Replacement-trigger ordering during a draw needs explicit coverage. |

Unspent mana normally empties at step/phase boundaries, with preservation effects applied. There are tests and explicit mana-pool code; do not use the passing pool-emptying tests to excuse a step ending prematurely after a mana activation.

## 4 Precombat main phase

Players alternate opportunities under the priority checkpoint. The active player may play a land when permitted and cast sorcery-speed spells only when timing permits and the stack is empty. Instants, flash permissions and activated abilities follow their own timing restrictions.

Land play is a special action: it does not use the stack. Check whose turn it is, priority, permitted zone/face and land-play allowance; carry the permission that allowed a non-hand land play. Triggered abilities resulting from the land entering still need normal trigger placement. The engine implements and tests ordinary land plays and extra land permissions, but action enumeration currently begins with the hand and is not a complete description of the executor's supported permissions.

## 5 Casting one spell

Each of these responsibilities needs tests; serializing a CastSpell command does not establish them all:

| Responsibility | Rules | Assessment |
| --- | --- | --- |
| Check permission, timing, player and castable face | 601.2, 601.3 | Implemented across casting/face/permission code, with many passing tests. Non-hand offers remain incomplete relative to execution. |
| Move the card to the stack as a new object and identify the spell | 601.2a, 400.7 | Basic spell stack identity demonstrated. Audit ordering relative to additional costs and generated triggers; the implementation plans/pays costs before emitting the stack move. |
| Announce modes, X, alternative/additional costs and relevant choices | 601.2b | **Failed probe:** a required modal spell is accepted on the stack with no modes and no pending announcement choice. Conditional mode bounds are implemented, but bounds do not repair missing announcement timing. |
| Choose every target and validate target groups | 601.2c, 115 | Target filters, ranges and player restrictions exist. One independent targeted cast and matching offer passes. Cross-group relationships and every player/object combination remain unassessed. |
| Announce divisions/distributions required during casting | 601.2d | Effect/distribution types exist. No complete player-facing announcement contract is demonstrated. |
| Check that the proposed cast is legal with announced choices | 601.2e | Existing validation covers a subset of cases; modes and other omissions require repair. |
| Compute and lock the total cost in the correct order | 601.2f, 118 | Alternative/additional costs, reductions and restrictions have tests. Do not equate a generated deep-mana-pool smoke test with cost correctness. |
| Permit mana abilities in the casting payment window | 601.2g, 605 | The public command accepts preselected costs and pays atomically; a general interactive mana window is not represented by that command shape. |
| Pay all costs, with legal choices and source provenance | 601.2h, 118 | Cost objects, mana provenance and restrictions exist. Illegal attempts must commit nothing; nested replacements and partial choices need integration tests. |
| Complete the cast, record cast triggers and retain priority | 601.2i, 117.3c | SpellCast history exists. **Pass-history behavior fails** after a response, so the cast/priority boundary is not conformant. |

## 6 Activating an ability and special actions

For an activated ability, identify the current ability instance, its controller, functioning zone, timing, restrictions, choices and costs. Non-mana activated abilities become independent stack objects; removing the source normally does not remove an already activated ability. Mana abilities require their own classification and resolve immediately without going on the stack.

The engine implements both paths and stores source definition/index information. Stable identity for granted/copied/removed abilities and last-known information need further checks. A mana activation was observed to leave the stack empty, but its pass reset is wrong. Its ability to make additional choices while paying for another action remains partial.

Special actions beyond land play, including face-down-card actions and format-specific procedures, are individually assessed through their corresponding rules; they are not automatically supported by the generic ActivateAbility command. The complete register includes those sections.

## 7 Responses and stack resolution

| Responsibility | Rules | Assessment |
| --- | --- | --- |
| Maintain a shared ordered stack | 405 | **Demonstrated.** Two ordinary spells resolve last-in-first-out. |
| Allow responses while each player gets priority | 117, 405 | **Failed probe** for stale pass history. Response spell B resolves before player A receives the required new opportunity. |
| Resolve one top object after consecutive passes | 117.4, 608 | **Demonstrated** for the ordinary no-intervening-action case. |
| Recheck targets at resolution and distinguish partial from all-illegal targets | 608.2b | Interpreter target checks and tests exist. Full mix of modes, changing controllers, protection and player targets remains unassessed. |
| Recheck intervening-if conditions | 603.4 | Trigger/condition paths exist; cross-path integration needs cases. |
| Perform effects in printed order with immediate instructions and required decisions | 608.2c onward | Sequences, choices, loops and resumption are implemented. Multi-pause and mid-proposal interactions remain a major risk. |
| Do not grant priority or perform ordinary SBA checks between instructions | 117, 704.4 | Requires dedicated full-resolution interaction testing; source presence alone is insufficient. |
| Apply replacement/prevention effects and choose their order where required | 614–616 | Real replacement machinery and existing tests pass. Loop limit behavior and ordering with paused combat/effect batches remain incomplete. |
| Move a resolved spell to its correct destination or create the permanent | 608, 400.7 | Demonstrated by integration and corpus scenarios for supported cases. Alternative destinations, copies and multi-face interactions need the corresponding cases. |
| Remove resolved abilities, collect resulting triggers, settle and give active player priority | 117.3b, 603, 704 | Ordinary cases pass. Complete ordering is unassessed; cleanup and response counterexamples show generic priority tests are insufficient. |

Copying a spell, countering an ability, changing targets, creating tokens and last-known information have implementation files/tests. They remain individual mechanics to validate, not automatic consequences of having a stack.

## 8 Combat phase

| Responsibility | Rules | Assessment |
| --- | --- | --- |
| Beginning of combat triggers, settlement and priority | 507 | Step traversal exists; mixed trigger/permission cases unassessed. |
| Declare attackers as a turn-based action | 508.1 | Attack restrictions, costs, summoning sickness, vigilance and defenders have code/tests. Player-controlled payment windows and complete requirements satisfaction remain partial. |
| Handle attack triggers, then priority | 508.2 | Trigger paths exist; precise timing and suspended costs need integration checks. |
| If no attackers exist, skip blockers and damage steps | 508.8 | **Failed probe.** Empty attacker declaration progresses to DeclareBlockers. |
| Declare blockers, respecting restrictions and requirements | 509 | Code/tests cover many common evasion and blocking cases. Choosing assignments is not proof of all possible mandatory-block combinations. |
| Handle block triggers and grant priority | 509 | Source present; complex placement timing unassessed. |
| Add a first-strike damage step only when required | 510.4 | Basic keyword check implemented. |
| Remember which creatures qualified at the start of that first damage step | 510.4 | **Failed isolated subsystem probe.** A first striker that loses first strike between damage steps assigns damage again. |
| Let players choose legal combat damage distributions | 510.1 | The implementation computes allocations itself. No general damage-assignment command appears in the public command union; complete player selection remains incomplete. |
| Deal assigned damage simultaneously, then settle | 510.2–3 | Batch code and replacement-resumption tests exist. Full simultaneous interaction semantics need focused tests. |
| Trample when every blocker is gone | 702.19 | **Failed isolated subsystem probe.** A blocked 5-power trampler with no surviving blocker deals zero rather than five to the player. |
| Apply deathtouch, lifelink, prevention, protection and damage consequences | 120, 702 | Existing coverage demonstrates subsets. Replacement ordering and history-dependent combinations remain unassessed. |
| End-of-combat triggers, priority, removal from combat | 511 | Implemented; complete duration/trigger ordering requires more cases. |

The isolated combat probes construct coherent subsystem inputs, including a recorded blocker that has left and a keyword loss between steps. They identify algorithmic gaps without claiming a live UI reproduction or a complete command-driven setup for those changes.

## 9 Postcombat main phase and ending phase

The postcombat main phase repeats main-phase permissions; it is a separate phase and may receive additional main-phase or extra-combat effects. Basic traversal exists. Arbitrary added/skipped phases and all skip-step replacement interactions are not certified.

The end step starts with its triggers and grants priority normally. Cleanup then proceeds as follows:

1. Discard down to maximum hand size if necessary.
2. Remove marked damage and expire until-end-of-turn effects simultaneously.
3. Check state-based actions and waiting triggers.
4. If any apply, handle them and give priority during cleanup.
5. Once that cleanup priority cycle ends, perform another cleanup step.
6. Begin the next turn only after cleanup actually finishes.

Rules: 513–514. The engine has discard, damage cleanup and duration expiry, but **the cleanup-trigger probe advances to turn two's upkeep without preserving cleanup priority or the trigger**. Additional risks include permanents becoming lethal when temporary toughness expires and triggers arising from those deaths. These require fixes and full integration tests.

## 10 Rules that operate throughout every turn

| Responsibility | Rules | Assessment |
| --- | --- | --- |
| Ownership, control, zones and new-object identity | 108–110, 400 | Immutable object IDs and zone movements exist. Control changes, hidden zones and delayed references need interaction testing. |
| Characteristics, types, colors, costs, abilities, power/toughness | 201–209, 613 | Substantial layer code exists. Text-changing layer 3 is not applied in the inspected layer pipeline; dependency ordering is explicitly incomplete. |
| Continuous-effect timestamps and dependencies | 611–613 | Timestamp application exists. CR 613.8 dependencies are not implemented; recursion guards are not a substitute. |
| Trigger occurrence, waiting, APNAP placement and linked abilities | 603, 607 | Existing tests cover many cases. **A once-per-turn trigger can trigger again after its first stack instance leaves without resolving**, demonstrated by a controlled event-removal probe. |
| State-based actions | 704 | Life/poison/empty-library loss, creature death, some attachment/legend/token/copy/counter checks exist. Assess every 704.5 subrule individually; a generic SBA class does not cover the full list. |
| Replacement/prevention history | 614–616 | Used-effect tracking and choices exist. Partial application must not silently become success after the 64-round guard. |
| Choices and private information | 101.4, 400 | Choice identity/ownership/count checks and redacted views exist. The choosing player's raw internal context is still exposed by the current pending-choice shape; exact leakage consequences need a separate privacy test, not assumption. |
| Mandatory and optional loops | 727 in this rules snapshot | Complete loop adjudication unassessed. A fixed iteration limit must not be declared a legal draw automatically. |
| Randomness and secret/simultaneous decisions | 101.4, individual mechanics | Seeded RNG and some secret-choice support exist. Simultaneous decision rounds and information ordering need dedicated tests. |
| Face-down cards, copies, transform, meld, adventures and newer layouts | 707 onward | Several dedicated paths/tests exist. Each layout and copied/granted ability combination must be assessed separately. |
| Keyword actions and keyword abilities | 701–702 | Every section is listed in RULES_REGISTER.md. Many declarative keyword expansions remain explicitly unsupported; some names also have narrower direct subsystem support. No blanket mechanic-completeness claim is made. |

## 11 Ending a game and ending a match

Basic win/loss, concession, empty-library loss and post-game refusal are exercised by the existing engine suite. Simultaneous outcomes, cannot-win/cannot-lose effects, optional variants, deliberate loop choices and unusual win conditions need separate conformance cases.

A game outcome is not a match system. Best-of-three sequencing, sideboarding, play/draw selection for the next game, tournament clocks, penalties and external tournament policy are outside the demonstrated engine behavior. Two-player Commander adds its own construction, command-zone, commander-tax and combat-damage rules; some code exists, but it is a separate variant audit rather than the default two-player checklist.

## 12 Required repair sequence

First repair the failed pass-history, first-draw, empty-combat, cleanup and mode-announcement cases. Then repair combat-history/trample and trigger-once accounting. Next audit the SBA/trigger checkpoint with a complete response game. Only after that baseline is correct should broader action schemas, stable ability IDs and new mechanic families be expanded.

For each row above, retain the expected rule, the exact tested setup, source location, test result and unresolved combinations. The full clause register deliberately leaves individually unchecked rules unassessed; it is a complete tracking surface, not a fabricated certification of all Magic.
