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
