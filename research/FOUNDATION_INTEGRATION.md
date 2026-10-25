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
