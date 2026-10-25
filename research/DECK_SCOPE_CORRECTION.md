# Deck and library scope correction

## Test-scope and obsolete-scaffold cleanup — 2026-10-25

Deck acceptance now runs separately through tools/verify-test-scope.ps1, defaulting
to Scope=Deck. Its verified results are 55 rules tests and 80 API tests (135 total).
Existing engine/API regressions were run separately: 1,579 rules and 755 API tests
passed. See tests/README.md, deck-scope-verification.log, and
existing-regression-verification.log. Full-suite totals must not be called deck tests.

Removed seven tests whose only subject was an obsolete unimplemented placeholder,
the unused AllStubs factory, and eight otherwise unreferenced stub classes.
Concession/post-game refusal assertions are unchanged and now use production
composition. The remaining reduced composition and five test doubles moved into
the test project as LegacySubsystems and Stubs; they no longer ship with the engine.
The production Wave2 composition is unchanged. Casting, combat, triggers, and
their meaningful behavior tests remain because current application paths use them.
No keyword functionality was added, and no commit was made.

## Audit follow-up — 2026-10-25

The user requested fixes for every reported audit finding. Removed the unused
casting payment methods and descriptor, payment-order command/scenario fields,
payment view flags in both repositories, and payment provenance propagation through
outcomes, replacements, interpreter runs, draws, and triggers. Removed unused mill
intent and pending-mill metadata. The three tests dedicated solely to removed
provenance were deleted; mixed tests retain their unrelated assertions and no
longer forward or expect removed contracts. A new regression requires obsolete
payment-order input to be rejected rather than silently ignored.

Legacy saved-payment records and the visibility guard remain intentionally used
by deserialization and privacy protection. The historical CardMilled discriminator
still supports replay of old observations. Recovery events now throw a specific
UnsupportedGameLogException, mapped to HTTP 409 and a clear SignalR error. A replay
regression checks both transports and that the saved log is unchanged.

Removed obsolete payment/mill/recovery fixture definitions and browser branches.
The retained manual-action harness is tools/TableQa/verify-library.js, with explicit
supported modes documented in its README. The remaining disclosure harness also
no longer contains its removed mill-observer scenario. Both scripts pass the
unused-variable check. Removed an unused collection printing helper/cache/import
and two unused deck-handler parameters, updating their template callers.

Verification: 1,641 rules tests, 835 API tests, and npm run verify with 1,896 client
tests and production build passed. Strict TypeScript noUnusedLocals and
noUnusedParameters passed. The QA host builds with zero warnings/errors. Logs:
audit-cleanup-rules.log, audit-cleanup-api.log, audit-cleanup-qa-build.log, and
sibling audit-cleanup-verify.log. The pre-existing card-modal stylesheet budget
warning remains. Four-seat live search passed, including private inspection,
pending reload, selection, hand privacy and broadcasts; see
audit-cleanup-search-live.log. Its fresh landscape capture was inspected.
Four-seat live draw also passed replacement decisions, pending refresh, broadcasts
and reload; see audit-cleanup-draw-live.log. Owned QA hosts were stopped. No commit.

The following section records the earlier cleanup checkpoint; this audit follow-up
supersedes its statements about retained seam methods and verification totals.

## Earlier approved cleanup — 2026-10-25

The user explicitly approved removal of the shared payment/recovery expansion
and its dedicated tests, then directed completion. That supersedes the older
pending-approval notes below.

Removed active scry/surveil arrangement protocols, mill continuation and trigger
handling, mill-cost payment, activation/effect payment journals, recovery planners
and executors, the payment-preview API, and payment-order UI. The pre-existing
keyword handlers and ordinary casting/payment path remain. Suspended replacement
payments are refused atomically; they cannot commit a partially paid activation.
Dedicated expansion tests and live verifiers were removed. Generic deck, movement,
search, privacy, and pre-existing casting tests remain.

Compatibility deliberately retained: private saved-payment data records, the
library visibility guard, legacy seam signatures for existing decorators, and an
obsolete recovery-event discriminator. They provide no active payment/recovery
feature. Saved games with pending legacy payments or recovery events are explicitly
unsupported and require a new game; they are not silently replayed incorrectly.
Mixed privacy/mana tests remain unchanged. Recovery source/test originals were
saved outside the repository under the Windows temporary directory.

Verification: 1,650 rules tests, 833 API tests, and `npm run verify` with 1,897
client tests plus production build pass. Logs: `library-final-cleanup-rules.log`,
`library-final-cleanup-api.log`, and sibling `library-final-cleanup-verify.log`.
The existing card-modal stylesheet budget warning remains.
Live four-player basic search passed on table
`523e104b-fc7b-4639-90f3-17355b577ad7`; fresh landscape capture inspected.
Live four-player Commander opening/mulligans passed on table
`371bdd16-8efd-48e5-b83f-2b38cf717414`: deck sizes, mulligan bottoming after reload,
privacy, kept-hand preservation, and first draw. Fresh completion capture inspected;
owned QA hosts stopped. See `library-final-cleanup-opening-live.log`.
The corrected deck/basic-interaction scope and active-expansion cleanup are complete.
No commit authorized or made.

## Latest disposition — 2026-10-24

The user explicitly superseded the earlier preservation instruction: remove the
extra rules code and its dedicated tests. Basic deck/library tests remain intact.
This cleanup is **partial**, not complete.

Removed the added scry/surveil arrangement coordinator, planner, runtime,
continuation helper, complete-partition answer validator and payload, completion
events and trigger matching, keyword-specific client menus/draft logic, and their
dedicated unit/API/live-test cases. The pre-existing scry/surveil handlers remain;
generic ordering, search, inspection, draw, shuffle and mulligans remain.
Mixed test files retain their unrelated assertions; only the removed feature's
cases were deleted. No expected results were loosened to make retained behavior
pass. The earlier immutable-test policy still applies to retained tests.

Automatic approval review rejected removal of the shared cost-payment continuation
and recovery implementation because it also serves casting callers. Its ten
temporarily removed dedicated test files were restored byte-for-byte from backups.
Mill, payment/recovery and other shared rules integration remain present. They
must not be reported as removed or as completed deck cleanup. The next approval
must explicitly cover removing the shared casting-payment/recovery additions and
updating dependent callers, with their dedicated tests. No commit has been made.

Read-only dependency check after the rejection confirms this is not an isolated
file deletion. The affected callers are:

- `Casting/CastingSystem.cs`: activation begins payment and invokes recovery.
- `Casting/CastingSystem.Choices.cs`: payment ordering and selected-cost discovery.
- `Choices/ChoiceSystem.Resume.cs` and `Engine/Seams.cs`: suspended activation resumes.
- `Interpreter/EffectInterpreter.Backlog.cs` and `EffectInterpreter.Payment.cs`:
  costs paid during effect resolution.
- `State/GameState.cs`: persisted unpaid-cost continuation.
- `Legal/LegalActions.cs`, `Views/ViewProjector.cs`, and
  `Library/LibraryDisclosurePolicy.cs`: action availability, payment indicators,
  and library visibility during payment.

Any approved rollback must update these callers together and preserve the
pre-existing casting/payment contracts, then rerun both backend suites, client
verification, and basic library live checks. Deleting the helper files alone
would leave broken references. No shared payment source was removed.

Verified after the completed removal:

- Rules: 2,076 passed (`library-scope-removal-rules.log`).
- API: 844 passed (`library-scope-removal-api.log`).
- Client: `npm run verify` passed, 1,909 tests and production build
  (`../mtg-client/library-scope-removal-verify.log`). Existing card-modal style
  budget warning remains (18.05 kB versus 16 kB).
- Live four-player basic search passed on table
  `6b7db04e-9241-4ecc-87e4-811e96ed9dbf`: filtering, private inspection, pending
  reload, submission, hand privacy and all-seat broadcast. Fresh landscape capture
  inspected; owned QA host stopped (`library-scope-removal-search-live.log`).

The sections below record earlier decisions and checkpoints; the disposition
above supersedes their preservation and pending-scope-decision language.

The user's latest correction supersedes the earlier keyword/parity expansion:
define what a deck is and how to interact with it. Do not continue implementing
keywords, card effects, replacement chains, payment recovery or researching exact
MTGO keyword gestures as prerequisites for this task.

## Existing model to reuse

`MtgEngine.Domain/Models/Collection.cs` already represents a saved deck with
`IsDeck`, format, primary/secondary commander Oracle IDs and `CollectionCard`
entries. Entries reference Oracle/printing IDs, quantities and main/side/maybe
boards. They are not individual game objects. Reuse this model; do not invent a
second card catalog or replace collection/deck-builder storage as part of this task.

A game library is an ordered sequence of existing `ObjectId` instances in
`GameState` under the player's library `ZoneKey`. Preserve that single authority;
do not add a second mutable library collection beside it.

## Basic interaction contract

| Operation | Existing owner / contract |
| --- | --- |
| Load a saved deck | GameLobbyDecks.LoadAsync: caller-owned Collection with IsDeck, matching format; expand main-board quantities into StartingCard records and separate commanders. Side/maybe entries are excluded. |
| Create a game library | Existing zone creation allocates individual game objects from StartingCard records. Saved collection rows remain definitions of composition, not mutable game cards. |
| Count | GameState.In(ZoneKey.Of(Zone.Library, owner)).Count; public projection exposes a count without revealing identities. |
| Draw one | LibraryDraw.Apply: current top instance moves through existing zone identity logic; record a draw or an empty-library attempt. Rules adapters decide whether to replace it. |
| Shuffle | LibraryShuffle.Apply: use the shared seeded RNG, record its advance and the committed order. No effect selection in the primitive. |
| Inspect / stop inspecting | ManualLibraryInspection.Set, gated by ManualLibraryCoordinator and ManualLibraryPolicy. Inspection does not move cards or become a second library. |
| Find matching cards | LibrarySearch.Find: inspect the supplied ordered prefix, then match; returns IDs without movement. Matching criteria come from the caller. |
| Select search results | ILibrarySearchInteraction supplies the prompt transaction; the current rules adapter preserves saved choices and replay. It is not part of the basic matching algorithm. |
| Put at a position | LibraryOrder.Place: reposition an existing ID, preserving remaining order; negative/oversized positions mean bottom. Command authorization checks ownership/current inspection first. |
| Arrange | LibraryOrder.Arrange; manual command requires the full current inspected membership exactly once. No card duplication or alternate state store. |
| Move between zones | Existing IZoneSystem.Move owns movement and identity. The library boundary must not copy that algorithm. |
| Deal / keep / mulligan | OpeningHandCoordinator composes the existing operations and owns the opening sequence. |

Core constraints: distinguish saved entries from physical game instances; maintain
one authoritative ordered state; do not expose hidden IDs without inspection;
validate ownership and current membership; keep request retry/replay behavior;
delegate movement and randomness to their existing owners. Card keywords and
effect policy are callers/adapters, not additional deck responsibilities.

## Disposition of inspected code

| Area | Disposition | Concrete boundary |
| --- | --- | --- |
| Collection/CollectionCard | Keep existing model | Saved composition, quantities, commander designation and card references. Unrelated collection modifications are not attributable to this task merely because they are dirty. |
| CommanderDeckAdmission / CommanderDeckValidator | Keep deck admission boundary | Validate composition using existing metadata; no card-effect execution. |
| LibraryOrder.Place / Arrange | Keep generic ordering primitive | Reposition existing instances and preserve membership. No keyword policy. |
| ManualLibraryCoordinator | Keep basic interaction responsibility; extract coupling | Own draw/shuffle/look/stop/search/reposition/arrange authorization and request identity. Its Search currently builds Cards.Search and calls IInterpreter.Run: this is a concrete coupling to remove from the deck interaction boundary. |
| LibraryDrawCoordinator | Split responsibility | Basic draw belongs; replacement selection, deferred effect batches and payment-origin propagation belong to external rules integration. Do not create a competing movement/identity algorithm. |
| LibraryShuffleCoordinator | Split responsibility | Randomize existing order using the shared RNG/event path; replacement offers and deferred completion belong outside the basic library operation. |
| OpeningHandCoordinator / OpeningHandPrompts | Keep opening orchestration | Deal, keep, mulligan, bottom chosen cards. Reuse the basic operations. |
| LibraryArrangementPlanner / Coordinator / Continuation / Answer | Outside corrected scope | These explicitly implement scry/surveil decision protocols. Generic ordering remains; keyword producers must live outside the deck interaction boundary. |
| LibraryDepartureObservation / mill continuations, payment/replacement recovery | Outside corrected scope | Do not expand, use as acceptance gates, or describe as necessary deck functionality. Identify shared callers before any removal. |
| Library inspection, details, basic menus, transport identity/privacy | Keep | Authorized count/inspection and generic interactions, without requiring keyword-specific menus. |
| ChoiceSelectionService scryDestination / scryTopOrder and scry/surveil UI additions | Outside corrected scope | Mixed into shared selection/board files. Separate their changes from generic ordering, search and mulligan controls before removing any hunk. |
| Existing acceptance tests | Preserve | Do not delete or weaken tests to make a narrowed implementation pass. Distinguish core deck contracts from external rules integration tests. |

## Cleanup sequence

1. Inventory dependencies and staged versus unstaged changes in shared files.
   Both repositories contain unrelated modifications; a wholesale reset is unsafe.
2. Extract the basic interaction contract from the current implementation, using
   existing card instances, zone movement, ordering, RNG and events. Keep one owner
   for each operation. Rules effects may call this contract from outside it.
3. Decouple manual search from the effect interpreter, then separate draw/shuffle
   rules adapters from their generic operation. Keep existing behavior/tests intact
   while establishing that boundary; do not add keyword behavior.
4. Remove only attributable out-of-scope additions after checking their callers
   and retaining a recoverable patch. Never remove unrelated engine work or tests.
5. Verify the core deck interactions and show the resulting boundary and UI before
   committing. Prior large test totals and the earlier 95% estimate are not a
   completion measure for this corrected scope.

This is a source-backed disposition audit, not a completed cleanup. No production
files or tests were changed by this audit; no commit was made.

## First extraction

LibraryShuffle.Apply now owns RNG advancement and the committed reorder event.
LibraryShuffleCoordinator retains replacement orchestration and delegates its
terminal operation to that owner. The sole pending-randomization producer sets
Shuffle=true; the generic operation creates the corresponding committed event.
No duplicate RNG or zone-movement implementation was introduced. The new owner
does not accept replacement, keyword, interpreter or payment dependencies.

Existing shuffle, manual-library, LibraryZoneService and event-log tests passed
unchanged: 33/33 (library-core-shuffle-extraction-tests.log). Initial compilation
found a missing Outcome namespace import, corrected before testing. No tests were
edited. Manual search and draw separation remain outstanding; this extraction
does not claim that the whole deck boundary has been decoupled. No commit.

## Basic draw extraction

LibraryDraw.Apply(state, player) now owns the terminal single-card draw: request
the existing ZoneSystem.PrepareDraw movement, apply it and record ObjectDrawn,
or record an empty-library draw attempt. It does not select replacements, walk
effects or manage payment continuations. LibraryDrawCoordinator.Complete delegates
to this operation after external replacement decisions; it still handles ordinary
already-committed events without drawing twice. No card identity/movement logic
was duplicated. Full unchanged rules suite passes 2,103/2,103 after both extractions
(library-core-operations-rules.log). No UI change or test modification.

Search remains the next substantive boundary: ManualLibraryCoordinator.Search
constructs Cards.Search and calls IInterpreter.Run, while SearchRuntime is private
to EffectInterpreter and implements continuation/prompt/movement callbacks. Moving
only the constructor behind another interface would not remove this dependency.
The shared search operation and its persisted answer route must be separated
together; do not introduce a parallel search implementation to avoid that work.

## Ordered search primitive

LibrarySearch.Find now owns ordered-prefix inspection and predicate matching.
It takes existing instance IDs, a matching function and an optional inspection
limit, returning inspected/matching IDs without movement or mutation. It has no
effect, replacement, prompt, payment or interpreter dependency. LibrarySearchPlanner
delegates to it after resolving external rules policy; the former inline filtering
implementation was removed. Existing search/manual tests pass unchanged: 107/107
(library-core-search-extraction-tests.log).

This separates the reusable search primitive, not the entire manual-search UI
transaction. The current manual command still enters IInterpreter.Run and restores
its saved Cards.Search through EffectInterpreter.Elsewhere/Tree. That remaining
coupling is explicit; do not label manual search decoupled based on this extraction.

## Manual search dependency boundary

ManualLibraryCoordinator now depends on ILibrarySearchInteraction.Begin rather
than IInterpreter. It retains authorization, request identity and command argument
validation. Cards.Search construction and EffectContext setup moved without
duplication into Interpreter/ManualLibrarySearchAdapter. Game is the composition
point supplying that adapter. Search selection/resumption still uses the existing
rules implementation, so this is dependency inversion at the deck-control boundary,
not removal of the rules runtime or a new independent search transaction engine.

All 107 existing search/manual interaction tests pass unchanged, including
serialized standalone choices and normal answer commands
(library-manual-search-boundary-tests.log). No test or expected result changed.
This preserves compatibility while keeping effect construction out of deck controls.
Out-of-scope keyword additions have not yet been removed. No commit.

## Keyword layer separation and verification

Moved LibraryArrangementPlanner, LibraryArrangementCoordinator and
ILibraryArrangementRuntime into the Interpreter namespace/folder; moved
LibraryArrangementAnswer into Choices. Basic LibraryOrder stays in Library.
The existing continuation-key helper remains for saved-choice compatibility and
its unchanged contracts. This relocates rules responsibility; it does not delete
keyword behavior or claim the entire rules package is a standalone deck module.

Full unchanged verification after the moves: 2,103 rules tests and 846 API tests
passed (library-layer-separation-rules.log, library-layer-separation-api.log).
No client files or tests changed in these cleanup steps. The broad dirty worktree
still includes unrelated and earlier rules work, which must not be blanket-reset
or silently included in a deck-focused commit. No commit authorized or made.

## Live check after extraction

Rebuilt the isolated TableQa backend and ran the unchanged four-player manual
search verifier. Table 72ed166d-50f9-466b-b5b1-ba87ebe45a69 passed filtered search,
private inspection, reload during the pending choice, selection submission,
hand privacy and all-seat broadcast (library-core-boundary-search-live.log,
process exit 0). Inspected the fresh landscape library-controls/search-form
capture. This proves the new manual-search adapter remains connected to the
existing UI and saved-choice path; it does not claim every basic action was
rerun in this particular scenario. The owned QA host was stopped.

The rebuilt backend's four-player opening test also passed unchanged on table
2e5bb882-c5c4-4d9f-9357-f4af296d7561 (library-core-opening-four-live.log): Commander
counts, mulligan bottoming after reload, private hands, kept-hand preservation and
multiplayer first draw. Fresh completion capture inspected; fixture cards have
no artwork. Owned host stopped. LIBRARY_REVIEW.md and LIBRARY_REMAINING_WORK.md now
explicitly mark the earlier keyword-expansion checklist as historical, preventing
it from driving further work outside the corrected scope.

## Core acceptance audit and remaining disposition

Source and assertion review after cleanup confirms:

- Saved composition/ownership/commander separation: GameLobbyDecks.LoadAsync and
  GameLobbyDecksTests check owned IsDeck records, main-card quantities, commander
  removal, 99/98-card libraries and saved-game reconstruction for two/four seats.
- Instance identity: DeckSelectionTests checks distinct physical instances and
  selected printing/finish preservation across draw, movement and replay.
- Draw: ZoneChangeTests checks the exact former top card, hand/library counts,
  movement-before-draw events, new object identity and empty-library behavior.
- Shuffle: ZoneChangeTests checks deterministic seeded replay, RNG advancement,
  exact membership/count preservation and unchanged opponent order.
- Placement/arrangement: ManualLibraryMovementTests checks exact order, preserved
  objects and refusal when inspection no longer covers current membership.
- Search/privacy/retry: StandaloneLibrarySearchTests checks chooser-only options,
  no opponent inspection, serialized continuation, all supported destinations,
  one recorded search/shuffle and duplicate-request refusal. Fresh four-player
  search also passed after the adapter change.
- Opening: OpeningHandCoordinator delegates movement to the shared zone owner;
  fresh four-player opening passed after draw/shuffle extraction, including
  mulligan bottoming after reload and private kept-hand preservation.

These assertions are included in the passing 2,103 rules / 846 API suites; none
was changed during cleanup. The earlier 1,919-test client verify remains the
client checkpoint because cleanup changed no client source.

The user has now authorized removing the extra work and its tests. See the latest
disposition above for completed removals and the separate automatic approval
restriction on shared casting-payment code. No new core functional gap was
demonstrated by this audit.
