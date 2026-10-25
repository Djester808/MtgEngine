# Library ownership audit

## Floating library window order — 2026-10-24

FloatingPanelStackService is the single presentation-order owner for mounted
floating windows. Registration establishes order, activation moves one window
to the front while preserving others' relative order, and destruction unregisters
it. Ranks are renumbered rather than increasing without bound. The directive
retains geometry/pointer handling and delegates focus activation; CSS supplies
the desktop layer. Phone sheets keep their existing layer. No library access,
card choice, game event, saved geometry or transport state enters this service.


## Mill payment/event-origin composition — 2026-10-24

The inspected path has one owner per index transformation: Outcome.Then offsets
following event positions; LibraryMillContinuation copies the movement's origin
to its appended CardMilled notification; TriggerObservation observes that
notification at movement time while preserving its notification CauseIndex and
PaymentOrigin, then remaps origins for the remaining event list. Game.Apply
offsets new origins when adding legacy deferred events. Game.Settle delegates
reversal filtering to PaymentTriggerRecovery and translates filtered choice-event
positions before persisting deferred triggers. PaymentTriggerRecovery owns
whole-step versus occurrence-specific reversal membership.

A new LibraryMillObserverTests regression directly verifies the appended mill
notification receives the movement's journal/step/occurrence origin, keeps its
original cause index through serialized event replay and occurrence observation,
and is absent from the remaining detector input, preventing a second trigger.
The focused observer plus PaymentTriggerRecoveryTests run passes 35 tests in
library-mill-origin-audit.log. Existing inspected recovery assertions distinguish
refunded-life triggers from retained library-movement triggers and whole-step
from partial reversal. No production change or alternative provenance mechanism
was added. This closes the scoped F4 source-ownership audit for mill composition;
it does not resolve F1 admission restrictions or certify unknown historical logs.

## Inspection lifecycle ownership audit — 2026-10-24

Inspected all production LibraryInspections references and the corresponding
disclosure/lifecycle test assertions. No second inspection registry or duplicate
search/draw implementation was found in this boundary.

| Responsibility | Owner and call boundary |
| --- | --- |
| Persist active access | LibraryInspectionChanged applies additions/removals to the single GameState.LibraryInspections map. |
| Search and arrangement access | EffectInterpreter.LibraryInspection creates operation/viewer scopes; search and arrangement adapters call its InspectLibrary/EndLibraryInspection helpers. |
| Explicit Reveal and Look | InspectDisclosedLibraries uses resolution/path/viewer/owner scopes. FinishDisclosures removes only that resolution's records after the root instruction completes, preserving pauses. Reveal alone sets PublicReveal; Look-to-all does not impersonate Reveal. |
| Authorized manual access | ManualLibraryInspection owns manual scope, current membership and exact-permutation checks; ManualLibraryCoordinator dispatches to it. Its lifetime intentionally differs from a resolving spell. |
| Shuffle and reorder expiry | ZoneReordered removes manual access after changed order and all owner inspections after shuffle. LibraryRevealCoordinator separately owns continuous top-card identity changes. These are different state responsibilities, not competing shuffle algorithms. |
| Identity refresh | EffectInterpreter.LibraryIdentity remaps only the operation's or resolution's existing records through shared LibraryIdentity.AfterReorder. It cannot recreate records already removed by shuffle. |
| Projection | ViewProjector filters by viewer and requires each inspected object still to occupy its recorded owner's library. LastLook/LastReveal remain historical records, not grants of active access. |
| Player/game end | PlayerLost removes inspections for the departing viewer or owner; GameEnded clears the registry. |

Inspected assertion bodies in LibraryDisclosureLifetimeTests establish expiry
after resolution, persistence across serialized pauses, Reveal versus Look-to-all,
and that drawing a disclosed card hides it without erasing another inspection.
HiddenInformationTests.InspectionLifecycle checks owner/viewer departure and game
end. Existing full-suite evidence covers these unchanged tests; no new test run
or production refactor is claimed here. Separate manual and resolution lifetimes
are intentional policies, so merging them solely to reduce class count would
obscure ownership. F4's payment/event-origin composition remains to reconcile.

## Bounded saved-choice compatibility contract — 2026-10-24

Search resume follow-up: LibrarySearchContinuationTests now also removes the
batch-plan keys from a serialized pending search and answers through Game.Apply.
Both selecting one card and choosing none pass: inspection/search notifications
are not repeated, exactly one shuffle occurs, replay gives the expected hand and
library counts, inspection ends, and duplicate answers are refused.
library-legacy-search-resume.log passes all three tests in this class. This is
an emulated pre-batch schema checkpoint, not a captured historical game log;
it closes the executable resume gap for this single-participant choice boundary.

Current source/assertion inspection and a fresh focused run establish the following
contract. `library-replay-contract-check.log` passes 61 tests, zero failures or
skips, across LibrarySearchContinuationTests, LibraryArrangementContinuationTests,
PartialNestedLibraryRecoveryTests, NestedLibraryPaymentRecoveryTests and
MillTriggerTests. This is a focused gate, not the final full-suite gate.

| Saved format / condition | Proven behavior and limit |
| --- | --- |
| Prior search-plan and move-map JSON | Literal old payloads deserialize with searcher, owner, candidate and destination preserved. Cleanup removes only the current search's keys. This is format-level evidence, not an end-to-end historical search log. |
| Prior arrangement keys | Participant order and single-participant legacy inspection keys remain readable. Cleanup preserves other players and instructions. The test does not establish arbitrary old multiplayer formats. |
| Immediate mill-cast offer without the offer queue | First-offer accept/decline and first additional-cost payment checkpoints replay and continue through all three offers, with exactly one stack resolution. Later legacy queue positions remain unproven. |
| Activation recovery without nested ownership evidence | PaymentRecovery refuses with rule 733.1 and an explicit saved-payment message. The public Game.Apply regression compares the serialized pending choice before/after refusal; retrying the complete-evidence control succeeds. No migration or inferred ownership is performed. The fixture reaches this old activation state through an internal setup seam, so this does not establish current public admission of that activation. |
| Complete nested payment evidence | Inspected recovery tests preserve irreversible library movement while refunding the reversible life payment and replaying pending choices. These cases do not certify every historical payment shape. |

The search compatibility branch lives in the existing LibrarySearchCoordinator;
it avoids re-emitting inspection for a prior answer/selection and continues through
the same movement and completion path. It is not a second search engine.
Remaining F4 evidence includes full historical search resumption, later legacy
offer positions, and the bounded inspection/event-origin ownership audit. Missing
evidence is not a promise of support or a reason to silently migrate saved games.

## Deferred mill-trigger persistence/privacy checkpoint — 2026-10-24

The API direct-choice regression now includes a deferred mill trigger with a
retained source object, private chosen value, occurrence index and bounded mill
departure appearance. Saving the event log and reconstructing GameSessionService
preserves these fields. Neither player's projected pending choice nor the chooser's
direct hub push exposes the deferred triggers or their private markers. Both
existing variants pass (draw continuation and shuffle-only continuation) in
library-deferred-trigger-privacy.log: 2 passed, 0 failed.

ChoiceSecrets.Withheld already constructs the display context explicitly, so this
required no production change or second redaction implementation. This is API
serialization/projection evidence; trigger timing is covered separately by the
observer tests/live run. It does not certify every historical continuation schema.

## Core operation call-site audit — 2026-10-24

Inspected current production callers and implementations, including manual actions
and interpreter adapters. This establishes ownership of these operations; it does
not establish complete rules coverage or equivalence to MTGO's private server.

| Operation | Authoritative owner | Traced callers / adapter boundary |
| --- | --- | --- |
| Draw | LibraryDrawCoordinator through ZoneSystem.Draw | ManualLibraryCoordinator calls zones.Draw; choice resumption calls the shared Resume; ReplacementSystem uses shared Complete. ObjectDrawn proposals originate in the coordinator. |
| Shuffle | LibraryShuffleCoordinator through ZoneSystem.Shuffle | Setup, opening-hand redraw, manual shuffle, interpreter effects, search runtime and deferred shuffle continuation delegate to zones.Shuffle. The only production Rng.Shuffle call in MtgEngine.Rules is the coordinator's Complete method. |
| Search bounds | LibrarySearchPolicy via LibrarySearchPlanner | Planner is the production caller of Bounds. Manual search constructs a generic Search instruction and invokes interpreter.Run, rather than selecting candidates independently. |
| Search execution | LibrarySearchCoordinator with ILibrarySearchRuntime | EffectInterpreter.Search delegates to the coordinator; SearchRuntime adapts prompts, authorized inspection, movement and shuffle to engine services. StandaloneLibrarySearch only serializes the resumable instruction; it is not a second search executor. |
| Opening hands | OpeningHandCoordinator | TurnSystem starts/advances it; ChoiceSystem holds the same coordinator type for answers. Redraw delegates movement and shuffle to IZoneSystem. OpeningHandsChanged construction is confined to this owner. |
| Pure library ordering | LibraryOrder | Zone movement, manual reposition/arrangement, search and arrangement coordinators, and relevant interpreter reorder paths call Place/Arrange. The helper computes order; ZoneReordered applies authoritative state. |

No duplicate algorithm was identified in these inspected core call paths, so no
replacement implementation or new interface was added. Remaining ownership review
is bounded to continuation serialization, inspection lifecycle and payment/event
origin composition; it must not reopen these paths without concrete contrary
evidence. Other state mutation sites and the entire client dependency graph have
not been certified by this source audit. No test run was needed for this
documentation-only result; latest backend evidence remains 2,061 / 840.

Same-step token movement checkpoint: PaymentCreationMovementRecovery adapts
creation evidence for the existing PaymentMovementRecovery validator. It seeds
creation state through ObjectCreated.Apply, then delegates chain validation.
PaymentCreationRecovery.Separate owns extraction for both stationary and moved
creations. PaymentCreationRestoration owns identity/history removal and event
ownership; PaymentRecovery uses that ownership for original-index provenance.
TriggerObservation replaces the tap-specific name and observes taps/token creation
through the existing trigger detector at event time. No parallel movement,
creation or trigger algorithm was introduced.

Token creation checkpoint: PaymentCreationRecovery owns creation validation,
history removal and extraction of creation-owned events. It delegates remaining
movement/property validation to the existing owners. PaymentRecovery composes
creation removal with those restorations and maps normalized event indices back
to journal origins; PaymentRestoration serializes ordered replay. No second token
creation algorithm or trigger detector was added. Same-step token movement and
live acceptance remain outstanding.
Property-event identity lookup is centralized in PaymentPropertyValidation.IsFor;
creation extraction, movement validation and recovery provenance all call it.

Mixed-chain follow-up: Describe now validates incidental nonlibrary roots lacking
a cost marker using the existing actual-departure and full-snapshot checks. The
existing Partition and reverse-order restoration split dependent and independent
chains; there is no special two-card recovery path. Four cases cover opposite
battlefield orders and optional replayed pauses. Missing root departure evidence
remains unsupported. Live acceptance is outstanding.

Terminal Commander movement follow-up: Describe admits a terminal Command arrival
for a recorded intermediate commander departure/arrival. All existing snapshot,
property, history and candidate checks remain shared. Restoration still uses
PaymentMovementRestoration in reverse order; there is no second Commander rollback
algorithm. Seventeen chain variants pass, including hand-entry return with either
an unrelated retained mill or shuffle. Full rules coverage: 1,980 passing in
library-commander-hand-rules.log.

2026-10-24 commander dependency checkpoint: PaymentMovementRecovery now owns
ReachesRetainedMovement, shared by the single-movement plan and mixed-step
partition. It follows local identity changes before checking consumption by an
already-retained step. PaymentRecoveryPlan remains the sole classifier of library
operations requiring retention; a command-zone destination alone is not classified
as a library operation. No Commander-specific rollback implementation was added.
Full rules coverage: 1,978 passing (library-commander-dependency-rules.log).

2026-10-24 recovery partition checkpoint: `PaymentMovementRecovery.Properties`
selects the residual properties of nonmoving objects and players. `Describe`
validates them through `PaymentPropertyValidation`; `Partition` now uses that
same residual event set to assign reversible origins beside retained movements.
It does not independently implement tap/counter/mana restoration. The existing
`PaymentObjectRecovery` and `PaymentManaRecovery` own retained-event dependencies.
The independent tap and later retained untap/retap regressions exercise this
division (`library-dependent-properties-tests.log`,
`library-dependent-retained-tap-tests.log`). Public admission remains restricted.

Inspected 2026-10-23. Scope: library only. This is a source dependency audit,
not MTGO server parity or a declaration that the SOLID requirement is complete.

2026-10-24 current ownership checkpoint: mill follow-up identity selection now lives
in `EffectInterpreter.Movement.RememberMillArrivals`. The generic proposal handler
calls it before Ask captures continuation memory but no longer contains the public
zone filter/deduplication policy. `LibraryMillContinuation` still owns occurrence
emission; movement memory owns follow-up references. These are distinct outputs,
not duplicate mill algorithms. The method extraction preserves call timing and
data unchanged. All 38 MillTriggerTests pass (`library-mill-memory-owner-tests.log`).

Immediate MayCast retains one path-scoped queue in interpreter memory and delegates
each card to the existing single-card casting implementation. Cost selection and
casting legality remain in CastingSystem. The replay and live additional-cost
checks are recorded in LIBRARY_ACCEPTANCE_AUDIT.md. This does not establish MTGO
parity or finish the remaining payment-recovery ownership audit.

Manual arrangement draft ownership: ZoneViewerComponent provides its own
ChoiceSelectionService and LibraryArrangementDraftService. The draft service owns
the inspected identity snapshot, cancellation and complete-order submission boundary;
the existing selection service still owns pick/move behavior. The board's server
choice uses a separate instance. ChoiceControlsComponent can display selected order
through an opt-in flag; its memoized presentation sort does not change source options
or the authoritative library. LibraryActionsService snapshots the submitted list and
owns busy/retry identities; the server validates full inspected membership and routes
to LibraryOrder.Arrange/ZoneReordered. No second reordering algorithm was introduced.

Mana recovery validation ownership: removed PaymentManaRecovery's separate
record-key grouping/summing validator. ManaChanged.TryApplyTo now performs checked
delta application through the existing generic ManaPool fold, which owns source,
key, color and restriction matching. The checked fold rejects negative blocks
before pruning; ordinary event application retains its existing behavior.
PaymentManaRecovery owns event chronology and expected/restored pool comparisons,
not block identity arithmetic. Full rules 1,780 pass (library-mana-fold-owner-rules.log).

Reposition interaction ownership: GameBoardComponent supplies its existing uiMode
to LibraryRevealComponent. The menu requires idle mode, matching the manual controls
in ZoneViewerComponent, and its command handler checks the same derived eligibility.
This does not introduce a second interaction-state store. The regression verifies
that a mode change removes the actions and prevents emission from a stale menu.

Client reposition transport now shares LibraryActionsService.execute with other
library actions. Retry identity keys include card and position, so an uncertain
top-placement request cannot be reused for a bottom placement or another card.
All actions share one in-flight guard. SignalRService routes the request through
engineCommand's manualLibraryReposition discriminator, with no supplied player ID.
This is transport/service work only; the inspection menu is not connected yet.

Scoped reposition follow-up: the generic ManualMove hub route rejects enforced cards,
so it cannot directly implement the honour-system library control for all admitted
cards. ManualLibraryReposition now reuses the library command coordinator's policy
and request identity guards, ManualLibraryInspection's current-membership check, and
LibraryOrder.Place/ZoneReordered. It adds no ordering algorithm or independent state.
The menu is not wired yet. API 828 and rules 1,757 pass in
library-reposition-command-{api,rules}.log.

Manual ordering entry-point audit: Game.Manual already routes same-library
ManualMove through LibraryOrder.Place and ZoneReordered. It avoids ObjectMoved and
retains hidden identities. The five position cases in ManualLibraryMovementTests
now also prove manual inspection expires on changed order, survives a no-op, and
does not remove an unrelated effect inspection, including serialized event replay.
Full rules suite passes 1,757 (library-reposition-inspection-rules.log).
LibraryRevealComponent currently offers search-choice selection and card details,
not a manual reposition action. Its missing control should reuse the existing
movement route rather than introduce another library ordering implementation.

Manual inspection lifetime is owned by ManualLibraryInspection; it uses the existing
LibraryInspectionChanged event, inspection store and private projection. The existing
ManualLibraryCoordinator and availability policy handle dispatch and authorization.
ZoneReordered expires only the owner's manual scope after actual reorder/shuffle;
other effect inspections remain independent. The client reuses LibraryActionsService
for busy/retry behavior and a typed shared action-command map. No second search,
card movement, inspection store or projection implementation was introduced.

Shared-search aggregation owns result uniqueness: LibrarySearchCoordinator combines
arrivals with stable identity deduplication before exposing RememberAs. This fixes
real duplicate library entries in a following Reorder without adding special cases
to each consumer. Serialized top/bottom regressions plus existing interrupted-search
tests pass in the 1,756-rule / 820-API suites. The direct Zones-assignment inventory
also located event reducers, reveal identity replacement, payment restoration and
the trigger look-back battlefield probe; this textual inventory alone does not
prove the full no-duplicate-logic requirement.

Draw replacement-history ownership: the shared Draw seam forwards the interpreter's
existing used set, and PendingDrawBatch owns its persistence for remaining draws.
ProposeThrough separates the nested replacement's history from the parent walk's
inherited set; independent subsequent draws do not inherit newly used replacements.
No second registry or replacement loop was added. Rules 1,754 / API 820 pass.

Nested replacement history remains in EffectContext. The shared shuffle seam accepts
that existing set and forwards it unchanged; ReplacementSystem seeds it when entering
an instead/choosing interpreter context. No second recursion registry was added.
All seam implementations and forwarding test adapters use the same optional argument.
Four nested-shuffle regressions pass with full 1,749 rules / 820 API validation.

Shuffle ownership follow-up: LibraryShuffleCoordinator is now the sole production
Rng.Shuffle caller. ZoneSystem delegates; ReplacementSystem calls its terminal
completion for awaiting-randomization proposals. The same committed events serve
immediate execution and resumed choices. Search and ordinary Shuffle effects use
the interpreter's existing proposal continuation, with no separate resume engine.
Rules 1,745 / API 820 pass; see library-shuffle-replacement-rules-final.log and
library-shuffle-replacement-api.log. Older references to ZoneSystem owning the RNG
call describe the previous implementation.

Shared-search identity ownership: SearchRuntime supplies the current search's
memory prefix and participant viewers to RefreshLibraryIdentities. That existing
helper owns mapping both saved references and inspection cards; LibraryIdentity
still owns the physical-card correspondence. Arrangement supplies its single
viewer. Four replay regressions and full 1,737 rules / 820 API tests pass
(library-shared-search-identity-rules.log, library-shared-search-identity-api.log).

Client order ownership: LibraryPaymentService owns the local cancellable order;
LibraryPaymentComponent renders it. SignalRService requests descriptors and sends
the confirmed indices on its existing Command path. Cost groups and original
indices remain server-owned. The library-cost marker is projected from the shared
cost model, including nested alternatives, rather than inferred from card text.

Descriptor ownership: CostPaymentOrder emits index/group metadata from its bound
entries; PaymentCostLabel formats public labels without querying hidden state.
ActivationSource is shared by activation execution and preview, avoiding a second
ability/zone/controller/priority/timing validator. GameSession supplies a snapshot
to the casting seam; GamesController binds the authenticated seat and returns
descriptors without saving. Client controls remain unimplemented.

Discovery follow-up: CastingSystem.Choices now reuses CostPaymentOrder.Expand
instead of maintaining a second, one-level alternative expansion loop. Its cursor
uses ObjectCosts.SelectionCount, matching preparation. Command-selected objects
are supplied to both cast and activation discovery. Nested library payment and
automatic-alternative regressions cover agreement between discovery and execution.

Payment-order follow-up: CostPaymentOrder owns recursive alternative expansion,
binding selected objects in announcement order, and stable payment grouping.
Selection counts reuse ObjectCosts.Choice rather than duplicating its cost table;
selected-counter costs add the one-object selector case. CostPayment executes
the prepared queue, and RemainingCostPayment stores unpaid per-cost bindings for
resume. Five new cases pass in the 1,693-test rules suite
(library-selected-order-rules-final.log). Player-chosen ordering remains open.

Ordered restoration follow-up: PaymentRestoration owns application of one reverse
stage. PaymentRecovery uses it to simulate restored properties between payment
steps, and PaymentRecoveryApplied uses it for event replay. Legacy flat fields
delegate to that same implementation rather than retaining duplicate mutation
loops. Verification: 1,680 rules / 813 API, library-recovery-order-rules-final.log
and library-recovery-order-api.log, including omitted-stage legacy JSON replay.

Mixed-property follow-up: PaymentPropertyValidation replaces the separate life,
mana, tap and counter classification branches. It checks ordered event folds
against whole recorded entities. CountersChanged.ApplyTo and ManaChanged.ApplyTo
are shared with ordinary event application. PaymentObjectRecovery now restores
all recorded counter kinds and tap changes in one step. No second counter or mana
arithmetic implementation was added. Regression evidence: 1,664 rules / 813 API,
library-mixed-properties-rules-final.log and library-mixed-properties-api.log.

Payment recovery ownership (2026-10-23): PaymentRecovery coordinates reverse
order and emits the final recovery event. PaymentRecoveryPlan classifies supported
steps; PaymentMovementRecovery reconstructs identity/zone changes;
PaymentManaRecovery reconstructs pools through the shared ManaChanged fold;
PaymentObjectRecovery reconstructs taps and counters through one retained-change
lookup and identity guard. Its accumulator is immutable, so a refused step does
not expose partial property restoration. This removes the duplicated tap/counter
lookup without adding runtime service dependencies or changing serialized events.
Existing command/replay regressions pass: 1,656 rules / 813 API
(library-recovery-ownership-rules.log, library-recovery-ownership-api.log).
This extraction does not complete general recovery or enable activation pauses.

Draw/shuffle call-site audit (2026-10-23): direct draw callers in turn draw-step,
monarch end-step, opening hands, manual library actions and interpreter effects
all route through IZoneSystem.Draw -> LibraryDrawCoordinator. The only production
Rng.Shuffle call is ZoneSystem.Shuffle; search, general Shuffle effects, opening
hands, manual shuffle and resolution cleanup call that shared implementation.
This confirms shared execution at those call sites, not complete rules correctness
(the monarch caller explicitly documents a turn-based approximation of a trigger).

The audit found a real sequencing defect in CastingSystem.FinishResolving: its
library shuffle ran while the preceding arrival waited on replacement choices.
Two LibraryResolutionShuffleTests cases reproduced it (library-resolution-shuffle-red.log).
LibraryShuffleContinuation now defers owed shuffles in server-only ChoiceContext
data and completes them after resumed operations finish. ChoiceSecrets strips
that continuation from player projections. Tests cover two/three competing
replacements, replay at every answer, unchanged random state while waiting,
exactly one final shuffle containing the arrived card, and both viewers' privacy.
Full rules 1,579 and API 806 pass (library-resolution-shuffle-rules.log and
library-resolution-shuffle-api.log). No card definitions changed. Browser coverage
for this specific interruption and MTGO server equivalence remain unverified.

Shortcut delivery follow-up (2026-10-23): the boundaries specified below are now
implemented for Keep/Mulligan. LibraryShortcutBindingPolicy is pure;
UiPreferencesService reuses existing storage/normalization/save status;
SettingsComponent handles editing; ChoiceControls uses current preferences for
dispatch and hints. No new persistence service or server endpoint was added.
Unit tests cover corrupt/invalid bindings, collision rejection, unbinding, reload,
reset isolation, exact modifiers and offered-action dispatch. Live UI evidence
is recorded in LIBRARY_METHOD_PARITY.md. Global application bindings, account
sync and selector-confirmation keys are not claimed by this implementation.

Configurable shortcut implementation boundary (2026-10-23): inspected client
LibraryChoiceShortcuts, ChoiceControlsComponent, UiPreferencesService and
SettingsComponent plus their existing specs. UiPreferencesService owns local
storage, normalization and save status; PreferencesApiService is a separate
account preference contract. Reuse UiPreferencesService for device-local library
bindings rather than introducing a second storage owner or implying account sync.

Required ownership for the pending implementation:
- Library shortcut policy: canonical key/modifier representation, collision
  detection, defaults and matching against offered mulligan options. No storage
  or transport in this class.
- UiPreferencesService: validated persisted binding values and resetting only
  library bindings; preserve accessibility, navigation and startup settings.
- SettingsComponent: accessible binding editor, explicit unbind/reset controls,
  collision feedback and the existing saved-state feedback.
- ChoiceControlsComponent: use the same current bindings for dispatch and
  aria-keyshortcuts/button hints; keep existing pending-send, text-entry,
  composition, repeat, handled-event and dialog guards.

Acceptance must cover reload persistence, invalid saved bindings, collision
rejection without losing the prior binding, unbinding, reset isolation, updated
labels and dispatch only for offered options. Live checks must change bindings
through settings, reload, use the new keys in opening hands, verify old keys no
longer act, and capture the editor at phone/desktop sizes. Modifier combinations
must be explicit; configurable does not mean only replacing one hard-coded digit.
This section records inspected implementation boundaries, not delivered behavior.

Identity ownership follow-up (2026-10-23): LibraryIdentity.AfterReorder now owns
the positional correspondence between a requested library order and its applied
result. Both the interpreter's memory/inspection refresh and ZoneSystem.Tape's
remaining batch instructions use this pure operation. The duplicated zip/filter
implementation was removed; callers retain responsibility for updating only
their own operation state. Both now use the same unequal-count guard and receive
an immutable map. Full rules 1,571 and API 806 pass in
library-identity-owner-rules.log and library-identity-owner-api.log, including
the existing ordering, revealed-card continuation and serialized batch scenarios.
This demonstrates a local duplication reduction with regression coverage, not
superiority to an unrecovered MTGO server implementation or a complete SOLID audit.

Revealed-card batch follow-up (2026-10-23): a reorder reidentifies a tracked
revealed card, but later requests still used its prior ID. Two regressions
reproduced the skipped second reposition (library-zone-revealed-red.log).
ZoneSystem.Tape now records the identity transitions caused by ZoneReordered
and resolves subsequent instructions against them. Remaining deferred moves are
resolved before saving a pending choice, so those references survive replay.
The three regression scenarios cover uninterrupted execution and commander
choices both before and after the first reorder, with serialized replay.
All 1,569 rules and 806 API tests pass (library-zone-revealed-rules.log and
library-zone-revealed-api.log). The fixture explicitly seeds revealed-top
tracking; it verifies identity continuation, not the timing of a continuously
active reveal ability between every instruction. That timing, live browser
coverage and the recovered MTGO method mapping remain unverified.

Direct zone-service follow-up (2026-10-23): six LibraryZoneServiceTests cases
reproduced false ObjectMoved events for top/bottom repositioning through Move,
MoveAll and a batch resumed after a commander replacement choice. Evidence:
library-zone-service-red.log. Proposal now retains the object for same-library
instructions; the shared ApplyMove translates them through LibraryOrder.Place
into ZoneReordered using the current order. ResumeMoveBatch allocates a new ID
only for actual zone changes. Bottom instructions use the append sentinel so
earlier moves do not leave a stale bottom index. DeferredZoneMoves retains its
existing ObjectMoved-shaped instruction format; these instructions are not
emitted as occurrence events. A dedicated deferred-instruction type remains an
architectural follow-up, not a claimed improvement already delivered.

Verification: all six cases pass, including serialization before answering the
commander choice and replaying the resulting events; hidden objects retain their
identity and properties. Full rules 1,566 and API 806 pass in
library-zone-service-rules.log and library-zone-service-api.log. This supersedes
the direct-entry-point gap below for the tested hidden-card paths. Continuous
revealed-top identity transitions across multiple deferred repositionings still
need specific coverage. MTGO method equivalence and browser behavior for these
paths remain unverified.

Manual movement audit follow-up (2026-10-23): Game.Manual previously emitted
ObjectMoved and allocated a new object ID for a move within the same library,
including a move back to the same index. Five ManualLibraryMovementTests cases
reproduced this (library-manual-movement-red.log). The same-library branch now
uses LibraryOrder.Place and ZoneReordered. Hidden objects and their properties
remain intact, the manual action stays logged, and serialized event replay
preserves the result. Positions 0, 1, 2, -1 and 99 cover top, unchanged, bottom
and the existing append convention. Full rules: 1,560 passing in
library-manual-movement-rules.log; API: 806 passing in
library-manual-movement-api.log. This is engine evidence, not browser evidence
or an MTGO method-equivalence claim. Direct ZoneSystem.Move/MoveAll and resumed
zone batches still require a same-library audit; this does not close those paths.

Search context restriction: ILibrarySearchRuntime.Context is now read-only.
The coordinator requests Forget(keys) for its own completed-search memory instead
of replacing the whole EffectContext. The adapter changes only Remembered and
preserves all other context fields. Full rules 1,553 pass in
library-search-context-rules.log. This closes the writable effect-context gap;
the exposed marks dictionary and complete call-site inventory remain open.

Arrangement coordinator follow-up: LibraryArrangementCoordinator now owns the
full participant/decision/movement/completion sequence, using the existing planner
and continuation classes. ILibraryArrangementRuntime supplies only the required
state/memory, answer and execution operations. ArrangementRuntime retains the
EffectRun cursor inside the interpreter and delegates to shared Ask, Relocate,
inspection and identity-refresh paths. No Library source references EffectRun or
the interpreter namespace after this extraction. Full rules 1,553 and API 806
pass (library-arrangement-coordinator-*.log), including four-player persisted
arrangement and replacement interruption. This supersedes the remaining embedded
arrangement-coordinator gap below. Broad search context/marks and repository-wide
mutation/call-site coverage still need review.

Search boundary follow-up: LibrarySearchCoordinator and ILibrarySearchRuntime no
longer reference EffectInterpreter.EffectRun or the interpreter namespace.
The contract exposes state/context, saved marks, instruction path, answer metadata
and shared search operations. SearchRuntime owns the traversal cursor privately
and returns its final result only to the interpreter. Proposal ordinals, accumulated
events, stack identity and traversal flags stay outside the library contract.
Existing operation bodies are reused behind adapter methods; no second movement,
shuffle or replacement implementation was introduced. Full rules 1,553 and API
806 pass (library-search-boundary-rules.log, library-search-boundary-api.log).
The runtime is stateful and scoped to one instruction invocation; it must not be
shared across resolutions. EffectContext/marks are still broad mutable seams and
can be narrowed further. This supersedes the specific EffectRun coupling gap below.

Live follow-up closes the fresh-browser verification gap for the continuation
extraction: two-player surveil table f6d518d1-7e40-4a02-9c5a-768fd83e7f65 passed
private inspection, refresh, landscape selection, public graveyard movement and
access expiry. Two-player scry table 187c3dcb-4a96-4106-a840-03894353a2a4 passed
refresh, private top ordering, confirmation and subsequent draw/second-scry
verification of server order. Logs: library-continuation-surveil-live.log and
library-continuation-scry-live.log in client e2e/screenshots. Inspected the completed
surveil and top-order landscape captures. These are two-player flows; four-player
arrangement remains engine-tested rather than live-tested.

Continuation follow-up: LibraryArrangementContinuation now owns the saved player
list, legacy single-player memory prefix, per-player memory prefix and targeted
cleanup. ArrangeLibrary delegates to it. The representation remains the existing
comma-separated participant IDs and :library:<path> keys. A literal-key regression
verifies round-trip participant order, legacy recognition and cleanup isolation
between owners and instructions. Full rules 1,553 and API 804 pass in
library-arrangement-continuation-*.log. This change has not had a fresh live browser
run; the preceding planner live capture predates this extraction. The coordinator
and search runtime work described below remains unfinished.

Follow-up: LibraryArrangementPlanner.Plan now owns the pure scry/surveil decision
transition: initial selection, automatically determined zero/one-card top order,
and explicit ordering of multiple remaining cards. Its inputs are inspected IDs,
saved selected/top IDs and operation type; its output is a decision and optional
prompt description. It has no EffectRun dependency or state mutations.
ArrangeLibrary consumes that plan through existing Ask/Recall and retains the
same persisted keys. Participant discovery, disclosure/movement and cleanup still
need a separate coordinator boundary; the search runtime dependency also remains.
Full rules (1,552) and API (804) pass after integration in
library-arrangement-planner-rules.log and library-arrangement-planner-api.log.

| Responsibility | Owner and inspected callers | Finding |
| --- | --- | --- |
| Individual draws, replacement interruption, deferred batches | LibraryDrawCoordinator.Draw/Complete/Resume; ZoneSystem.Draw; ReplacementSystem; ChoiceSystem.Resume | One draw coordinator. TurnSystem, interpreter drawing, opening hands and manual draw route through zones.Draw. No second core draw implementation found in these inspected paths. |
| Shuffle permutation | ZoneSystem.Shuffle calls Rng.Shuffle | OpeningHandCoordinator.Redraw, ManualLibraryCoordinator.Execute, interpreter shuffle and SearchRuntime.Shuffle reuse zones.Shuffle. Replacement/proposal handling remains in the existing shared runtime. |
| Top/rest/bottom arrangement | LibraryOrder.Arrange | Search coordinator, scry/surveil and generic reorder call this pure function. It does not move cards or ask questions. |
| Search eligibility and destinations | LibrarySearchPlanner.Plan/Moves/Destination; LibrarySearchPolicy | Separate from search sequencing. Requires query and characteristics dependencies. |
| Search sequencing and persistence | LibrarySearchCoordinator.Execute; LibrarySearchContinuation | Dedicated owner, but both coordinator and ILibrarySearchRuntime expose EffectInterpreter.EffectRun. Dependency direction remains coupled to the concrete interpreter cursor. |
| Search state-changing operations | EffectInterpreter.SearchRuntime implements ILibrarySearchRuntime | Reuses shared movement, replacement, shuffle and inspection operations. Do not replace this with a second zone engine during decoupling. |
| Reveal-until prefix calculation | LibraryRevealPlanner.Plan | Pure selection plan using IQuery; no UI prompt or movement. |
| Scry/surveil | EffectInterpreter.Library.ArrangeLibrary | Still combines participant discovery, disclosure, saved decisions, ordering prompts, replacement-aware movement and cleanup. This is the largest inspected library responsibility still embedded in the interpreter. |
| Owner-selected order before library movement | EffectInterpreter.OrderLibraryMovement | Saves owner decisions before shared Relocate. Closely tied to movement continuation; extraction must retain per-owner order, insertion reversal and proposal ordinals. |
| Opening-hand rounds | OpeningHandCoordinator.Start/Advance/Decide/Bottom/Redraw | Round state has one owner; redraw/bottom reuse IZoneSystem. TurnSystem and ChoiceSystem delegate to it. |
| Authorized manual draw/shuffle | ManualLibraryPolicy and ManualLibraryCoordinator.Execute | Availability and execution separated; requests use shared zone operations and persisted request IDs. Broader manual library operations are not implemented here. |
| Inspection display | LibraryRevealComponent | Uses authorized DTO contents, grouped by owner; owns presentation and detail expiry. It does not decide permission or perform library mutations. |

## Required next boundary work

1. Separate scry/surveil planning and continuation state from the interpreter's
   traversal cursor. Keep the existing shared arrangement and movement operations.
2. Define the smallest library resolution contract needed by coordinators, then
   move ILibrarySearchRuntime away from the nested EffectRun type. A renamed wrapper
   that still exposes the complete interpreter cursor would not satisfy this.
3. Inventory the remaining direct library state mutations outside the directories
   inspected here before claiming no duplicate logic across the repository.
4. Retain replay compatibility for already-persisted choices, or explicitly migrate
   their representation. Refactoring must not strand an active multiplayer game.

## Behavioral constraints on refactoring

The current regression fixtures cover four-player APNAP decisions before movement,
per-viewer inspection privacy, private continuation serialization, top/bottom
ordering, and pauses during competing replacements. Preserve these behaviors while
changing ownership. Full library acceptance also needs live multiplayer and the
client verification gate; architecture tests alone cannot establish gameplay parity.

This audit found no reason to introduce per-card implementations or broaden work
into collection/deck construction. Those domains remain outside this library pass.

## Search and arrangement call-site review (2026-10-23)

Current production call-site search confirms EffectInterpreter.Searching constructs
LibrarySearchCoordinator through SearchRuntime; manual search enters the same path
via interpreter.Run with a Search instruction. EffectInterpreter.ArrangeLibrary
constructs LibraryArrangementCoordinator through ArrangementRuntime for scry/surveil.
The adapters own EffectRun, answer/mark access, events and replacement-aware movement;
the coordinators do not own a second interpreter state. Rng.Shuffle has one production
call site in LibraryShuffleCoordinator.

StandaloneLibrarySearch owns the server-created source-less Search serialization key.
EffectInterpreter.Run saves it; Resume recovers the instruction and preserves it across
answers. ChoiceSecrets.Withheld removes that key from projected choices. This is a
concrete serialization/privacy ownership mapping, not proof that string marks are an
ideal domain contract. ILibraryArrangementRuntime still exposes mutable Marks and
Remembered contracts: replacing these requires a complete continuation dependency
inventory and replay compatibility evidence, not simply wrapping the dictionaries in
another class. No production refactor or new test result is claimed in this review.

### Arrangement continuation dependency inventory (2026-10-23)

| Data / operation | Writer and reader | Compatibility boundary |
| --- | --- | --- |
| Frozen participant list | LibraryArrangementCoordinator calls LibraryArrangementContinuation.SavePlayers; ReadPlayers reads AnswerExtra on re-entry. | Existing `:library:<path>:players` string key and APNAP sequence must survive pending-choice serialization. |
| Inspected, selected and top lists | Coordinator reads/writes participant keys in Remembered; continuation owns participant prefix and cleanup. | Single-player legacy keys omit the player suffix. Empty lists are decisions, not missing values. |
| Choice serialization | EffectInterpreter.Ask copies Marks and serializes Remembered under RememberedPrefix; Resume.Bind restores missing remembered entries. | Do not introduce a second persistence mechanism or overwrite authoritative context memory during restore. |
| Reorder identity transition | ArrangementRuntime.Order emits ZoneReordered then RefreshLibraryIdentities updates operation-prefixed remembered IDs and the active viewer inspection. | A detached typed-state snapshot would become stale unless it participates in this existing remapping. |
| Interrupted surveil movement | ArrangementRuntime.MoveToGraveyard delegates to replacement-aware Relocate; coordinator returns when runtime.Halted. | Resume must retain prior decisions and shared proposal progress, without repeating completed moves. |
| Cleanup | ClearParticipant removes only looked/selected/top for one participant; ClearPlayers removes only the instruction's participant marker. | Keep unrelated players, paths and interpreter marks. Existing LibraryArrangementContinuationTests explicitly checks this boundary. |

The narrower interface should offer arrangement-specific participant/decision
operations while its interpreter adapter remains responsible for the shared stores.
Do not move EffectRun, event emission, replacement traversal or identity remapping
into a new standalone arrangement engine. Preserve wire keys initially so existing
pending games remain resumable. Acceptance must include the existing
MultiplayerLibraryOrderingTests (all decisions before movement),
InterpreterTests.SearchRevealIdentity (revealed top identity after reorder),
LibraryArrangementContinuationTests (legacy keys and isolated cleanup), and the
interrupted-movement regressions. This inventory is complete for the inspected
arrangement seam only; it does not close the full library ownership audit.

### Arrangement mutation boundary narrowed (2026-10-23)

ILibraryArrangementRuntime no longer exposes Marks or a Remembered setter.
LibraryArrangementCoordinator saves/clears participants through explicit operations;
ArrangementRuntime applies them to EffectRun using LibraryArrangementContinuation.
This prevents the coordinator from replacing unrelated interpreter state while
preserving existing serialized keys and identity-remapped reads. Read-only memory
and string-key decision operations remain exposed; the complete typed decision
interface and broader ownership audit are still unfinished.

Validation after edits: full rules 1,790 and API 837 pass in
library-arrangement-interface-rules-final.log and library-arrangement-interface-api.log.
The earlier unsuffixed rules log ran before the edits and is not refactor evidence.
No UI changes or npm verification in this pass. No commit.

### Typed arrangement decisions completed (2026-10-23)

The arrangement coordinator/runtime contract no longer exposes interpreter Path,
AnswerExtra, Marks or Remembered dictionaries, or accepts a string memory key.
LibraryArrangementMemory identifies Looked/Selected/Top; the existing continuation
class maps those values to unchanged persisted keys. ArrangementRuntime owns saved
participant lookup, legacy inspection detection and card-list access. Order accepts
participant count and constructs the identity-refresh prefix inside the adapter.
Every card-list read still uses current EffectRun memory, including after identity
remapping. No detached state cache, new movement engine or saved-game migration.

Full rules 1,790 and API 837 pass after the changes:
library-arrangement-typed-rules.log and library-arrangement-typed-api.log.
The arrangement-specific exposed-dictionary gap is closed; the analogous search
contract and the complete ownership audit remain open. No client changes, new
browser claims or commit.

### Search continuation mutation ownership (2026-10-23)

ILibrarySearchRuntime no longer exposes writable Marks, AnswerExtra or arbitrary
Forget(keys). SearchRuntime owns scoped participant/move/board save and restore
operations through LibrarySearchContinuation. ClearContinuation removes only this
search's persisted plan fields and remembered prefix. The coordinator retains
selection, all-participant movement-before-shuffle sequencing and result aggregation.
Wire keys, source-less instructions, replacement traversal and identity remapping are
unchanged. EffectContext and string-key card memory remain exposed and need the next
ownership pass; this is not full search-interface completion.

After edits, full rules 1,790 and API 837 pass in
library-search-state-owner-rules.log and library-search-state-owner-api.log.
No UI change, new browser claim or commit.

### Search planning context isolated (2026-10-23)

Removed EffectContext from ILibrarySearchRuntime and removed the concrete planner
constructor dependency from LibrarySearchCoordinator. Plan and PlanMoves go through
the adapter to the existing LibrarySearchPlanner with current run.State/run.Context.
The coordinator retains sequencing without access to source, target, replacement or
other unrelated interpreter context fields. Its remaining Remembered dictionary is
read-only but still string-keyed; typed search memory remains unfinished.

Full rules 1,790 and API 837 pass after edits in library-search-context-rules.log
and library-search-context-api.log. No algorithm duplication, persisted-format change,
UI change, new browser claim or commit.

### Typed search card memory (2026-10-23)

Removed the Remembered dictionary from ILibrarySearchRuntime. Search decisions use
LibrarySearchMemory (Choice/Selected/Library/Arrived/Top/Bottom); the continuation
class owns their persisted suffix mapping. Explicit Positioned/MarkPositioned calls
preserve per-card within-library progress. SearchRuntime reads the current context
on every call, retaining identity remapping and the distinction between absent and
empty decisions. RememberResult remains separate for the effect's public RememberAs
contract. Participant prefixes and the saved move-plan map remain string-based and
require a later scoped contract; this is not complete search-interface closure.

Full rules 1,790 and API 837 passed in library-search-typed-memory-rules.log and
library-search-typed-memory-api.log. Existing saved keys are unchanged. No client
change, new browser claim or commit.

### Search persistence keys isolated (2026-10-23)

LibrarySearchScope carries participant count and searcher identity. The coordinator
uses it for typed card decisions and positioning markers, and keys planned moves
by PlayerId. SearchRuntime translates those scopes/plans through the existing
LibrarySearchContinuation keys at save/load boundaries. The coordinator no longer
constructs continuation prefixes or receives interpreter Path. Legacy-selected
lookup is an explicit adapter operation. Removed unused memory-prefix arguments
from Shuffle/Order; the existing shared-search identity refresh still covers all
participants using the saved participant list.

Existing wire keys and source-less search continuation remain unchanged. Full rules
1,790 and API 837 pass in library-search-scope-rules-final.log and
library-search-scope-api.log. Initial build failed only the enforced formatting
check, corrected before the successful runs. This closes the inspected search
prefix/move-plan exposure, not the entire library architecture or acceptance audit.
No UI change or commit.

### Partial movement recovery ownership (2026-10-23)

PaymentMovementRecovery.Partition now owns retained-dependency filtering, source-zone
anchor adjustment, partial event selection and retained-object conflicts. Its immutable
PaymentMovementPartition returns the restorable movements and reversed origins, or
null for unsupported input. PaymentRecovery retains sequencing/application and no
longer implements the movement partition inline. Existing partial-movement/replay and
trigger cases pass with full rules 1,798 / API 837 (library-partial-owner-rules.log,
library-partial-owner-api.log). No saved format or behavior change; no commit.

### Recovery planner dependency cycle removed (2026-10-23)

PaymentRecoveryPlan now owns assessment/classification only. Movement identity-chain
tracing moved to PaymentMovementRecovery, which already owns movement description
and partitioning. PaymentSnapshotComparison owns saved-content equality using the
unchanged EventLogSerializer options and JsonElement.DeepEquals; mana, movement and
property validation share it. Those validators no longer reference the planner,
removing their previous back edge while preserving replay comparison semantics.
Test call sites use the same extracted comparator. No journal/event format changed.
Full rules 1,809 and API 838 pass in library-recovery-dependency-owner-rules.log and
library-recovery-dependency-owner-api.log. This closes the inspected recovery cycle,
not the complete library ownership audit. No commit.

### Manual arrangement inspection ownership (2026-10-23)

ManualLibraryInspection.CanArrange now validates the submitted complete order against
current library membership and the active manual inspection. It builds membership sets
once instead of asking Contains for every card (which repeatedly scans the library).
The predicate carries a NotNullWhen(true) contract. ManualLibraryCoordinator delegates
validation and still emits the shared LibraryOrder/ZoneReordered result; no separate
ordering algorithm, inspection store or permission system was introduced.
Session-level honour-system permission remains in GameSession, while game-state
availability remains shared through ManualLibraryPolicy. Rules 1,809 pass in
library-inspection-owner-rules-final.log. No saved format or UI changes; no commit.
API authorization/persistence compatibility: 838 passed in library-inspection-owner-api.log.

### Library retry persistence (2026-10-23)

LibraryRequestJournal is the single owner of persisted uncertain request identities.
LibraryActionsService supplies semantic action keys, serializes submissions and clears
confirmed entries. SignalRService supplies the current table/viewer scope and preserves
transport errors; it owns neither storage nor retry allocation. The previous in-memory
Map was removed. Unit/service integration and four-browser reload evidence are recorded
in the acceptance audit's Reload-safe library request journal section.

## Completed source audit: ordinary ordering and shuffle call sites — 2026-10-23

Inspected every production `new ZoneReordered`, `LibraryOrder` call and `Rng.Shuffle`
call under MtgEngine.Rules, then read each library producer and its caller. This closes
the ordering/randomization call-site portion of the ownership audit, not the entire
library ownership requirement.

| Entry point | Responsibility and shared owner |
| --- | --- |
| ManualLibraryCoordinator.Reposition / Arrange | Checks current inspection and complete membership; delegates to LibraryOrder.Place / Arrange. |
| Game.Manual same-library move | Logs the manual action and uses LibraryOrder.Place; emits no ObjectMoved. |
| ZoneSystem.ApplyMove | Recognizes same-library proposals, uses LibraryOrder.Place, suppresses unchanged order. |
| EffectInterpreter.Relocate | Handles replacement continuation and disclosed identities around LibraryOrder.Arrange for one-card top/bottom placement. |
| EffectInterpreter.Reordered | Obtains any-order answers and groups by current zone; uses LibraryOrder.Arrange. |
| LibraryArrangementCoordinator | Plans scry/surveil choices and uses LibraryOrder.Arrange; ArrangementRuntime emits the supplied order and refreshes saved identities. |
| LibrarySearchCoordinator.FinishSearch | Controls shuffle-after and selected top/bottom placement through LibraryOrder.Arrange; SearchRuntime emits the supplied order and refreshes saved identities. |
| LibraryShuffleCoordinator.Complete | Sole production Rng.Shuffle call; advances RNG and commits the completed shuffle after replacements. |
| ZoneReordered.Apply | Sole ordinary reorder reducer; expires manual inspection and reordered disclosure when appropriate. |

The additional EffectInterpreter.Resume.Reordered producer explicitly addresses
Zone.Stack, so its selected-slot permutation is not a duplicate library algorithm.
LibraryRevealCoordinator directly replaces one identity in Zones while preserving
its slot; this is disclosure identity renewal, not another ordering algorithm.
Repeated event construction in runtime adapters preserves distinct continuation and
identity-refresh responsibilities; extracting a shared event factory alone would
not eliminate duplicated ordering logic.

Coverage read: LibraryOrderInvariantTests checks one duplicate/stale-reference case,
preserving membership and the unselected sequence.
LibraryZoneServiceTests checks direct, batch and commander-choice-deferred same-library
movement, object identity, no ObjectMoved and serialized event replay. Existing full
rules baseline remains 1,809; no production edits or new test run in this audit.

Remaining ownership work includes draw completion/batch continuations, inspection and
disclosure lifetimes across entry points, and interrupted payment ownership. This
audit establishes source delegation, not MTGO internal algorithm equivalence.

## Completed source audit: draw entry points and continuation ownership — 2026-10-23

Read all production calls to IZoneSystem.Draw and all construction sites for
ObjectDrawn, DrawFromEmptyLibraryAttempted and PendingDrawBatch. Manual draw,
turn-step draw, monarch draw, opening-hand redraw, interpreter Draw and deferred
standalone batches enter ZoneSystem.Draw -> LibraryDrawCoordinator.Draw.

LibraryDrawCoordinator owns one-at-a-time proposal creation, replacement offering,
standalone remaining counts and terminal completion. ReplacementSystem invokes its
same Complete method after replacement selection. The AwaitingCardMovement marker
separates a proposal from a committed draw; a committed ObjectDrawn is applied without
moving another card. Complete asks ZoneSystem.PrepareDraw to build the current top
card's movement, then emits movement before ObjectDrawn. It does not cache the original
object allocation or assume replacements left the library unchanged.

EffectInterpreter.Drawing owns the evaluated instruction amount and APNAP participant
plan in its existing continuation marks. It submits individual draws through
ProposeThrough, whose interpreter progress handles resumed instructions. In contrast,
PendingDrawBatch records draws owed to standalone callers that do not have an effect
tree. ChoiceSystem.Resume delegates those batches back through zones.Draw. These are
two continuation contexts, not two implementations of drawing or moving cards.
Inherited replacement history is retained per deferred batch; replacements consumed
by one independent draw are not copied into the next draw's inherited set.
ChoiceSecrets removes DeferredDraws from public pending projections.

Coverage inspected (not newly executed): LibraryDrawContinuationTests covers forbidden
draws, standalone serialized continuation, dynamic amount stability and movement before
draw accounting after replacement candidates disappear. LibraryNestedDrawTests covers
inherited replacement history, private continuation data and nested draws through
serialized choices with empty/nonempty libraries. EmptyLibraryDrawReplacementTests
checks that a replaced empty-library draw neither increments draw counts nor records
an empty-library failure. Full rules baseline remains 1,809.

No duplicate draw algorithm was found in these entry points; no runtime refactor was
needed. This does not close opening-hand initialization as a whole, disclosure lifetime,
interrupted payment or live all-player scry/surveil acceptance. It establishes source
ownership, not MTGO server method parity.

## Completed source audit: opening-hand initialization and mulligans — 2026-10-23

Game.Create delegates initial zone population to ZoneSystem.Create, then offers
opening hands through ITurnSystem.OpenHands before beginning the turn. ZoneSystem
creates library objects from DeckCards and command-zone objects from CommandCards;
it calls the same Shuffle and Draw methods used during play for initial randomization
and hand dealing. Initial object creation is setup responsibility, not a second
movement/draw algorithm. This closes the initialization call-site omission from the
previous draw audit.

OpeningHandCoordinator owns declaration rounds, per-seat mulligan counts, kept seats,
redraw timing and bottoming completion. OpeningHandPrompts owns private choice shape.
Redraw delegates MoveAll -> Shuffle -> Draw through IZoneSystem. Bottom translates
chosen hand objects into ordered bottom-placement requests and uses MoveAll; it does
not mutate library lists independently. TurnSystem's opening-hands partial bridges
round completion to turn one. ChoiceSystem routes declarations/bottom answers to the
coordinator. OpeningHandsChanged stores round state, while player departure removes
the departing participant through the lifecycle event reducer.

Read MulliganTests covering delayed redraw until all declarations, four-seat round
replay, kept-seat preservation, multiplayer free first mulligan, two-player cost,
repeated mulligans ending at zero cards and departures during declaration/bottoming.
These are existing tests, not a fresh execution. No duplicate shuffle, draw, or
bottom-placement algorithm was found. No runtime change was needed for this ownership
slice; current rules baseline remains 1,809. This source review is not a new rules
edition validation or proof of every Commander deck-construction requirement.

## Completed source audit: inspection and disclosure entry points — 2026-10-23

ManualLibraryInspection owns the manual scope and membership checks. Interpreter
inspection adapters own effect-local scopes and resolution lifetime for look/reveal,
search and arrangement. They all persist permissions in GameState.LibraryInspections
through LibraryInspectionChanged; there is no separate permission registry for UI
inspection. Manual reorder expires only the manual scope. Effect scopes survive
pauses and are closed by their corresponding operation/resolution completion.
Player departure removes scopes where that player owns or views the library; game
completion clears scopes in the lifecycle reducer.

ViewProjector filters inspections by viewer, then verifies each referenced object
still exists in the inspection owner's library. Thus a drawn/moved card is no longer
live inspection access even if its old ID remains in a historical inspection record.
LastLook and LastReveal are historical snapshots; VisibleGameObjects.Current excludes
them and includes current inspection/choice/top-card projections. GameArtworkService
uses that shared projected-object enumeration for current artwork.
It separately includes already-authorized historical look/reveal snapshots for their
artwork; this does not insert those historical objects into Current.

LibraryDisclosurePolicy.TopAvailable is used by both ViewProjector's continuous
revealed/lookable top and LibraryRevealCoordinator's tracking reconciliation.
LibraryRevealCoordinator owns identity renewal and tracking when a publicly revealed
library card becomes hidden/revealed again. The interpreter's shared identity helpers
update only the applicable operation's saved references and inspection scope after
reorder; search and arrangement use those helpers rather than reimplementing physical
card correspondence. Reconciliation at interpreter boundaries and game settlement
calls the same coordinator, not independent disclosure algorithms.

Read LibraryDisclosureLifetimeTests: movement removes live access without removing an
unrelated inspection; resolution choices retain access through replay and completion
expires it. Read LibraryInspectionReplacementTests: surveil's replacement pause keeps
reidentified inspected cards while another viewer receives none. Existing payment-top
coverage is recorded separately in the acceptance audit. No new test execution or
production change in this review. This closes the source ownership slice, not every
privacy scenario or UI acceptance requirement. Interrupted payment ownership remains
open; current rules baseline remains 1,809.

## Interrupted library payments: ownership map and unresolved contract — 2026-10-23

Read CostPayment.Begin/Continue/Resume, CostPaymentStep capture/merge,
CastingSystem.ActivateCore and activation continuation, PaymentRecoveryPlan,
PaymentRecovery, PaymentRestoration, PaymentTriggerRecovery and ChoiceSystem.Resume.

| Owner | Responsibility |
| --- | --- |
| CostPaymentOrder | Binds announced ordering to per-cost selections before execution. |
| CostPayment | Runs costs, captures completed/interrupted steps, returns unpaid costs and structured refusals from One. |
| CostPaymentStep | Captures before/after object, player, zone and movement-history changes; merges resumed segments. |
| CastingSystem activation continuation | Retains the announced activation, journal provenance and initial library tops; resumes unpaid costs and creates the paid activation. |
| ChoiceSystem | Resumes the pending operation and deferred batches, then hands the resulting outcome to activation continuation. |
| PaymentRecoveryPlan | Classifies supported completed steps as retained/reversible/unsupported and propagates pure movement dependencies backward. |
| PaymentRecovery | Coordinates validated restorations in reverse payment order; delegates movement, mana and property-specific checks. |
| PaymentRestoration / PaymentRecoveryApplied | Replays the accepted restoration without emitting ordinary gain/move occurrences. |
| PaymentTriggerRecovery | Filters only trigger/event occurrences whose recorded payment origins were reversed. |

The normal activation path still admits a pending final MillCost or library
ExileFromZoneCost, with no unpaid following cost. The internal BeginActivationPayment
path is broader for recovery development; its existence is not normal-command support.
Spell/mana-ability CostPayment.Pay still rejects a remaining continuation.

Unresolved ownership contract: ChoiceSystem evaluates resumable.Resume and deferred
operation completion before calling RetainPayment. A RefusedException inside that
resumed operation therefore has no returned partial Outcome for the payment coordinator
to record or recover. CostPayment's catch surrounds a subsequent One call, not this
outer resumption boundary. This is a source boundary, not a newly reproduced defect:
unpayable replacement PayCost already has separate handled behavior. Before extending
admission, reproduce a real throwing resumed operation and determine the retained
operation outcome needed by recovery. Do not add a catch that discards library events
or reconstructs partial state from the exception alone.

No runtime change or new test execution in this audit. The map locates responsibilities;
it does not certify all recovery combinations, complete the whole ownership requirement,
or establish MTGO's internal recovery design. The payment recovery requirements in the
readiness table remain open.

## Shared deferred-batch completion coordinator — 2026-10-23

Extracted ZoneBatchContinuation.Complete from ChoiceSystem.FinishZoneBatch. It owns
resuming remaining movement proposals or retaining them (with per-source inherited
history and evaluation snapshot) on another pending choice. It has no turn service
or turn-advance responsibility. ChoiceSystem delegates completion and separately
applies its existing AdvanceAfterBatch policy. The zone system still owns movement
execution; no second movement loop or payment loop was introduced.

Full rules suite passes 1,819 (`library-batch-continuation-owner-rules.log`), including
cleanup advancement, commander batch choices, serialized library batches and inherited
history cases. This establishes the shared dependency needed for interpreter payment
resumption; PayCost itself is not wired to it yet and paused-cost support remains open.

### Action-independent recovery evidence — 2026-10-23

PaymentRecoveryPlan and PaymentRecovery previously consumed ActivationPayment,
although their recovery decisions use only Steps, JournalId and HasEventOrigins.
PaymentRecoveryJournal now owns that evidence shape. The existing activation entry
points adapt to it; the shared Apply overload receives the priority holder separately.
No activation command is fabricated for effect payments, and no second restoration
algorithm was introduced. Existing serialized activation/event shapes are unchanged.

This extraction is preparation, not effect-payment recovery completion. The
interpreter still needs to capture/extend a serialized journal at every pause, carry
event provenance through its outcome, and report recovered unpaid status back to
PayCost/MayPay. PaymentRecoveryApplied currently clears Pending and grants priority;
resolution recovery must preserve resolution control instead. Trigger cancellation
must use the same journal provenance as activation recovery. Do not reuse Apply in
the interpreter until those contracts are accounted for.

Rules: 1,833 passed (library-recovery-journal-rules.log).

### Recovery priority policy — 2026-10-23

The shared PaymentRecovery.Apply overload now accepts preservePriority. Its
PaymentRecoveryApplied event records PreservePriority (default false for existing
activation behavior and historical logs). When true, restoration preserves the
current priority holder and pass sequence rather than granting priority mid-effect.
Pending is still cleared as part of recovery; interpretation of paid/unpaid and the
remaining resolution remains the interpreter's responsibility.

EffectPaymentRecoveryTests exercises the shared owner without an activation record:
a captured life-payment step is refunded after a library-cost failure, its reversal
provenance and journal reservation survive serialized replay, and both priority
policies are checked. Refunds emit no new life-change or priority-granted occurrence.
Rules suite: 1,835 pass (library-recovery-priority-rules.log).

The interpreter does not yet call this recovery path. Effect-journal capture,
continuation, provenance and unpaid branching remain required to fix the stuck
prompt. This check proves the shared recovery behavior, not end-to-end recovery.

### Interpreter payment-origin transport — 2026-10-23

EffectRun now retains PaymentOrigins alongside its event list. Merge offsets incoming
indexes, and Resolve/Run/Resume result creation preserves the map. ProposeThrough
remaps origins when removing ChoiceRequested events before rewrapping a pause; it
previously rebuilt the event list with no origin information.

InterpreterPaymentOriginTests uses a replacement test double to supply marked
occurrences, including an occurrence after a discarded choice event. Synchronous
resolution and two successive pauses preserve the origins at the correct result
indexes, while stack completion remains unmarked. Full rules: 1,837 pass
(library-interpreter-origins-rules.log).

This proves transport through the interpreter for supplied origins, not creation of
effect-payment provenance. Cost-step annotation and any remaining outer-wrapper
transport must be verified before setting effect journals HasEventOrigins=true or
invoking recovery. The unpayable-later-cost prompt remains unresolved.

### Deferred-choice origin retention — 2026-10-23

Five wrappers appended a ChoiceRequested by constructing a new Outcome and thereby
lost its PaymentOrigins: ZoneBatchContinuation, ChoiceSystem.AdvanceAfterBatch,
ChoiceSystem.RetainPayment, LibraryShuffleContinuation and LibraryDrawCoordinator.Defer.
They now preserve outcome metadata while changing State/Events. Three regressions
exercise deferred movement, shuffle and draw continuation with an existing marked
life payment, verify the exact origin index, and replay the resulting pending state.
Rules: 1,840 passed (library-continuation-origins-rules.log).

The draw proposal-normalization loop separately expands an offered event into
terminal movement/draw events and currently reconstructs its outcome. That mapping
needs dedicated review/testing; this change does not claim all library origins are
preserved. Effect cost-step annotation and recovery invocation also remain unfinished.

### Draw proposal expansion retains origins — 2026-10-23

The draw normalization loop discarded offered PaymentOrigins and origins from earlier
draws in the same batch. Three regressions reproduced empty maps for two draws from
libraries of zero, one and two cards (library-draw-origins-red.log).
The loop now preserves earlier origins, indexes each offered event, and assigns a
marked proposal's origin to each terminal event it produces. An awaiting draw's
movement and committed draw therefore retain the same causal origin. Unmarked
life events around the proposal remain unmarked, and indexes remain correct after
one-to-two expansion and across later draws/empty-library attempts.
Tests also replay the terminal events and verify hand size, life and empty attempts.
Full rules: 1,843 pass (library-draw-origins-rules.log).
Effect-payment step annotation and recovery invocation are still required; this
fix alone does not enable recovery or resolve the pending-prompt defect.

### Explicit mill occurrence and matcher — 2026-10-23

Added CardMilled(Player, Card snapshot), a non-mutating event recorded only after
actual mill movement. TriggerMatching now recognizes Mills through this event,
checks the player and optional card filter against the recorded arrival snapshot,
and carries the card/player as trigger subjects. The event compatibility manifest
includes CardMilled. Tests serialize explicit graveyard/exile occurrences, check
player and creature/land filtering, and verify ordinary library-to-graveyard movement
does not fire a mill trigger. Rules: 1,855 pass (library-mill-trigger-rules.log).

This is the event/matcher contract only. MillCost and interpreter Mill currently do
not emit CardMilled; producer integration is required. It must preserve the selected
source identities through replacement pauses, mark actual arrivals once, preserve
payment provenance, and distinguish prevented movement from redirected movement.
Multi-player milling, hidden destinations, additional replacement-created cards and
post-mill movement still require acceptance. Do not claim full mill-trigger support.
The repository rules text 701.17a-c guided this boundary; no rulebook edits were made.

### Mill-cost emission through shared continuation — 2026-10-23

LibraryMillContinuation records CardMilled for actual ObjectMoved events whose
source was selected for milling and whose destination leaves the library. It carries
remaining source identities/players in private ChoiceContext.PendingMills through
pauses, suppresses duplicate arrival events, and preserves existing payment origins.
ObjectCosts.Mill uses this coordinator after the shared zone batch; the shared
ZoneBatchContinuation invokes it again as deferred movements complete. ChoiceSecrets
removes PendingMills from all player views.

LibraryEffectPaymentTests now require exactly one CardMilled per completed mill
across mandatory/optional multi-card/multi-cost pauses and source removal. The game
settlement recovery test now uses Mills rather than ZoneChange and passes both
immediate and paused paths: refunded life trigger is canceled, actual mill trigger
remains. Full rules: 1,855 pass (library-mill-cost-events-rules.log).

Interpreter Mill still uses its resumable Move path without invoking this coordinator.
That producer needs integration. Redirected/prevented movement, hidden destinations,
replacement-created extra cards and multi-player effects still need dedicated
acceptance. This checkpoint does not establish every mill operation.

### Interpreter Mill uses shared emission — 2026-10-23

Extracted the existing Move arm into MoveObjects; both Move and Mill keep the same
selection, arrival memory and resumable relocation path. Mill identifies relocation
as milling and invokes LibraryMillContinuation for actual events. ProposeThrough
retains private PendingMills when wrapping its choice and completes emission on
resume before following instructions execute. Payment continuations keep their
own inner mill context; the outer choice does not duplicate it.

Five new cases cover empty/short/full libraries, two-card instructions, replacement
pauses, serialized replay, private source redaction, exactly-once mill events and a
follow-up that waits for completion. All eight focused MillTriggerTests pass. The
first full run aborted with an internal CLR error in JSON metadata configuration;
no assertion failure was reported. The full retry passes all 1,860 rules tests
(library-mill-effect-events-rules-retry.log; crash log retained separately).
Redirected/prevented movement, hidden destinations, replacement-created extra cards
and multiplayer milling still require dedicated acceptance.

### Explicit mill intent for replacement matching — 2026-10-23

Six tests distinguish ordinary library movement, Mill and MillCost, each with/without
a competing replacement. The ordinary-movement cases reproduced WouldMill replacing
a non-mill move (library-mill-intent-red.log). MoveRequest and ObjectMoved now carry
optional MilledBy. Mill producers supply it; ZoneSystem copies it to immediate and
deferred proposals, and WouldMill requires it and matches the named player.
Interpreter milling uses the batch seam for the marked single-card proposal while
retaining the existing continuation/emission owner. Fallback MoveAll refuses marked
requests rather than silently dropping intent. Unmarked ordinary movement remains
eligible for generic WouldBePutIntoZone replacements, but not WouldMill.

All six cases pass with serialized reload between answers, including the second
cost-batch proposal. Full rules: 1,874 pass (library-mill-intent-rules.log).
Pre-marker paused logs have no explicit mill intent; their behavioral migration is
not established by this test. Multiplayer, hidden destinations and replacement-
created extra cards remain separate acceptance requirements.

## 2026-10-24 — partial nested recovery implementation boundary

Inspected PaymentRecoveryJournal, PaymentOrigins, PaymentRecovery,
PaymentRecoveryPlan, PaymentMovementRecovery and EffectInterpreter.Payment against
the remaining partial-nested requirement. This is an implementation dependency,
not a reason to loosen the current refusal guard.

Concrete missing information:
- PaymentRecoveryJournal.NestedOrigins groups nested origins by outer step only.
  PaymentNestedRecovery therefore cannot distinguish retained and reversed
  occurrences within the same step, and correctly refuses that shape.
- PaymentOrigins.Events already accepts eventOffset and excludes choice-control
  events. CompleteEffectPayment uses prior.Steps[^1].Events.Count when stamping
  resumed outer origins, but PaymentRecoveryJournal.CompleteOperation passes no
  eventOffset when observing ownership. Step-only grouping currently masks that
  discrepancy. An occurrence-level ownership map must use the accumulated history
  offset, including repeated pauses, before it can authorize partial recovery.
- Effect journals begin with HasEventOrigins=false. PaymentMovementRecovery.Partition
  requires true for dependent/independent movements in one step. Enabling that flag
  requires proof that every persisted step event has stable numbering across initial
  execution, repeated resumes, recovery and nested-origin preservation.
- Legacy journals have no occurrence map. NestedOriginsComplete proves whole-step
  coverage only; it must not be reused to assert finer coverage for old logs.

Required implementation order:
1. Persist outer step/event → nested origin ownership, with a separate completeness
   marker whose missing serialized value remains false. Use JSON-supported keys.
2. Add initial/resumed/repeated-resume round-trip tests for monotonically increasing
   event indexes, excluding ChoiceRequested/ChoiceAnswered; verify view redaction.
3. Teach PaymentNestedRecovery to classify each mapped occurrence using the actual
   reversed event set, rejecting missing coverage and conflicting retained ownership.
4. Prove event coverage before enabling partial effect-payment recovery. Exercise a
   generated movement batch with one card dependent on a retained library action and
   one independent card; include nested life changes and trigger cancellation, normal
   commands, replay and legacy refusal controls.
5. Run rules/API suites and live acceptance for the resulting supported path.

No recovery behavior changed in this audit. Whole-step mixed/two-level tests remain
valid; they do not prove this missing occurrence-level contract. The active library
plan remains incomplete.

## 2026-10-24 — earlier activation pause gate review

Reviewed CastingSystem.ActivateCore in CastingSystem.cs and
CastingSystem.ActivationPayment.cs after the partial effect-payment work.
The normal activation gate still permits only a paused final MillCost or
library ExileFromZoneCost. Earlier pauses use internal BeginActivationPayment.

A concrete prerequisite remains before broadening that gate:
- ActivateCore assigns StepOrigins to the outcome instead of preserving nested
  progress.Outcome.PaymentOrigins alongside the outer ownership map.
- ResumeActivationPaymentProgress similarly regenerates EventOrigins/StepOrigins
  for the interrupted operation and subsequent steps, losing independent nested
  identities from those outcomes.
- ActivationPayment stores steps/event coverage but no nested ownership evidence.
  PaymentRecoveryJournal.From reconstructs only those older fields; it cannot
  supply NestedOrigins/NestedOccurrences/completeness to the shared recovery path.

Therefore successful effect-payment partial recovery does not establish the same
capability for an earlier activation pause. Before changing admission, activation
must retain the same shared ownership evidence through initial payment, repeated
resumes and recovery, with legacy defaults conservative. Prefer extracting a shared
ownership value/helper rather than duplicating the effect journal's tracking logic
or persisting a second copy of all payment steps.

Next regression should use an activation whose earlier library-cost replacement
starts a nested payment, then fails a later cost. It must demonstrate nested versus
outer trigger retention/cancellation after replay, through the normal command path
once admitted. Existing final-cost acceptance remains valid. No gate or runtime
behavior changed in this review; no additional test run claimed.

## 2026-10-24 — shared nested evidence for activation recovery

The prerequisite identified above now has an implementation. `NestedPaymentEvidence`
owns nested step/occurrence tracking for both effect journals and activation payments.
Activation retains this value alongside its existing steps; it does not duplicate
the step history. Initial payment and resumed operations preserve nested origins
when combining them with outer origins. `PaymentRecoveryJournal.From` carries the
evidence into the shared recovery path. Missing legacy evidence defaults to incomplete.

`PartialNestedLibraryRecoveryTests` now includes an internal activation case:
nested sacrifice/life payment, repeated serialized choices, later failed mill,
life refund, retained card in the library, and exactly one retained departure
trigger. Answers use normal commands. The initial public activation is explicitly
asserted refused: this test does not establish admission of earlier pauses.
All four targeted cases and all 1,914 rules tests pass
(`library-activation-evidence-tests.log`, `library-activation-evidence-rules.log`).

Before broadening activation admission, cover failure and unsupported cost
combinations through the public entry point. Exceptions inside resumed operations
still require the separate recovery contract described above.

## 2026-10-24 — missing activation evidence must not imply no nesting

A new regression removed nested evidence from the generated partial activation
payment. Before the fix, the final answer was Applied: neither the remaining
operation nor the default evidence reported foreign origins, so recovery accepted
an unproven absence of earlier nested payments. Activation recovery now explicitly
requires complete nested-origin coverage before applying refunds/restorations.
This intentionally refuses recovery of older activation records lacking that
coverage; it does not attempt to reconstruct unavailable ownership.

The regression verifies refusal with default legacy evidence and successful
recovery from the original complete pending state. Synthetic dependency/mana
fixtures now explicitly initialize complete empty evidence for their non-nested
histories. Full rules suite: 1,915 passed (`library-activation-legacy-rules.log`).
Public earlier-pause admission remains unchanged. This closes a compatibility
guard gap; it does not complete the overall activation/recovery contract.

### Serialized compatibility verification

The activation legacy regression now serializes the generated activation payment,
removes `nestedEvidence` from the JSON, and deserializes it with the event-log
options. Both coverage flags default to false; the normal answer command refuses
recovery. The original complete record accepts the same answer and restores life
without leaving a pending choice. All five partial nested recovery cases pass
(`library-activation-omitted-field-tests.log`). This supersedes the earlier test's
in-memory default-value substitution. No production code changed in this follow-up.

Rechecked admission against `ChoiceSystem.Resume`: the resumed interpreter,
remaining draws, zone batch, and shuffles finish before `RetainPayment` receives
the outcome. A thrown refusal in that chain still bypasses payment recovery.
Broadening the activation gate therefore needs a partial-outcome failure contract,
not just complete ownership metadata. Existing diagnostic refusal/retry tests
establish atomic command rejection, not automatic recovery from every failure.

## 2026-10-24 — operation resumption boundary

Extracted `ChoiceSystem.ResumeOperation` from the nested expression in `Resume`.
It owns the existing ordered operation pipeline; `RetainPayment` owns the subsequent
payment continuation. No exception conversion, admission change, or automatic
failure recovery was introduced. All 1,915 rules tests pass
(`library-resume-boundary-rules.log`). The implementation contract and remaining
proof requirements are in [LIBRARY_RESUMED_FAILURE_CONTRACT.md](LIBRARY_RESUMED_FAILURE_CONTRACT.md).
This separation is preparatory and does not close the resumed-failure requirement.

## 2026-10-24 — one choice display contract

The off-stack effect omission exposed duplicated policy in `ChoiceSecrets.Withheld`:
both the unchanged predicate and the clearing expression enumerated private fields.
A second regression reproduced `AdvanceAfterZoneBatch` surviving redaction
(`library-choice-allowlist-red.log`). No API or client consumer of that internal
flag was found. The helper now constructs the permitted context from Source,
AbilityIndex, EffectPath and filtered Extra, leaving continuation fields at defaults.
Record equality preserves the existing unchanged-reference behavior for already
public contexts. Server state is never mutated.

This removes the duplicate private-field inventory and prevents populated new
continuation fields from being copied implicitly. It does not replace the existing
Extra-key secrecy policy. Tests verify location/options preservation, flag removal,
idempotence and the earlier off-stack replay/privacy checks. Full rules: 1,921 passed
(`library-choice-allowlist-rules.log`). This is an improvement over our previous
implementation, not a demonstrated comparison with MTGO.

## 2026-10-24 — properties on movements retained by library operations

Reproduced a completed-step recovery refusal with supported replacement effects:
gain one life, tap the sacrificing artifact, move it to the graveyard; a later
mill replacement returns it to the library and shuffles, then the next mill cost
is unpayable. The prior partition rejected the tap occurrence even though
`Describe` had already validated it against the departure snapshot.
`library-dependent-source-tap-red.log` records one failure and eleven passing cases.

`PaymentMovementRecovery.Partition` now relies on that existing validation and
classifies moved-object property occurrences with their movement. Properties of
the retained object stay retained; properties of a restored object's departure
and arrival identities are reversed. Residual independent properties continue to
use the existing property recovery implementation. No duplicate validator or new
public admission path was added.

The expanded replay regression checks the retained artifact's tap event index,
library order and life refund. Existing mixed-batch cases continue to check
restored state and movement triggers. An exploratory extension that also tapped
the independent land before its batch movement was rejected by Describe: the
batch retains its pre-batch departure snapshot, which does not include that tap.
That distinct snapshot/validation case remains unsupported and needs a separate
regression and policy decision; it is not fixed by this partition change.

An exploratory trigger variant exposed a separate limitation: BecomesTapped on
the artifact produced no stack trigger when that artifact departed during the
same replacement (`library-dependent-source-tap-tests.log`, expected one, actual
zero). That exploratory fixture was removed from this recovery regression rather
than asserting the incorrect behavior as the contract. Trigger creation for this
case remains unresolved; the event-origin assertions do not establish its runtime
trigger behavior. Earlier interrupted activations remain internally exercised
and publicly refused. Live multiplayer acceptance of this new case is unverified.

Final validation: 1,954 rules tests passed in
`library-dependent-source-properties-rules.log`; 840 API tests passed in
`library-dependent-source-properties-api.log`. Client source was unchanged;
the existing client verification was not rerun or presented as new evidence.

## 2026-10-24 — chained payment movement ownership

Extended the existing movement descriptor and partitioner to handle a validated
arrival becoming a later departure within the same payment step. Restoration still
uses PaymentMovementRestoration in reverse order; no parallel rollback engine was
added. CostPaymentStep owns capture of all touched zones, including net-unchanged
intermediate zones. Actual intermediate departures use the same property validator
as single-hop movements. Dependency traversal includes the current step's chain
before later retained library moves, keeping the whole dependent path together.

Reversal and retention regressions pass, including life refund, original object
properties, unrelated exile contents and serialized recovery. Full rules: 1,965
passing in `library-chain-recovery-rules.log`. Paused/mixed/commander chain cases
and live acceptance remain, as recorded in LIBRARY_RESUMED_FAILURE_CONTRACT.md.
