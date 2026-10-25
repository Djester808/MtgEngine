# Foundation checkpoint

Reset on 2026-10-25 at the user's direction. The earlier integration checkpoint
incorrectly retained the September engine. The pre-reset implementation and tests
remain recoverable in Git at `b6fa524`; they are not the baseline for future work.

## Preserved contracts

- Printed card definitions and faces: layout, type line, mana cost, text and printed stats.
- Selected printings and finishes; physical card identity separate from current object identity.
- Players and game-local seats; owner, initial controller and current stored controller.
- Immutable game snapshots and structural validation for two to four players.
- Library, hand, graveyard, battlefield, exile, command and stack snapshots.
- Top-first library/graveyard order and bottom-first stack order.
- Permanent status derived from battlefield membership, including phased-out objects.
- Token/copy markers and stack metadata, without lifecycle or resolution behavior.
- Turn/priority snapshots and choice descriptions, without progression or continuations.
- Viewer DTOs, historical reveal data and table layout references. These are data
  contracts, not an implemented privacy projector or transport boundary.

The prior battlefield snapshot queried the legacy layer evaluator. It now reads
explicit stored control. A future executor must own control changes; no replacement
layer algorithm has been introduced. A library snapshot replaces the old executable
draw/search/shuffle coordinators.

## Removed

Casting/payment, combat execution, triggers, layers, replacements, state-based
actions, keyword semantics/expansion, effect interpretation, event reducers/replay,
turn progression, legal-action generation and choice resumption. Also removed:
the card-definition DSL, compiler/translation pipeline, generated executable card
corpus, engine tests and executable pipeline/QA tools, sessions, lobbies, hub, game reports
and implementation-status endpoints.

Database migrations and historical storage entities are retained. No database was
reset. Unrelated API features and the Comprehensive Rules reference are preserved.
The sibling browser client is outside this reset; its play screens are not functional
against this models-only API.

## Verification boundary

Foundation tests construct snapshots directly and check identity, ownership/control,
zone topology/order, player counts, turn/priority separation and table layout.
Architecture tests prevent the removed execution namespaces from returning unnoticed.
Printed-card mapping and the unrelated API suite remain in the solution.
Old evidence reports and baseline audit results describe the retired implementation;
they are historical evidence only, not passing results for this foundation.

Next work should be explicitly scoped from these models. There is no execution engine
or keyword support to reuse implicitly.

Validation after reset: 26 foundation tests, 5 printed-card mapping tests and
736 API tests pass. Solution build reports zero warnings and errors.

## State and zones milestone — 2026-10-25

The reset above remains the baseline. The following primitives are newly written;
none reuse the retired executor. The earlier sections describe the reset checkpoint.

- `GameSetup.Create` creates two-to-four stable seats and all seven zone kinds:
  three personal zones per player and four shared zones (CR 400.1).
  `AddCard` allocates physical/object identity and snapshots mutable printed lists.
  Setup library order is insertion order; no shuffle or opening hand is implied.
- `GameState.Validate` checks unique object membership, positive object/card IDs
  and timestamps, distinct physical cards, valid players/faces/selections, owner-only
  personal-zone membership, and current exile-inspection permissions. Missing empty
  zones remain valid for compatibility with manually constructed snapshots.
  `OutsideTheGame` remains an enum value but is not a game zone (CR 400.11).
- `ZoneTransitions.Move` is a trusted, immutable physical-card primitive, not a
  player action or permission check. Personal destinations redirect to the owner
  (CR 400.3). Library/graveyard insertion is top-first; stack insertion is bottom-first
  with its top at the end. Top/bottom placement is explicit; unordered zones append.
  Ordinary same-zone movement is rejected instead of silently reordering a pile.
- Moves allocate a fresh object ID and timestamp, retaining a server-only physical
  card ID, deck selection, owner and commander designation (CR 400.7, 903.3).
  Allocation high-water marks survive moves and must be preserved with saved state.
  Object-local status is reset; attachments to the departed object detach without
  executing state-based actions. Re-exile and command-zone reentry also recreate the
  object (CR 400.8/400.10). CR 400.4 prohibited card-type moves return unchanged state.
- `ChangeController` updates battlefield control without changing identity, owner,
  membership, or initial controller. Entry defaults to the owner except that stack to
  battlefield preserves stored control; an explicit entry controller may override it.
  The preserved nonnullable stored controller is an owner fallback outside battlefield
  and stack. The new view reports null there, consistent with the basic CR 109.4 case.
- `ZoneStateView.For` is the milestone's disclosure boundary. It projects only
  counts for libraries and other hands, visible printed faces for the viewer's hand
  and public zones, and redacted face-down public objects. Face-down battlefield/stack
  details are available to their controller. Face-down exile requires an explicit
  `AllowExileLook` permission for each viewer (CR 406.3); reentry/movement drops it.
  Printed data is labeled as printed, never evaluated characteristics. Physical IDs,
  timestamps, transition links, arbitrary dictionaries, historical reveals, pending
  choices and legacy library inspections cannot escape through this view.

These operations accept trusted server state. A future session must authenticate the
viewer and serialize only projected data; never expose GameState, ZoneMoveResult, or
the old GameView DTO as an automatic transport boundary. No API is wired to Rules.
Operations are deterministic snapshots, not a concurrent store; callers must serialize
changes to one authoritative branch of state. IDs are game-local, not global/account IDs.

Deliberate limits: movement handles physical cards and resets to printed face zero.
Tokens/copies, alternative-face entry, face-down casting/entry, face turning (including
CR 400.9), public library reveals/inspection/shuffle, simultaneous batches and arbitrary
reordering need later scoped operations. Stack movement is placement only, not casting
or resolution; stack choices/metadata and CR 400.7 effect exceptions are not carried.
No replacements, triggers, layers, state-based actions, combat, costs, turn progression,
deck legality or effect interpreter are implemented. No user decision blocks this scope.

Milestone verification: Release solution build passed with zero warnings/errors;
52 foundation/state-zone tests (26 added), 5 printed-card mapping tests and 736 API
tests passed, 793 total with no skips. Full solution format verification passed.

## Turn, priority and stack milestone — 2026-10-25

`MtgEngine.Rules.Engine.TurnExecution` is a new immutable coordinator over the
state/zone primitives. It is not connected to API gameplay. Retain the continuation
alongside its `State`: persisting GameState alone loses the execution boundary.
There is no public resume/deserialization API yet. Operations assume a serialized,
authoritative server branch; retaining an old continuation does not authorize replay.

Public integration surface:

- `Start(state, startingPlayer)` begins ordinary individual turns after setup/opening
  hands, for two to four seats. `State`, `Phase`, `Boundary`, `PriorityRecipient`
  and `Resolving` describe the current position without granting a choice actor priority.
- `RequirePriority(actor)` checks timing ownership only. `Act(actor, provider)` accepts
  an already validated synchronous action, resets consecutive passes and checkpoints
  the same actor (CR 117.3c). The next action milestone owns legality, costs and targets.
  Providers receive no priority and must reject invalid/no-op actions themselves.
- `Pass(actor)` visits seats in order. Every prospective recipient passes through
  `Checkpoint`; a complete pass round resolves only the top stack object or ends the
  step (CR 117.3d–117.5). Stack insertion during a checkpoint resets the pass sequence.
- `Resolve(provider)` exposes exactly the selected top ID with no priority. The
  provider must execute its effects and disposition, leaving all other stack IDs in
  order. The active player then gets a fresh checkpoint, even if the stack is empty.
  No generic spell/effect resolver is supplied; stack-changing resolution such as
  countering another entry is deliberately rejected by this initial contract.
- `CompleteTurnAction(provider)` supplies mandatory turn-based work before priority.
  Untap goes straight to upkeep without a priority checkpoint (CR 502.4). Draw and
  declare attackers stop before priority. An empty attack declaration must be certified
  in `CombatState`; only then are blockers/damage skipped (CR 508.8), with priority
  still offered in declare attackers and end of combat. Nonempty combat is unsupported.
- `CompleteBasicTurnAction()` explicitly opts into ordinary untap, a one-card draw,
  or cleanup without discarding. It uses stored control to untap/clear summoning
  sickness, moves the library top through ZoneTransitions, and clears damage and
  deathtouch markers on all permanents including phased-out ones. Empty draws mark
  `DrewFromEmptyLibrary` for the checkpoint provider; no loss is adjudicated here.
  This helper assumes no replacement/continuous effects, untap restrictions, phasing
  or day/night semantics. It does not inspect Oracle text. Phased-out untap states,
  required cleanup discards and combat declarations explicitly require another provider.
- `CompleteCheckpoint(provider, cleanupActivity)` requires the caller to perform the
  CR 117.5 SBA/trigger fixed point. There is no built-in implementation or implicit
  no-op default. An identity function is appropriate only for a deliberately restricted
  fixture/rules subset. Providers must report cleanup SBA/trigger activity even when
  it leaves an empty stack. Quiet cleanup starts the next turn; activity grants active
  player priority, and all passes on an empty stack repeat cleanup (CR 514.3a).

Ordinary phase/step order follows CR 500.1. Only the starting player's first draw
step is skipped in two-player games; three/four-player games retain it (CR 103.8).
Unspent mana and per-step draw counters clear at step boundaries (CR 500.5).
Per-turn land/draw counters reset at the next turn. Empty combat state clears at the
end of combat. Cleanup providers must discard first, then simultaneously remove
damage and expire turn-duration effects; the basic helper only supports the stored
damage portion in a subset with no such effects or required discards.

Providers are trusted code, not player requests. Their results are structurally
validated and cannot change seats, turn scheduling or priority bookkeeping. Exceptions
leave the old immutable continuation usable. Active player and next priority recipient
are separate; a pending chooser is never treated as either. Pending choices and
asynchronous action/resolution suspension are explicitly unsupported in this milestone.
Extra turns/steps/phases, arbitrary skips, shared-team turns, player departure,
nonempty combat, mana-ability timing, layers, replacements, triggers, SBAs and keyword
execution require later scoped work. Outcome snapshots stop execution, but this
coordinator does not determine winners. Printed text cannot establish legality.

The architecture guard allows only `TurnExecution`, `ExecutionBoundary` and the
coordinator's compiler-generated nested types in the formerly forbidden Engine
namespace. Other retired execution namespaces (including nested namespaces), executable
card definitions and API-to-Rules coupling remain protected.

Milestone verification: Release solution build passed with zero warnings/errors.
Full solution tests passed: 78 Rules tests (26 new), 5 Cards tests and 736 API tests,
819 total with no skips. Full solution format verification and `git diff --check`
passed. No client or gameplay endpoint changes, commits or pushes were made.

## Basic player actions milestone — 2026-10-25

`Engine.BasicActions` supplies a closed, explicitly registered rules subset over
`TurnExecution`. It does not restore the deleted DSL or interpret arbitrary Oracle
text. `BasicCard` binds a complete printed definition to one `BasicBehavior`.
The registry snapshots its lists, checks the supported shape/text/cost at creation,
and matches the complete printed face when used. Every card in the game, including
hidden zones, must belong to this subset: unknown abilities might operate there.
Registration and state are trusted server inputs, never player-supplied declarations.

Supported definitions are normal single-face, front-face cards only:

- Basic lands with exactly one of Plains, Island, Swamp, Mountain or Forest as their
  subtype, no printed mana cost, and empty text or the canonical mana reminder text.
  They have the corresponding intrinsic tap-for-one ability (CR 305.6). Wastes,
  snow lands, nonbasic lands and creature mana abilities are deliberately unsupported.
- Nonlegendary vanilla creatures (including artifact creatures), empty rules text,
  fixed nonnegative integer power and positive integer toughness.
- Instants/sorceries with exactly `Target player gains N life.` for positive N, or
  exactly `Return target creature to its owner's hand.` Text equality validates an
  explicit declaration; it is not a parser or a general semantic translation.
- Printed mana costs containing only fixed numeric generic symbols and W/U/B/R/G/C.
  Missing costs, X, hybrid, Phyrexian, snow, alternative/additional costs, cost
  modifiers, and every unregistered ability are rejected, never treated as vanilla.

Public action contract:

- `PlayLand(execution, actor, objectId)` requires priority, the active player's main
  phase, an empty stack, that player's hand, and zero lands played this turn. It moves
  directly to the battlefield, records the land play, and checkpoints the same actor
  (CR 305.1–305.3). It creates no stack entry.
- `ActivateMana(execution, actor, sourceId)` requires priority and an untapped,
  controlled registered land. It taps and adds one mana immediately with no stack
  entry, resets passes and checkpoints the same actor (CR 605.3). A newly played land
  can tap immediately. This helper supports priority timing; other payment windows
  require a provider. It does not support responding to mana production.
- `Cast(execution, actor, CastChoice)` requires priority and the actor's hand.
  Creatures/sorceries additionally require their main phase and an empty stack;
  instants can respond. `CastChoice` contains the current card ID, exact six-color
  `ManaPool` spend, the single required object or player target (or neither for a
  creature), and optional distinct `ManaSources` to tap during casting. Generic
  allocation is explicit. Sources must be controlled, current untapped basic lands.
  Targets, taps, full payment, stack movement and stored casting metadata succeed
  together or throw without changing the original continuation (CR 601.2, 118.3).
  The caster receives the next checkpoint. Mana remaining in the pool is retained.
- `Resolve(execution)` delegates to the coordinator's selected top-object boundary.
  It checks stored target shape and current target legality again (CR 608.2b), gains
  life or returns the creature, and puts the spell into its owner's graveyard.
  A stale target makes the targeted spell have no effect; it still leaves the stack.
  A creature spell enters under its stack controller with fresh object identity and
  summoning sickness (CR 608.3a). Physical card identity and deck selection survive.
- `CompleteCheckpoint(execution)` certifies a quiet checkpoint for this closed subset.
  It rejects unsupported statuses (including counters, designations, attachments,
  face-down/phased objects, tokens/copies) and states needing unimplemented SBAs,
  including nonpositive life, failed draws or lethal creature damage. It is not a
  general SBA/trigger engine and does not adjudicate losses. Use a future broader
  provider when extending the rules subset.

All choices are complete, synchronous submissions by the authenticated priority
holder. There is no suspended-choice/token protocol or choice on resolution in this
subset. Wrong actors, foreign hand cards, extra/missing/wrong targets, duplicate tap
sources and stale object IDs fail. Repeating a consumed card ID or tapped source
against the latest continuation fails; replaying a historical immutable continuation
is still a session/store concern. The host must serialize access to one authoritative
branch and authenticate the actor. Do not expose continuations, registry, stack
metadata, CastChoice requests from other players, or GameState as transport snapshots.
`ZoneStateView.For` remains the disclosure boundary; bounced opponent cards expose
only a hand count, without their new identity or physical-card links. That view is
not yet a complete action UI protocol or a legal-action list.

Combat handoff: use `PlayLand`, `ActivateMana` and `Cast` during priority windows,
`Resolve` at the resolution boundary, and preserve the TurnExecution continuation.
These operations do not declare attackers/blockers, deal damage, remove dead
creatures, or implement keywords, replacement effects, layers or triggers. A combat
milestone must supply its own turn-action and SBA checkpoint provider; lethal damage
cannot pass this milestone's quiet-checkpoint helper. Stored control, tapping,
summoning sickness, owner and physical/object identity are maintained for that work.
No API endpoints or sibling-client files were changed. Architecture tests explicitly
allow only the new BasicActions, BasicCard, BasicBehavior, CastChoice and internal
BasicMana types (plus compiler nested types); retired namespaces remain prohibited.

Milestone verification: Release solution build passed with zero warnings/errors;
101 Rules tests (23 new), 5 Cards tests and 736 API tests passed, 842 total with no
skips. Full solution format verification and `git diff --check` passed. Tests cover
land-to-mana-to-creature execution at two/three/four seats, response ordering, stale
targets, exact mana allocation, payment rollback, ownership, duplicate submissions,
timing, unsupported definitions and hidden-hand projection. This milestone made no
commits or pushes; the prerequisite is preserved at `19dc9fb`.

## Basic combat and game completion milestone — 2026-10-25

New `Engine.BasicCombat(BasicActions registry)` implements a closed vanilla subset,
using the preceding immutable `TurnExecution` continuation. It supports individual
two-to-four-seat games, no teams or limited range of influence, with the attack
multiple players option (CR 802). `GameState.Format` is metadata, not a rules-profile
selector. This is not Commander execution: commander-designated cards are rejected
because commander damage, command-zone choices and the other format rules are absent.
No gameplay endpoints or client files are connected to these operations.

Public contract:

- `DeclareAttackers(execution, actor, IEnumerable<AttackDeclaration>)` runs only at
  the declare-attackers turn-action boundary. Supply the complete list of current
  creature IDs and defending player IDs. It validates control, untapped status,
  summoning sickness, living opponent targets, and uniqueness before tapping any
  attackers (CR 302.6, 508.1). Empty declarations are valid. All living opponents
  are defending players; each attacker names exactly one of them. Attacks against
  planeswalkers/battles and attack requirements/costs are unsupported.
- `NextDefender(execution)` identifies the next attacked living defender who must
  declare blocks. `DeclareBlockers(execution, actor, IEnumerable<BlockDeclaration>)`
  accepts that defender's complete list, in APNAP order (CR 802.4). Each untapped
  controlled creature may block one current attacker attacking that player;
  summoning sickness does not prevent blocking. Several blockers may block the same
  attacker. Blocks do not tap creatures. Intermediate defender submissions remain
  at `TurnAction`, with no priority/checkpoint between declarations. A null next
  defender means the active player must certify an empty declaration, for example
  when all attackers left in response. If the active player departed, the next
  living seat performs this empty declaration instead (CR 800.4h).
- `DealDamage(execution, actor, IEnumerable<CombatDamageAssignment>? assignments)`
  runs only at the combat-damage turn-action boundary. The active player supplies
  exact nonnegative damage splits for each positive-power attacker with multiple
  current blockers. Dictionary entries may omit zero allocations; their total must
  equal the attacker's power. No lethal-before-next-blocker order is imposed: this
  follows the repository's CR 510.1c. Forced assignments are automatic; extra,
  stale, duplicate or incomplete choices fail atomically. Each blocker blocks one
  attacker, so its assignment is forced. Zero/negative power deals no damage.
  Damage accumulates from the same pre-damage snapshot and is applied together,
  before any deaths or losses (CR 510.2). No priority exists during assignment.
- `CompleteCheckpoint(execution)` is the combat subset's CR 704 provider. Use it
  throughout the game instead of `BasicActions.CompleteCheckpoint`, whose older
  quiet-state contract remains intact. It validates the same registry and rejects
  unsupported statuses, then computes creature deaths and player losses from one
  snapshot. Toughness at most zero and lethal marked damage move physical creatures
  to owners' graveyards with new object IDs and preserved physical IDs. Deaths are
  determined together; without triggers/replacements/continuous effects, this batch
  reaches the subset's fixed point. Same-owner simultaneous graveyard entries use
  deterministic object-ID processing order; choosing graveyard order is not exposed
  in this subset, which contains no graveyard-order-dependent effects.
  Life at most zero and failed draws eliminate players (CR 704.5a/b/f/g).
  Cleanup activity grants priority and forces another cleanup (CR 514.3a).

`BasicActions` registration now admits any fixed integer creature power/toughness,
including negative power and zero toughness. It still rejects nonnumeric stats and
all unimplemented text. Creature resolution remains separate from the subsequent
checkpoint. Basic land plays, mana, casting, target checks and resolution APIs are
unchanged. Server code must authenticate each actor, retain the full continuation,
and apply submissions to one authoritative branch. Every public combat submission
either returns a new continuation or leaves the input intact. This is not a
concurrent session, replay guard, transport schema or legal-action generator.

Combat identity and turn integration:

- Blocked status is retained even if all blockers leave; the attacker does not hit
  the defending player (CR 509.1h). Removed attackers and blockers deal no damage,
  and damage is not assigned to departed players. New object IDs cannot rejoin old
  assignments. `CombatState.RemovedFromCombat` records participants removed by
  `ZoneTransitions.ChangeController`, including an away-and-back control change.
  Control changes still reset summoning sickness. Ordinary untap clears sickness
  for the active player's permanents; damage stays marked until cleanup.
- `TurnExecution` now visits blockers and damage for nonempty declarations, waits
  for all block submissions, and clears combat after end-of-combat priority. Empty
  attacker declarations still skip blockers/damage. Priority and pass counts use
  living seats, and an elimination resets the pass sequence. A departed active
  player's turn continues to completion (CR 800.4j); next living seat receives its
  priority and handles empty combat decisions. That departed seat draws no cards,
  and future turns skip it. Player IDs/seat positions are never renumbered.
- Departure removes all owned objects from every zone, clears associated private
  inspection history, and retains allocation high-water marks. Life/failed-draw
  losses are batched: zero survivors yields a draw; one survivor wins immediately
  (CR 104.2a, 104.4a). Finished continuations offer no priority or further actions.
  Departure with any cross-player-controlled battlefield/stack object is explicitly
  rejected before publishing a state change: CR 800.4a control-effect expiration
  needs history that this foundation does not store. Tokens, copies and their stack
  departure rules remain unsupported. No concession or arbitrary effect-based
  win/loss API, poison, teams, shared turns, loops or restart adjudication is added.

Keyword integration handoff: extend the explicit registry/support gate before
admitting keyword cards. `BasicCombat` currently uses printed integer stats only;
no layers, counters, evasion, vigilance, haste, first/double strike, trample,
deathtouch, lifelink, damage prevention/replacement or attack/block triggers exist.
In particular, never feed first-strike combat into the single damage step and claim
support. Keep simultaneous damage followed by the CR 704 checkpoint, preserve
blocked status and physical/object identity, and update the narrow architecture
allowlist only for newly authorized types. Basic cleanup still requires a trusted
discard provider if the hand exceeds its maximum; this milestone does not add a
discard-choice protocol. No historical executor/DSL/pipeline was restored.

Files changed for this milestone: new `Engine/BasicCombat.cs` and
`BasicCombatTests.cs`; extended `Engine/TurnExecution.cs`, registry validation in
`Engine/BasicActions.cs`, `State/GameState.cs`, `Zones/ZoneTransitions.cs`, the
foundation architecture allowlist, README and this document. All preceding local
player-action changes are preserved. No commits or pushes were made.

Milestone verification: Release solution build passed with zero warnings/errors;
122 Rules tests (21 new combat cases), 5 Cards tests and 736 API tests passed,
863 total with no skips. Full solution format verification and `git diff --check`
passed. Conformance coverage includes the real land/mana/cast/resolve/turn/combat
path to a winner, three/four-seat multiple defenders, wrong actors and duplicate
declarations, sick/tapped creatures, explicit multi-blocker splits, simultaneous
creature deaths and opponent elimination, bounce responses, negative power, zero
toughness, continuing after active-player loss, departed stack ownership, draw
adjudication, cleanup damage/activity, and explicit unsupported departure cases.

## First evergreen keywords milestone — 2026-10-25

This extends the new closed `BasicActions` / `BasicCombat` subset, not the retired
executor. The prior combat milestone completed and passed 863 tests before these
edits began. All prerequisite work is preserved; API gameplay remains disconnected.

Registration remains explicit trusted server input. `BasicCard.Keywords` is an
immutable set of `BasicKeyword` values, separate from `MtgEngine.Cards` printed data.
Only supported registered creatures can carry these declarations. Registration checks
that Oracle text consists of exactly one canonical standalone line per declared
keyword, in any order (for example `Flying` then `First strike`, separated by newline).
CRLF and LF are accepted; comma lists, reminder text, extra text, unknown enum values,
and keywords on lands/spells are rejected. Empty sets retain the vanilla text rule.
This deliberately small spelling contract validates authored behavior; it does not
parse arbitrary Oracle text or claim catalog-wide support. Definitions still match
the entire registered face in all zones, including hidden zones.

| Family | Supported behavior | Boundary / limits |
| --- | --- | --- |
| Flying / reach | Flying attackers require flying or reach blockers; flying can block ground, reach does not give evasion (CR 702.9, 702.17). | One attacker per blocker; other evasion and blocking permissions unsupported. |
| Vigilance / haste / defender | Vigilance prevents attack tapping only; haste bypasses attack summoning sickness; defender prohibits attacks, including with haste (CR 702.3, 702.10, 702.20). | Tapped creatures still cannot attack. Creature tap/untap-symbol abilities are not admitted by the action registry, so that haste clause awaits creature activated-ability/cost support. |
| Menace | A submitted declaration must have zero or at least two legal blockers for each menace attacker (CR 702.111). | Removing a blocker afterward does not revalidate the declaration or undo blocked status. No attack/block requirements or costs. |
| Trample | Explicit allocation to current blockers and defending player; all blockers need lethal assignment before player damage, considering marked damage and deathtouch. Overassignment is legal; absent blockers allow all damage to the defender (CR 702.19). | Attacking players only; no planeswalkers, battles, multi-attacker blockers, prevention or replacements. Blocking trample has no effect. |
| Deathtouch / lifelink | Positive combat damage marks deathtouch; its next checkpoint destroys affected creatures. Lifelink gains the controller actual damage dealt to all recipients, simultaneously with damage and before losses (CR 702.2, 702.15). | No noncombat damage provider, last-known-information damage, or life-gain/damage triggers. Zero damage has neither effect. |
| First / double strike | Extra first damage step when a current participant has either ability; only those participants deal early damage. Checkpoint and full priority/stack responses precede the ordinary step, where ordinary creatures and surviving double strikers deal (CR 510.4, 702.4, 702.7). | No ability-grant/removal or layers. Participation records current object IDs, so removed or returned objects cannot rejoin. |
| Indestructible | Prevents lethal-damage and deathtouch destruction; zero toughness still goes to graveyard. Marked damage remains, including for trample's assignment threshold (CR 702.12, 704.5f–h). | No general destroy-effect interpreter, sacrifice, regeneration or replacement support. Bounce remains legal. |

`CombatDamageAssignment` now adds optional `DamageToDefender` (default zero).
`DealDamage` requires a choice for each positive-power, damage-eligible attacker with
multiple current blockers, or with trample and at least one current blocker. All
other assignments remain forced. Nonnegative allocations must total power exactly;
extra/stale choices and illegal spill are rejected atomically. Blockers cannot block
multiple attackers, so no other attacker's assigned damage contributes to their
trample threshold in this subset. Indestructible never reduces the lethal assignment
required. Damage, deathtouch markers and life gain use one pre-damage snapshot.

Use `BasicCombat.CompleteCheckpoint` throughout these games. The older
`BasicActions.CompleteCheckpoint` explicitly rejects states containing keyword
cards. During declare-blockers checkpoints, combat refreshes
`CombatState.FirstStrikeParticipants` from current participants, including changes
caused by response spells. The last checkpoint before passing into damage supplies
the CR 510.4 membership; no state-changing action occurs between that checkpoint
and step entry. `TurnExecution` uses that membership to enter
`FirstStrikeCombatDamage` as a turn-action boundary, then advances through a separate
ordinary `CombatDamage` step. Membership is retained across the two steps.
Deathtouch markers clear at every combat checkpoint, even when indestructible
prevented destruction; ordinary damage clears only at cleanup. Existing loss,
departure and winner/draw processing runs after all simultaneous damage and life gain.

No general layers, replacement effects, triggers, arbitrary card abilities, keyword
grant/removal, creature activated costs, or noncombat damage semantics are claimed.
All existing closed-subset restrictions remain (including Commander and departure
with cross-player control). Keyword state/registrations/continuations are trusted
server-only values, not new transport DTOs. `ZoneStateView.For` remains the disclosure
boundary; tests verify an opponent's keyword card in hand does not expose identity
or the executable registry. Host authentication, serialized latest-state submission,
and cleanup discard-choice integration remain future session work.

Changed in this milestone: `Engine/BasicCard.cs`, `BasicActions.cs`, `BasicCombat.cs`,
`TurnExecution.cs`, `State/GameState.cs`, `State/GameObject.cs`, the foundation
architecture allowlist, new `BasicKeywordTests.cs`, README, and this document.
No client or gameplay endpoint changes, commits, or pushes.

Milestone verification: Release solution build passed with zero warnings/errors.
Full solution tests passed: 152 Rules tests (30 new keyword cases), 5 Cards tests,
and 736 API tests, 893 total with no skips. Full solution format verification and
`git diff --check` passed. Conformance tests exercise cast/resolve/haste attack to
winner, flying/reach and menace legality, vigilance/defender, trample assignment and
bounced blockers, deathtouch/lifelink simultaneous damage, indestructible versus
zero toughness and marked damage, both strike steps and response spells between
them, registry rejection, and hidden-hand projection. The .NET SDK required normal
outside-sandbox execution because restricted Windows service access prevented its
initial test command from starting; all reported checks subsequently completed.

## Integration verification — 2026-10-25

All four milestone chats completed in dependency order in the shared checkout.
Turns/priority/stack was already committed as `19dc9fb`; the overlapping actions,
combat and keyword work was reviewed and integrated together after all writers
finished. Independent verification passed: Release solution build with zero warnings
and errors, 893 tests with no failures/skips, full solution format verification,
and diff checks. The README's earlier no-keyword status was reconciled with the
completed bounded keyword support. Integration is local; no external push was made.
