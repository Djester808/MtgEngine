# Library acceptance audit

## Top-placement authorization regression — 2026-10-24

Added a fixed LibraryRevealComponent regression exercising the real card-button
context-menu event. It verifies the offered action and emitted current option,
blocks pending/disconnected actions, clears stale-choice and revoked-inspection
menus, rejects ambiguous physical-card options and malformed ordering bounds,
and checks the visible top-position projection. No production code changed.
Existing expectations were retained. Initial test setup directly called a handler
with no DOM currentTarget; dispatching from the actual button corrected that
setup without changing assertions. It was not a production defect.

Full npm run verify passed 1,918 tests, coverage and production build, exit 0
(../mtg-client/library-top-placement-guards-verify.log). Existing card-modal size
warning remains. Production is unchanged from the four-player top-placement run
b3eea15a-6789-481a-8539-59af030d2bcd and its inspected captures. No commit.


## Saved-deck startup revalidation — 2026-10-24

Added two fixed acceptance cases to GameLobbyDecksTests for two and four seats.
They persist real collection decks and a ready lobby, edit the final guest's deck
to 99 total cards, then call GameLobbyStartup with real GameLobbyDecks,
CommanderDeckAdmission, GameLobbyStore, GameSessionService and SQLite GameLogStore.
Startup must refuse, reopen the lobby and create no session or game log. Correcting
the saved deck to 100 allows retry; reloading the game yields 92 library, seven
hand and one commander per seat, matching seat ownership. A subsequent startup
retry must return the same ID without changing the persisted log.

All 15 GameLobbyDecksTests pass (library-lobby-revalidation-tests.log); full API
suite passes 846 (library-lobby-revalidation-api.log). No production change was
needed. Existing tests and expectations unchanged. The initial compile attempt
used an xUnit overload prohibited by the analyzer; using its predicate overload
preserves the identical assertion. Card lookup/pool use existing test doubles;
this verifies the production startup/admission composition, not live Scryfall,
HTTP authentication or a real user's collection. No commit.


## Final-worktree integration refresh — 2026-10-24

No implementation or test expectations changed in this audit. Rebuilt and ran
full rules (2,103) and API (844) suites, both exit 0, recorded in
library-final-integrated-rules.log and library-final-integrated-api.log.
The current client remains the 1,916-test library-panel-stack-verify.log build.

Fresh four-player opening table 25593e00-44cb-4777-b341-c6e6a84a311a passed with
all four revisions 19 (library-final-integrated-opening-four.log). Fresh two-player
table 8992885b-9d54-4004-b4ba-33f507459810 passed with both revisions 10
(library-final-integrated-opening-two.log). Existing assertions verify exact
Commander-sized counts, mulligan bottoming after reload, hidden options, preserved
kept hands, per-player library counts and multiplayer versus two-player first draw.
Four-player bottom-choice landscape, two-player bottom-choice desktop and portrait
rotation captures were inspected. Fixtures still bypass collection-backed lobby
admission and use synthetic cards; these are opening integration, not catalog or
fresh production-lobby acceptance. All owned hosts stopped. No commit.

Public research was retried for hidden MTGO bottom-card ordering; the same Reddit
lead could not be fetched and no primary source establishing hidden order was
found. This does not change the local fixed ordering contract or justify claiming
full MTGO parity. Remaining comparison limits are recorded explicitly in the
method register; generic flags alone do not establish library requirements.


## Current four-player opening acceptance — 2026-10-24

Rebuilt the isolated TableQa host against the current engine and ran
verify-library-opening.js --four. Table 91224c86-ea8d-45ac-a036-85f052bfc3aa
passes at revision 19 on every seat; library-final-audit-opening-four.log exits 0.
Inspected the harness assertions: paid bottoming restores library count to 93,
kept opponents retain exact hand IDs, only the chooser receives offered options,
all four seats refresh at the pending decision and after completion, and the
starting player draws exactly once in multiplayer before PrecombatMain. Final
rendered pile counts match each authorized view. This is the four-player variant;
the separately recorded two-player run proves its different first-draw behavior.

Viewed library-opening-four-bottom-reload-seat-0-landscape.png and
library-opening-four-completed-seat-0-desktop.png. The landscape prompt exposes
all seven choices within its scrollable panel; the desktop table shows four
library counts and separate commanders. Synthetic cards have no artwork, so
these captures do not establish image loading. Owned host stopped after success.

Plan audit: the original Requirements and evidence table remains historical.
Current admission code has one CommanderDeckAdmission -> CommanderDeckValidator
call and explicitly checks commander membership, exact total, compatible pair,
color identity, basic-land types, legality metadata and copy limits. Inspected
CommanderDeckValidatorTests covers total/pair/color/land/copy/legality examples;
this source review is not a fresh production-lobby admission run. No duplicate
validator or named-card implementation was added. Full method parity and final
acceptance remain open; no commit.

## Complete scry validation checkpoint — 2026-10-24

The malformed complete-top partition regression now includes a null option ID.
The red run reproduced ArgumentNullException from the dictionary lookup; the
validator now refuses it before emitting ChoiceAnswered, preserving the pending
choice and library. Full verification passes: 2,103 rules tests
(library-scry-null-full-rules.log) and 844 API tests (library-scry-null-api.log).
This includes the two new multiplayer complete-partition replay cases and the
null regression. Latest unchanged client verification remains 1,899 tests plus
coverage and production build (library-scry-complete-order-verify.log).

No user MTGO session or recording is available; public-source research is the
authorized comparison path. Search surfaced a player report at
https://www.reddit.com/r/MTGO/comments/167u6hn describing last-bottom-selection
as the bottommost card. The full page could not be retrieved in this pass; this
is a research lead, not verified primary evidence or closure of bottom-order
parity. Existing implementation preserves bottom selection order and its live
all-bottom scenario passes. Remaining comparison limits include MTGO bottom
placement, multiple-window collapse, and decided scry cards remaining visible
locally. Final requirement-by-requirement acceptance remains open. No commit.

## Complete scry multiplayer replay accepted — 2026-10-24

Extended the existing APNAP ordering theory with complete all-top and mixed
partitions, retaining legacy scry/surveil cases. Each player's answer events are
serialized/replayed before a fresh engine processes the next player; assertions
check unchanged library order before all decisions, chooser-only options, private
inspection and exact final order. All six cases pass in
library-scry-complete-multiplayer-replay.log. This adds two tests beyond the last
2,100-test full suite; no newer full-suite total is claimed.

Final mixed-destination client payload rerun passes on four-player table
6295da8e-bdf9-4883-ace5-d07883abf48a (library-scry-complete-mixed-live.log, exit 0).
It verifies local first decision, exact order, deferred movement, authorized
views, per-seat and final reload. Together with complete-top and complete-bottom
logs this covers all three destination patterns after the TopOrder contract.
Owned host stopped. No production change, new client verification or commit.

## Current integrated checkpoint — 2026-10-24

Full current rules suite: 2,095 passed, zero failed/skipped, exit 0
(library-current-complete-rules.log). Includes the new unqualified-search refusal
and valid completion after replay. Full current API suite: 844 passed, zero
failed/skipped, exit 0 (library-current-complete-api.log).

Two-player Commander opening rerun passes on cb504f11-772d-4ccd-b4d4-363f9bbf3033
(library-current-complete-opening-two.log). The harness verifies Commander-size
counts, paid mulligan bottoming after reload, private choices, preservation of the
other kept hand and two-player first-turn draw handling. Bottom-choice landscape
and completed desktop captures inspected. The owned QA host was stopped.

Client production source is unchanged since the successful 1,893-test full verify
in ../mtg-client/library-search-capacity-layout-verify.log. Subsequent sibling
changes are to an older live harness only; its rerun remains unverified. Current
four-seat automatic and partial search evidence remains in
library-search-capacity-layout-live.log and library-search-partial-capacity-live.log.
This checkpoint updates suite/opening evidence; it does not establish full MTGO
action-descriptor parity or complete the requirement-by-requirement final audit.
No commit.

## Latest library UI checkpoint — 2026-10-24

The single-card surveil destination menu now corresponds to the publicly observed
MTGO interaction, using existing engine and choice-submission owners. See the top
of mtgo/LIBRARY_METHOD_PARITY.md for the recording, CR 701.25a, guarded UI mapping
and limits. Client verification now passes 1,886 tests and production build in
../mtg-client/library-surveil-menu-verify.log. Four live seats pass both top and
graveyard outcomes, privacy, exact order and reload in library-single-surveil-*-live.log.
Desktop and landscape captures inspected; no commit. Earlier client counts and
claims that this menu is absent are superseded. This is not full MTGO parity.

## Review checkpoint — 2026-10-24

The current acceptance contract is LIBRARY_IMPLEMENTATION.md, "Requirements and
evidence" and "Required acceptance standard: method traceability". Its published-
rule/server boundary expressly distinguishes independent rule implementation from
unavailable MTGO server methods. This does not waive the client action comparison.

Inspected evidence for this review:

| Deliverable | Evidence and disposition |
| --- | --- |
| Current complete rules suite | library-final-rules.log: 2,094 passed, zero failed/skipped. Includes latest opponent fail-to-find and mill departure/return cases. |
| API suite | library-current-api.log: 844 passed, zero failed/skipped. Earlier checkpoint, not a new run. |
| Client verification | ../mtg-client/library-layout-preferences-verify.log: recorded full verify and completed production build; existing 18.05 KB card-modal stylesheet warning. No client changes since that checkpoint are claimed here. |
| Four-player inspection | library-layout-preferences-live.log: table b68c7192-62e9-4750-815b-9347a6c479ab, four seats, private look, active reload, stop/expiry and all-seat final reload passed. Geometry assertions are in tools/TableQa/verify-library-payment.js. |
| Two-player opening | library-current-opening-two.log: table 9e6d56a3-2146-4d46-a7df-4d4d5f5e8564, Commander-size opening, mulligan bottoming after reload, kept-hand preservation, privacy and player-count-specific first draw passed. |
| Desktop review | tools/TableQa/library-panel-persisted-desktop.png inspected again: expanded inspection panel, visible Move handle, scrollable contents and four-player board context. Synthetic fixture cards lack artwork; this capture does not establish real-card artwork coverage. |
| Rules decisions | F1/F2 current decisions are in LIBRARY_REMAINING_WORK.md and their linked contracts. Older unresolved statements below are historical. |
| Remaining client comparison | Explicit local search confirmation does not establish equivalence with MTGO's optional per-action confirmation/menu descriptors. Recovered consumer methods establish the descriptor capability, not actual library scenario values. This remains a gap, not a superiority claim. |

This review does not certify full MTGO parity or mark the goal complete. The
finite UI inventory and existing operation/ownership mappings remain applicable;
do not restart them as absent. No production changes or commits in this review.

2026-10-24 primary MTGO search comparison: the January 20, 2026 official patch
notes state that opponent-library searches via Opposition Agent or Mindslaver
must allow fail-to-find when applicable:
https://www.mtgo.com/news/mtgo012026
This establishes a documented behavior requirement, not the confirmation UI or
ACTION_LIST payload. A new generic opponent-library regression verifies qualified
search with an available land, chooser-only options, serialized pending replay,
zero-card answer, shuffle of the searched library only, unchanged identities,
no exile movement and inspection expiry. It passes library-opponent-fail-to-find.log
(1 targeted test, exit 0). The existing policy required no production change.
This tests the shared Search instruction, not implementations of the two named
cards or player-control effects. Conditional confirmation remains unverified.

2026-10-24 current integration checkpoint: full rules suite passes 2,089 tests
(library-current-rules.log) and full API suite passes 844 tests
(library-current-api.log), both exit 0 with zero failed/skipped. The unchanged
client remains at the successful 1,881-test npm run verify plus production build
in library-layout-preferences-verify.log. Two-player Commander opening rerun
passes on table 9e6d56a3-2146-4d46-a7df-4d4d5f5e8564
(library-current-opening-two.log): Commander-size counts, paid mulligan/private
bottom choice after reload, kept-hand preservation and first-turn draw skip.
Bottom-choice landscape, completed desktop and portrait captures inspected;
portrait displays the established rotation notice. The owned host was stopped.
Recent four-seat saved-layout/privacy/expiry/reload evidence remains in
library-layout-preferences-live.log. This is an integrated checkpoint, not final
completion: F1 capability acceptance, F2 authoritative interpretation and F5
conditional action comparison remain open. No commit or new production changes.

2026-10-24 persistent library layout: library inspections opt into geometry
persistence through UiPreferencesService.libraryPanel. The existing preferences
normalizer accepts only finite positive-size geometry and copies only left/top/
width/height; no card, player or inspection identifiers are stored. The shared
floating directive restores opted-in geometry and clamps it to the current
viewport. Unconfigured panels retain instance-only behavior. Phone landscape
continues to use its fixed overlay. Full npm run verify passes 1,881 tests,
coverage and production build (client library-layout-preferences-verify.log,
exit 0; existing card-modal style warning). Four live seats pass drag/resize,
collapse/reopen, phone fallback, full-page geometry restoration, private active
reload, stop/expiry and final reload (library-layout-preferences-live.log,
table b68c7192-62e9-4750-815b-9347a6c479ab). Persisted desktop and landscape
captures were inspected. This closes basic browser-local saved geometry, not
MTGO export/import, multiple-window layout equivalence or conditional action
confirmation. F1/F2 and final integrated acceptance remain open. No commit.

2026-10-24 characteristic discriminator checkpoint: HiddenMillDestinationTests
now directly exercises a printed creature redirected unrevealed to hand, with a
Creature filter (one trigger) and Artifact filter (no trigger), for both cost and
effect milling across a serialized replacement pause. The positive cases retain
the existing per-event opponent privacy checks through collection/resolution and
prove the trigger body cannot move the hidden arrival to exile. All 42 focused
cases pass in library-hidden-characteristic-tests.log (exit 0). No production
matching behavior changed. These cases make the current semantics explicit;
they are not independent proof that those semantics are the only rules reading.

Primary rules rechecked: CR 603.10 supplies default post-event trigger matching;
701.17c limits an effect finding the milled object in a hidden destination;
701.9c explicitly makes characteristics undefined for discarded cards redirected
unrevealed to hidden zones, but does not say this for milling. A working inference
is to retain normal characteristic matching while withholding a follow-up card
subject. No explicit primary ruling for the synthetic mill-to-hand interaction
was found, so F2's authoritative interpretation remains unverified. Do not label
these tests as a new rules fix or claim this closes F2. Source:
https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf

2026-10-24 successful payment resolution checkpoint: the public-command success
variant of PartialNestedLibraryRecoveryTests now continues through public priority
passes and serialized replay after every pass. Three payment triggers, the ability
and its resulting trigger resolve exactly once (five StackObjectResolved events).
The ability grants exactly seven life, taking 22 to 29; the paid library identity
order remains unchanged, with no subsequent ZoneReordered or CardMilled events,
no recovery, one activation, and no events on repeated settlement. The success
fixture uses no-op observer bodies to avoid its former self-repeating gain-life
trigger. Failure fixtures retain their original observer bodies. All six targeted
cases pass (library-prefix-resolution-tests.log, exit 0). This supersedes the
success-path limitation below that stopped at stack creation. No production code
changed and no new full-suite/live/UI result is claimed. F1 classifier boundaries,
F2 and final integrated acceptance remain open; no commit.

2026-10-24 inspection reopen checkpoint: the existing geometry directive now
retains position and size through collapse/reopen and responsive fallback for
its own lifetime; a new directive starts fresh. The new regression failed before
the fix. Full npm run verify passes 1,879 tests, coverage and production build
(library-panel-reopen-verify-final.log, exit 0). Four live seats pass native drag,
resize, collapse/reopen geometry equality, phone landscape, privacy, expiry and
reload (library-panel-reopen-live.log, table 767cd729-f25f-45ff-beaf-af2e2d711e66).
Reopened desktop and landscape captures were inspected. Cross-match saved layout
is not implemented; this does not establish full MTGO window parity.
Web checked: https://www.mtgo.com/getting-started/getting-started-gameplay
confirms resize/pop-out and export/import layout settings. The official tips
page confirms undo restrictions after hidden-zone movement. Neither resolves
the outstanding synthetic unrevealed mill-to-hand trigger boundary. F1/F2 and
final integrated acceptance remain open. No commit.

Scope is strictly library behavior, as corrected by the user. Older card/deck
foundation tasks are historical context, not authorization to expand this work.
The goal remains incomplete. This ledger consolidates evidence without treating
old progress notes or passing totals as proof of every requirement.

## Core operation reconciliation — 2026-10-24

Desktop library inspections now use the existing authorized details panel as a
movable/resizable window. FloatingPanelDirective owns geometry only: pointer
capture, arrow-key movement, viewport clamping, responsive cleanup and observer
teardown. Native CSS resizing is enabled at the shared desktop breakpoint;
phone landscape retains the existing fixed overlay. No new card cache or
inspection lifetime was introduced. Existing card-menu tests now target card
buttons explicitly rather than the first button, which is the new Move handle.

Client verify passed 1,878 tests, coverage gates and production build, exit 0
(library-floating-verify-checked.log). Earlier failures were obsolete test button
selectors, formatting, a test-host lint name and the typed querySelector return;
those were corrected before the passing gate. The existing card-modal style
budget warning remains. Table 78556d99-11e4-4d06-8570-d8b9db09ec1c passed
`--look --panel-probe --four` (library-floating-live.log, exit 0): actual drag,
keyboard movement, native resize, viewport containment, landscape fallback,
private look, active reload, stop/expiry and all-seat final reload. Desktop and
landscape panel captures were inspected; the owned host was stopped. This
establishes the bounded geometry counterpart, not saved MTGO window positions
or every multiple-panel overlap/reopening behavior.

Pending library actions now retain the submitted control and hide alternatives.
GameActions carries local option/confirm/skip metadata through the effect to the
existing SignalR request owner. Its single pending signal stores an immutable
copy of submitted IDs and is scoped by game/choice; duplicate submissions keep
the first request's metadata, and completion/failure clears it. Metadata is not
sent in the engine command. ChoiceControls applies this presentation only to
search/mulligan/mulligan-bottom, falling back to disabled controls when submission
metadata is absent. Unit assertions cover duplicate requests, mutable caller
arrays, failed-send cleanup, retained option and restored alternatives.

Client npm run verify passed 1,875 tests, coverage gates and production build,
exit 0 (library-submission-presentation-verify-resumed.log). The initial run's
tests passed but its coverage gate rejected a type-only file; definitions were
colocated with the existing choice service. The subsequent interrupted run ended
during lint and was restarted only after its process handle was missing. The
existing card-modal stylesheet warning remains.

Four-seat table 0441782a-24d2-47ab-b542-6a776c0ebae5 passed Confirm and Choose none
in separate browser runs (library-choice-confirm-live.log,
library-choice-skip-live.log). The transport holds one real answer before sending;
only its disabled action remains, then release completes search and all-seat
reload. Both desktop/landscape capture pairs were inspected. Commander table
59d8522e-0d54-41db-b923-cf0f4c51f847 passed pending mulligan and the full opening
flow (library-choice-mulligan-live-final.log); desktop/landscape and portrait
rotation-notice captures inspected. An earlier mulligan harness run timed out
because its delay reset on reload; the optional one-shot session key fixes that
probe, not production behavior. Both owned fixture hosts were stopped.

Rendered withdrawal follow-up: table a759c5c7-2c2e-412a-a654-d2b52ff6e6df passes
`--search --withdrawal-probe --four` (library-search-withdrawal-live.log, exit 0).
A QA-only socket probe re-delivers an authorized view with the same search ID,
withdraws the selected offered ID and tests minimum zero versus one. On desktop
and 667x375 landscape, selected styling disappears; empty confirmation stays
enabled only for the optional search. Library event counts remain unchanged.
The real server view is then restored by reload and normal four-seat completion,
privacy and reload checks pass. Required desktop/landscape and optional landscape
captures were inspected. This proves the rendered response to simulated view
updates; it does not assert a naturally occurring rule-generated withdrawal.
It supersedes the rendered-transition-unverified limitation below. No production
change followed the successful 1,872-test verify gate. Owned fixture host stopped.

Search offered-membership regression: ChoiceSelectionService previously retained
a selected ID after the same choice's offered options withdrew it, and canSubmit
still returned true. The new focused regression fails both assertions in
client library-choice-membership-red.log (one failed, six passed). The shared
service now filters retained picks against current offered IDs when binding and
checks membership before submission, using one offeredPicks helper. No second
library selection implementation or server-authority change was introduced.
Four-player table 489bd8de-9cf9-4865-8b39-1bda41c859f4 passes normal filtered search,
private selection, pending reload, confirmation and all-seat reload
(library-choice-membership-live.log, exit 0). Its pending desktop capture was
inspected; this normal-flow run does not reproduce a same-ID withdrawal on the
live board. That exact rendered transition remains unverified. The fixture host
was stopped. Pending-button presentation was not changed: the view receives
only a busy flag, not the identity of the submitted action to retain visibly.
Full client verification subsequently passed: 1,872 tests, coverage checks and
production build, exit 0 in library-choice-membership-verify-final.log. The first
verify attempt stopped on spec formatting; formatting was corrected before the
successful run. Existing card-modal stylesheet budget warning remains.

Hidden-hand replay privacy strengthened: all six existing cost/effect and
known-filter variants in HiddenMillDestinationTests now inspect the opponent's
serialized projection after every resumed movement event and every subsequent
trigger collection/resolution event. The private oracle identifier stays absent
throughout; existing owner-hand and unsuccessful follow-up movement assertions
still pass. Full rules suite passes 2,085 tests, zero failures/skips, exit 0 in
library-hand-prefix-rules.log. The initial sandbox test host produced no results
and was explicitly interrupted before the successful elevated run. This closes
an intermediate-event privacy coverage gap, not F2's characteristic-rule question.
No production code changed.

Four-seat opening observer coverage strengthened: the verifier now explicitly
checks all three nonchoosing viewers' empty bottom-choice option lists before
and after reload, and preserves each kept hand's ordered identities through
the other player's mulligans. Table 78a15249-45e9-45ce-aebd-c7c6fa50f44d passed
the full opening flow (library-opening-all-observers.log, exit 0). Earlier runs
connected/reloaded all seats but made these specific assertions for only one
opponent. No production or layout change was made; this pass adds behavioral
evidence, not a new visual acceptance claim. The owned fixture host was stopped.

### Commander admission versus opening fixture

Integration follow-up: four new GameLobbyDecksTests cases now connect the real
collection-backed loader/Commander admission to GameSessionService, DeckGate,
GameLogStore on SQLite and a fresh session service reloading the stored game.
Two/four-seat and single/paired-commander combinations verify seven opening hand
cards, 92/91 library cards, one/two owner-filtered commanders in the shared command
zone, 100 total cards and no commanders in hand/library before and after reload.
Metadata and executable card definitions remain controlled test dependencies.
All 13 tests in the class pass (library-admission-opening-integration-final.log,
exit 0). The first run's four failures were an incorrect per-owner command-zone
test lookup; the assertion now follows the shared-zone model. No production
behavior was changed. This closes the loader-to-session evidence gap described
below, without claiming live external catalog validation or lobby HTTP coverage.

Source recheck separates two independent responsibilities. GameLobbyDecks.LoadAsync
extracts saved commanders, calls CommanderDeckAdmission.ValidateAsync on the full
deck plus command list, then calls DeckGate.Admit. CommanderDeckAdmission resolves
oracle metadata and delegates to CommanderDeckValidator. The latter enforces
100 total cards, eligible single/paired commanders, color identity, legality and
copy limits. The existing GameLobbyDecksTests assertion bodies cover a 99-card
library with one commander, a 98-card library with two saved compatible commanders,
missing commanders and refusal of a 99-total-card deck even in honour-system mode.
Those tests use controlled metadata and do not claim live Scryfall validation.

TableQa instead calls GameSessionService directly with a synthetic QaPool.
DeckGate itself is production code; the pool reports synthetic Verified statuses.
The corrected opening fixture exercises 99+1 separation, draws and mulligans in
live two/four-seat sessions, but does not pass through the collection-backed
Commander admission path. Earlier documentation calling DeckGate a stub was
incorrect and has been corrected. This pass inspected existing code and assertions;
it did not run a new test suite or change gameplay behavior. Full-path collection
admission must not be inferred from the opening browser captures alone.

### Original UI/transport requirements: evidence reconciliation

The original Requirements and evidence table in LIBRARY_IMPLEMENTATION.md is a
historical starting inventory. Its UI/transport rows now map to the following
recorded acceptance; this mapping does not claim every viewport combination or
unknown network interleaving has been tested.

| Original row | Current evidence and limits |
| --- | --- |
| Private library projection and preview cleanup | Library UI acceptance matrix records public/private shuffle expiry and four-seat phasing revocation. The separate detail-preview run e425af95-9552-4947-bc31-42ce9651d428 verifies an already-open private detail disappears, with all-seat reload. This supersedes the initial rendered-verification-pending note. |
| Opening-hand UI | Four-seat Commander opening evidence is retained in the register; two-seat paid mulligan and first-turn draw skip pass library-opening-two-counts.log, including summary/pile agreement. These establish separate multiplayer and duel variants. |
| Pile/count and empty boundaries | The matrix records empty/single-card inspection captures in three viewports and last-card draw versus subsequent empty draw. The latter includes elimination, priority transfer and continued survivor turns. Synthetic artwork is not catalog validation. |
| Search, selection and confirmation | Recorded invalid-input, no-match, private selection/reload and busy-request tests cover these states. The no-match run confirms one search/shuffle and no selected arrival; invalid counts produce no command. Viewport-specific proof is listed beside the captures, not inferred from component tests. |
| Top/bottom ordering and desktop/touch controls | Arrangement edit/cancel/confirm, revoked inspection and touch runs are recorded in the matrix/register; the revoked run removes the draft and controls before fresh inspection. Browser touch emulation is demonstrated; physical-device behavior is not. |
| Pending/error states | The matrix's visible-pending follow-up supersedes its earlier missing-status finding. Uncertain-delivery warnings/retries use the recorded request-identity tests. A busy indicator is not evidence of every possible server-error presentation. |
| Duplicate/stale actions and reconnect | Core API/manual-request mappings above establish membership, exact order and one-save retry behavior. Recorded accepted-response-loss and reconnect runs cover draw/shuffle/search/inspection/order scenarios. The specific phasing and prefix-payment runs establish reload consistency; they do not substitute for forced transport loss. |
| Verification and show-before-commit | Latest full gates: 2,080 rules, 840 API, 1,870 client tests plus production build. Prefix activation now runs through the payment panel, cancellation, three choices, recovery and all-seat reload. Captures were inspected. Final requirement-by-requirement acceptance and presentation remain F7; no commit is authorized. |

This completes the F3 mapping of the original UI/transport rows to existing
evidence. Any remaining concrete UI state belongs to F6's finite inventory;
rules, legacy compatibility and MTGO unknowns retain their own checklist items.

### Commander redirects, manual permissions and transport

| Original contract | Inspected source and assertion evidence | Classification |
| --- | --- | --- |
| Commander library replacement | ReplacementSystem.CommanderCandidate/AskCommander/ResumeCommander offer the owner a hand/library replacement through the normal replacement queue. CommanderReplacementTests assert owner rather than controller, accept/redirect, no movement before answering, multiple affected objects, subsequent instructions, no early RNG/shuffle, and serialized continuation. | Implemented at the shared movement boundary. These subsystem tests are not a substitute for live Commander UI evidence recorded separately. |
| Authorized manual library operations | ManualLibraryCoordinator checks request identity and ManualLibraryPolicy, logs the action, and delegates draw/shuffle/search/order. ManualLibraryInspection requires current own-library membership; arrangement requires an exact inspected permutation. GameHubTests.ManualLibrary asserts enforced-table rejection at hub and session entry points, no log change, missing request ID refusal and normal empty-library loss. | Implemented. Honour-system permission is an API/session boundary, not a second rule inside pure ordering helpers. |
| Duplicate/stale requests | The concurrent manual retry test launches eight attempts and asserts one Applied, seven Refused and one save. A reconstructed session rejects reuse across action kinds for that player; another player can use the same GUID independently. | Implemented with concurrency and persistence evidence. This supports the inspected manual-command scope, not every command in the application. |
| Search reconnect and authorized answers | GameHubTests.LibrarySearch reconstructs a four-seat paused search, checks per-viewer options/cards, rejects wrong player, stale choice and unoffered ID without saving, races duplicate answers, then reconstructs completion and verifies one movement/search/shuffle and no inspection cards. | Implemented with session and serialization evidence; browser interruption behavior remains separately documented in the UI acceptance matrix. |

The inspected API cases are included in the passing 840-test checkpoint
library-mill-global-condition-api.log. No production change was indicated by
this audit. F3's remaining bookkeeping is the original UI/verification row mapping;
the explicit unresolved F1/F2/F4/F5/F6 items remain authoritative.

Inspected implementation and assertion bodies, rather than treating class names
or passing totals as proof. These rows refine F3; they do not certify all card
combinations or replace F1/F2's specific unresolved boundaries.

| Original contract | Current owner and inspected evidence | Classification |
| --- | --- | --- |
| Individual draws, replacement interruption, empty library and restrictions | LibraryDrawCoordinator.Draw offers each draw separately, defers remaining batches and delegates terminal movement through ZoneSystem.PrepareDraw. Complete removes the proposal marker before recording the committed draw. LibraryDrawContinuationTests assert limits before movement/empty failure, serialized multi-draw continuation, frozen original amount and move-before-notification. EmptyLibraryDrawReplacementTests assert replacement produces neither failed-draw marking nor draw-count increments. | Implemented with scoped engine/replay evidence. Some tests intentionally enter subsystem seams; they do not individually prove every public command. |
| Server shuffle and replayable randomness | LibraryShuffleCoordinator offers replacement before randomization and Complete emits RngAdvanced plus committed ZoneReordered. LibraryShuffleReplacementTests assert replaced shuffles preserve RNG/order for empty, one-card and larger libraries, and replacement-created shuffles do not reapply their own source. | Implemented; randomness ownership is also traced in LIBRARY_OWNERSHIP_AUDIT.md. This is not a statistical RNG-quality certification. |
| Search quantity, qualification, fail-to-find and shuffle follow-up | LibrarySearchPolicy.Bounds strips location from the qualification test and distinguishes mandatory public-zone matches from qualified hidden-zone searches. InterpreterTests.LibrarySearch asserts unqualified minimum one, qualified minimum zero despite matches, up-to-zero maximum, and conditional shuffle behavior after finding nothing with following instructions completing once. | Implemented for the inspected vocabulary. Do not re-open as missing basic search. |
| Search participants and privacy | MultiplayerLibrarySearchTests assert APNAP choices before movement, own-library inspection only, per-chooser option visibility, serialized answers and replacement pause, then four shuffles and cleanup. Separate bounded/other-owner search tests are indexed in the original suite. | Core multiplayer search evidence exists. The indexed additional tests are not relabelled as newly inspected proof in this pass. |
| Top/bottom ordering, scry and surveil | LibraryOrder.Place/Arrange preserve existing identity and untouched-card order. InterpreterLibraryOrderingTests assert ordered remaining top cards, keep-all ordering, empty-library completion, private options, resumed state and no ObjectMoved for same-library scry bottoming. | Implemented with replay/privacy evidence. Existing all-player live runs supply the separate browser layer. |

These classes were included in the last full 2,065-test rules gate
(library-mill-global-condition-rules.log). Eight later test-only pause cases
passed separately; this is not a claim that a new combined final gate ran here.
Remaining F3 reconciliation is limited to Commander redirects, manual permission
boundaries and mapping the original transport/UI rows to their current evidence.

## Manual search control acceptance (2026-10-23)

### Original-contract reconciliation: Commander admission and opening hands (2026-10-24)

Current source inspection resolves two stale implementation questions in
LIBRARY_IMPLEMENTATION.md's original requirements table:

| Original requirement | Inspected implementation and assertions | Remaining acceptance |
| --- | --- | --- |
| Deck validation and commander separation | CommanderDeckValidator enforces total size, commander membership/eligibility, legality, color identity/basic land types and copy limits. GameLobbyDecks loads only owned main-board cards, expands finishes, removes selected commanders, then validates the combined deck before admission. GameLobbyDecksTests assert 99+1 and 98+2 separation, selected printing/finish retention, absent commanders and honour-system size enforcement. CommanderDeckValidatorTests and CommanderPairPolicyTests cover construction and pair constraints. GameLobbyStartup passes the separated collections to each NewGameSeat. | This establishes the implemented startup boundary, not exhaustive card-catalog legality or MTGO equivalence. Final integrated gate remains required. No new deck-builder work is indicated. |
| Opening hands and mulligans | OpeningHandCoordinator owns declarations/redraw/bottoming and delegates movement to IZoneSystem. InitialMulliganCount applies the multiplayer/Brawl free mulligan. MulliganTests assert four-player declaration rounds, paid two-player first mulligan, free multiplayer first mulligan, bottom-choice replay, preserved kept seats, zero-card termination and concessions during declarations/bottoming. | Existing four-seat live evidence remains valid. The separately required two-player opening browser variant still needs explicit final acceptance; two-player search/scry runs do not prove it. |

The inspected test classes are included in the recorded full backend checkpoint:
library-legacy-refusal-rules.log (2,061 passed) and
library-legacy-refusal-api.log (840 passed); both terminal summaries were checked.
No production behavior changed in this reconciliation. These two rows must not
be reopened merely because their original historical notes say "audit" or
"UI pending". Other original requirements remain under F3/F6/F7 review.

Full manual arrangement acceptance: engine/API exact-permutation checks are backed
by rules 1,790 / API 837. The scoped local editor now uses the shared ordering
controls, invalidates drafts on changed permission/inspection and presents cards
in selected order. Client verify passes 1,849 tests, coverage and build. Four-seat
table 824e5148-8385-4e95-a769-cab862de3ad8 verifies edit/cancel without network
mutation, exact confirmed order via reinspection, privacy, expiry and final reload.
See library-order-visual-live.log and inspected library-arrange-live-four-editor
desktop/portrait/landscape captures. Follow-up table
50102311-2367-4ea9-bf54-a549091c5048 verifies Chrome touch input and live expiry
of an open arrangement draft after the inspection ends, then fresh inspection and
successful ordering (library-order-revocation-live.log). Revoked/editor captures
were inspected. Physical devices remain unverified.

Live option follow-up: table 51512843-1f99-444d-8beb-54d2c9d3d587 searched for
two Basic Forests, revealed them and placed both on the battlefield tapped with
shuffle disabled. Four projections agree; QA event counts prove one search, one
reveal and zero shuffles. Reload/confirmation and inspected desktop/landscape
captures pass (library-search-battlefield-live.log). Table
2088946a-1edf-4468-b55a-e5488d73d5b9 verifies the complementary private move to
hand with one shuffle and no reveal (library-search-event-counts-live.log).
Only the QA harness changed; the client verification baseline remains 1,843.

Own-library honour-system search now has a persisted source-less instruction,
retry-safe engine command, authenticated DTO, shared client transport and viewer
form. Backend baseline: rules 1,789 / API 832. Client verification: 1,843 tests,
coverage and production build pass (library-search-controls-verify.log).
Four-seat table 701a460e-aff5-4e5f-bab3-66eddf8519f5 completed a Land-filtered
search to hand through browser controls, selection reload and explicit confirmation.
Observers received no search cards/options or hidden hand identity. Final counts,
inspection expiry, broadcasts and reload matched. See library-search-controls-live.log
and inspected tools/TableQa/library-search-manual-live-four-*.png captures.
375x667 and 667x375 form checks cover overflow and reachable 44px submit control.
Other destinations/filters and replacement variants retain engine coverage but are
not all live-verified. Physical devices, full arrangement and full MTGO parity remain
open. QA artwork is intentionally absent. No commit.

## Deferred-shuffle API and replay privacy verified (2026-10-23)

Retained mana-emptying recovery fold: three regression cases verify chronology,
serialized journals, refusal of missing events/unexpected pools, and recording of
emptying an already-empty pool. Rules 1,760 / API 828 pass in
library-mana-empty-rules-final.log and library-mana-empty-api.log. Retained mana
spending, source dependencies and general activation pauses remain incomplete.
This is not new browser or full recovery acceptance.

Idle-mode guard live acceptance: four-seat table
0e60b43b-830e-4c5f-9627-6d740f62c8f6 hides library reposition actions during a
cast draft, restores them on cancellation without server-state mutation, then
completes top placement and reload. library-reposition-mode-guard-live.log records
the pass; tools/TableQa/library-reposition-during-cast.png was inspected.

Surveil menu/detail regression now has live evidence after the tap-menu change:
584594db-8666-4147-a4b2-ecab4e1e7e35 passes private inspection refresh, opening
details from the menu, expiry on authoritative answer and matching public graveyard
results for both seats. Choice-push privacy checks pass too. Existing catalog cards
only. Client log: library-surveil-menu-expiry-live.log; active-detail and completed
landscape captures under e2e/screenshots/library-surveil-*.png were inspected.

Inspection touch entry now uses a normal tap/click to open the shared action menu.
Chrome touch-input emulation validates both card and destination taps at 667x375
on four-seat table 877a627d-cf82-4cb7-8bbd-3835397fe83a. Inspection expiry,
broadcasts and reload pass; capture inspected. Evidence:
library-reposition-touch-live.log, library-reposition-bottom-touch-menu-landscape.png
under tools/TableQa, client library-touch-menu-verify.log (1,840 tests plus build).
This supersedes the browser touch-input gap, not physical-device acceptance.

The separate bottom-action browser gap is now covered by four-seat table
8215372f-bfd3-4768-873a-debd6f046539. --reposition --bottom --four passes menu
submission, inspection expiry, broadcasts and reload; landscape menu inspected.
Log: library-reposition-bottom-live.log. Exact final ordering is asserted by the
API tests, while this browser run checks the UI-to-command interaction and privacy.
Native touch invocation remains unverified.

Manual reposition UI checkpoint: full client verify passes 1,840 tests plus coverage
and production build. Four-seat top-menu test passes on
a442e5ac-2b80-44b4-8e74-a9ae5f20ca08, including inspection expiry and all-seat reload.
Desktop/landscape captures inspected. Evidence: library-reposition-live.log,
tools/TableQa/library-reposition-menu-*.png, client library-reposition-menu-verify.log.
Both destinations have API and component assertions; the browser run exercised top
only. Native touch invocation and a separate bottom browser run remain unverified.

Independent inspection lifetimes now have explicit simultaneous-player coverage:
draw projects exactly the remaining library identities; explicit stop and shuffle
expire only the acting player's manual inspection; the other player's exact library
identities remain inspectable after service restart. Both cases pass with the full
826-test API suite (library-look-independent-api-final.log). No production or UI
behavior changed in this follow-up; browser evidence remains the run documented below.

Manual look acceptance: own-library honour-system look and stop use authenticated
commands with request replay guards. Four API cases cover private projection, replay,
retry refusal, explicit stop/shuffle expiry, and enforced-table rejection. Full API
824 / rules 1,757 pass. Final client verify passes 1,837 tests and production build.
Four-browser table 30eb0609-5f42-4a48-98db-95bb85c33404 passes owner-only inspection,
active refresh, stop, all-seat synchronization and final refresh; final desktop and
667x375 captures inspected. Logs: library-manual-look-{api,rules,live-final}.log;
client library-manual-look-verify-final.log. This does not prove Commander deck
legality, manual search/order controls, or complete MTGO interaction parity.

Shared arrangement boundary now preserves unique library membership when references
repeat, while ignoring stale IDs and keeping untouched-card order. A focused red
regression demonstrated the duplicate output; full rules 1,757 / API 820 pass in
library-order-invariant-rules.log and library-order-invariant-api.log. This is an
ordering invariant, distinct from search result uniqueness for later non-ordering
effects. No new UI or browser completion claim.

Library membership integrity follow-up: overlapping shared-search selections now
produce a unique combined result. Before the fix, a following top/bottom reorder
duplicated a library entry (2 physical cards / 3 entries), reproduced in
library-order-unique-red.log. Result composition is fixed at its search owner;
eight existing resumed-search variants assert unique remembered identities too.
Rules 1,756 / API 820 pass (library-shared-search-unique-rules.log,
library-shared-search-unique-api.log). This is engine/replay evidence only.

Four-seat browser extension passed for manual draw and shuffle. Tables:
ad62faee-96d4-4058-a866-b24b1da25b7e (draw),
732186dc-8fc5-4744-b927-5bb358435c16 (shuffle). All seats synchronize after pending
refresh/answer and final reload; all three opponent projections omit the drawn
identity while showing the updated hand count. Four-player summary-rail DOM checks
pass. Inspected landscape prompts and fourth-seat draw capture. Logs:
library-four-draw-live-final.log, library-four-shuffle-live.log. These synthetic
fixtures cover these flows, not every Commander/library requirement.

Live draw checkpoint: table 54ad791c-1347-415a-86ef-e19228698f44 passed two-browser
manual draw, pending refresh, replacement selection, broadcast privacy and final
reload. Hand count increased once and library count decreased once; the opponent
projection contained no drawn-card identity. Captures inspected under
tools/TableQa/library-draw-live-* (pending landscape and both seats).
Evidence: library-draw-live.log. Synthetic fixture acceptance, not full completion.

Nested draw checkpoint: four red-to-green cases cover empty/nonempty library and
one/two replacement sources with reloads during nested choices. Deferred draw batches
now persist inherited used-replacement history; a separate test checks both later
draws and redacted projections. Existing multi-draw disclosure tests caught outer
context contamination, fixed by separating nested history from the parent's set.
Full rules 1,754 / API 820 pass (library-nested-draw-rules-final.log,
library-nested-draw-api.log). New live-browser coverage remains outstanding.

Live manual-shuffle replacement checkpoint: two browser seats passed on table
c3ed8e30-87c1-4629-a0ee-7506afadc5a2, including modal action, pending refresh,
replacement answer, broadcast and final reload. Inspected desktop and phone-landscape
captures under tools/TableQa/library-shuffle-live-pending*.png. Counts and life remain
unchanged. Log: library-shuffle-live-final.log. This closes this specific browser
gap using a synthetic isolated fixture; full library acceptance is still open.

Nested shuffle replacement coverage: bounded fixtures reproduced self-reapplication
and loss of earlier replacement history in two-source chains. Shuffle now receives
the interpreter's used set, and replacement application preserves the whole set in
EffectContext. Four direct/search, one/two-source cases reload serialized choices
and verify single application per source plus one completed shuffle. Rules 1,749 /
API 820 pass (library-nested-shuffle-rules.log, library-nested-shuffle-api.log).
This extends engine/replay coverage; browser acceptance remains outstanding.

Shared shuffle now consults replacements before randomization. Previously recognized
WouldShuffle effects were bypassed by ZoneSystem.Shuffle. Replacement-order search
tests reproduced the missing pause; further regressions exposed repeated stale search
placement IDs and ordinary Shuffle continuing subsequent instructions while pending.
These paths now share LibraryShuffleCoordinator completion and existing proposal
resumption. Rules 1,745 / API 820 pass (library-shuffle-replacement-rules-final.log,
library-shuffle-replacement-api.log). Empty/singleton/multi-card replacement cases
leave RNG and order unchanged; serialized search decisions and generic-effect resume
are covered. Live browser acceptance and nested replacement auditing remain open.

Shared-search identity checkpoint: overlapping selections of a revealed library
card left one participant's remembered result pointing to a removed object after
shuffle. Two persisted-reference assertions reproduced this despite passing final
zone counts. Operation-wide refresh now covers every participant's bookkeeping and
inspection scope using the shared identity mapper. Four serialized decision cases
cover top/bottom and distinct/shared selection. Rules 1,737 / API 820 pass:
library-shared-search-identity-rules.log, library-shared-search-identity-api.log.
This is engine/replay evidence; no new live-browser or MTGO parity claim.

Scenario adapter follow-up: serialized ActivateStep.PaymentOrder reaches the same
engine validator used by live commands. Two red-to-green cases cover reordered
selected-library exile/mill and refusal of an invalid ordinary/deferred order.
Current totals: 1,733 rules / 326 card-schema / 820 API tests. Evidence:
library-scenario-payment-order-rules.log, library-scenario-payment-cards.log,
library-scenario-payment-api-host.log. This closes an adapter gap, not the outstanding
general payment-recovery or library UI acceptance requirements.

Final selected-library exile now shares the no-later-cost continuation boundary with
final mill. Four new rules variants preserve chosen identity through serialized
replacement decisions and keep an unselected card; two new API variants reload each
pending stage with two/four seats. Totals: 1,731 rules / 820 API. The combined live
picker/replacement flow passed on befd0864-d012-4fa3-a1c1-c00e0138988a with two browsers
(library-final-exile-pause-live.log). Captures inspected in tools/TableQa with prefix
library-selection-replacement-live. This does not enable pauses with later unpaid
costs or general failed-payment recovery. No new catalog card or commit.

Readable replacement-label gap addressed for the library path: fallback descriptions
identify the action and already-public source; hidden and hypothetical entry sources
do not acquire names. Supplied text remains unchanged. Replacement-only text rows
show all four fixture options in inspected desktop and 667x375 captures. Final live
table 6dbb6269-3dd6-4ca1-a6c7-fe4deeac4768 passed pending refresh and completion
(library-replacement-label-live-final.log). Capture paths under tools/TableQa:
library-replacement-live-pending.png and library-replacement-live-pending-landscape.png.
Rules/API/client totals 1,727 / 818 / 1,835, full npm verify passed. These generic
fallbacks do not describe every possible replacement outcome; that remains limited
by supplied ability text. General recovery and the full library goal are incomplete.

Final MillCost replacement pauses now work through normal activation commands when
no later costs remain. Rules tests serialize each answer for hidden/revealed one-/two-card
cases; two-/four-seat API tests reload persisted sessions between answers. Earlier
unpaid-cost pauses remain gated. Totals 1,722 rules / 818 API pass. Two live browsers
verified a refresh mid-choice and matching completed state on table
daa98e8d-97c8-4661-9988-925f98d9e54e (library-final-payment-pause-live-final.log).
Inspected pending and both-seat captures in tools/TableQa/library-replacement-live-*.png.
Current labels expose internal event type names; readable replacement descriptions
remain a concrete presentation gap. This is not general payment recovery acceptance.

Visible-card selection now has live two-browser evidence on isolated table
9ddd8279-b596-4e0e-a9ca-0db147a82f0f: one authorized private top card offered, exact
selection required, cancel unchanged, confirmation paid and broadcast, reload stable.
library-payment-picker-live-final.log and inspected tools/TableQa/library-selection-live-
*.png cover the picker and desktop/landscape/portrait states. No hidden library inventory
was shown. LegalActions preparation offers were needed to make selection reachable;
unselected execution remains refused. Rules/API/client totals: 1,718 / 816 / 1,835;
full npm verify passed in mtg-client/library-payment-picker-verify-final.log.
This does not complete hidden-card selection, alternative-cost prompts or normal
replacement-payment continuation. No catalog definitions changed; nothing committed.

Latest live payment evidence: isolated two-seat TableQa fixture passed real browser
cancel/reorder/confirm, both-seat SignalR and displayed life updates, and reload
snapshot checks (library-payment-qa-browser-final.log). Inspected prompt and both
seat captures in tools/TableQa/library-payment-live-*.png. Life 40 to 39, library
57 to 54, and stack count 0 to 1 matched both views. Synthetic definitions and private
in-memory log were used; no catalog-backed Commander legality claim. Internal order
between the two mill operations is not distinguished by aggregate counts; existing
engine tests cover explicit ordering. Cost-card selection and normal replacement
pause support are still open.
Full client verification passed afterward: 1,832 tests, changed-file coverage and
production build in mtg-client/library-payment-live-verify.log. Nothing committed.

Live payment-order catalog audit: no card definition in cards/ uses MillCost or
ExileFromZoneCost from Library. The four exile-cost cards are Mines of Moria
(Graveyard), The Soul Stone (Battlefield), Force of Will (Hand), and Force of
Negation (Hand). Searches included schema/scenario hits, then recursively inspected
each matching file's definition to distinguish executable costs from schema text.
No catalog definition was added or changed. A catalog-backed live ordering run was
therefore not performed; an isolated synthetic live fixture remains an option.

Eight projection cases now verify nested library costs versus other-zone costs
and mill effects, including face-down suppression for both viewers. A client
adapter regression preserves true/false/omitted markers without text inference.
Rules suite: 1,714 passing (library-payment-marker-rules.log). Targeted client
transport suite: 22 passing (mtg-client/library-payment-marker-client.log).
This follow-up changes tests only; the prior full npm verify and API baseline
remain the latest full runs. These checks do not substitute for live acceptance.

Payment-order UI now consumes server descriptors for library-cost abilities.
Fixture capture harness e2e/verify-library-payment.js verifies group movement,
cancellation, bounds and 44px controls; reviewed desktop/landscape images fit.
Portrait retains the rotate-phone guard. Successful log:
mtg-client/library-payment-ui-captures-final.log. This is fixture visual evidence;
do not describe it as live two-player or four-player payment acceptance.
Backend suites pass 1,706 rules / 814 API (library-payment-ui-rules.log,
library-payment-ui-api.log). Cost-card selection remains a separate missing UI.
Final npm run verify passes with 1,831 client tests, coverage and production build
(mtg-client/library-payment-ui-verify-final.log). New transport tests verify no send
before confirmation, stale-view cancellation, disconnect/connection-loss handling,
and no unnecessary prompt for one payment. Existing card-modal size warning remains.
Full site-wide verify:ui and live multiplayer ordering were not run in this increment.

Read-only descriptor endpoint verified in two-/four-seat controller/session tests:
indices and labels are returned without state/log/save changes or private library
names. Nonseated requests return 404; wrong-seat ability previews return 400.
The engine test covers nested expansion, default/requested order and unchanged
library/life. All 1,706 rules / 814 API tests pass
(library-payment-descriptors-rules-final.log, library-payment-descriptors-api.log).
These are automated engine/controller tests, not browser captures. Board ordering
controls still need implementation and visual/live verification.

Discovery/execution agreement: nested library alternatives previously offered zero
object questions instead of two (library-choice-expansion-red.log). Discovery now
uses the shared recursive expansion and selected-object cursor. Two regressions
cover nested alternatives and automatic choice matching execution. All 1,705 rules
and 814 API tests pass (library-choice-expansion-rules-final.log,
library-choice-expansion-api.log). Payment-order descriptors/control are still
missing from the board; no browser or UI-completion claim is made.

Explicit-order backend: six new cases cover selected library order and invalid
permutations/group boundaries; saved-pause coverage now selects a nondefault
order. The ignored-order defect reproduced in library-explicit-order-red.log.
All 1,703 rules tests pass (library-explicit-order-rules-final.log). The DTO passes
selected objects and order to the authenticated command. Client discovery and
order selection remain missing, so this does not complete player-facing ordering.
All 814 API tests pass (library-explicit-order-api.log), including request-to-command
mapping and two-/four-seat hub refusal without saving when the requested order
puts mill before life. These are in-process hub tests, not browser multiplayer runs.

Saved binding compatibility follow-up: the omitted ObjectSelections JSON field
now has a replacement-pause/resume test. A legacy flat queue also verifies
regrouping library exile after discard without swapping identities; too few or
too many binding slots refuse. All 1,697 rules tests pass
(library-payment-binding-compatibility-rules.log). This closes the omitted-field
verification gap below for these paths, not every historical journal shape.
No runtime changes or API rerun were needed in this test-only follow-up.

Selected-library order now has five additional regressions, including serialized
per-cost bindings and partial-selection isolation. Direct and alternative exile
previously preceded discard (library-selected-order-red.log); ordinary costs now
precede selected library exile without swapping selected identities. All 1,693
rules tests pass (library-selected-order-rules-final.log), as do all 813 API tests
(library-selected-order-api.log). This supersedes the
selected-exile grouping gap below, but does not establish omitted-field journal
compatibility or explicit player-selected ordering.

Mill-order follow-up: four new tests and corrected older fixtures pass in the
1,688-test rules suite (library-payment-order-rules-final.log); all 813 API tests
also pass (library-payment-order-api.log). Begin/Resume now
expand selected alternatives and defer mill stably. The previous normal-command
mill-before-sacrifice fixture now asserts sacrifice happens first and its source
replacement is absent during mill. This supersedes its earlier compliance claim.
Selected library exile grouping, random costs and player-selected within-group
order remain incomplete; see LIBRARY_PAYMENT_RULES_AUDIT.md.

Rules audit correction: [library payment audit](LIBRARY_PAYMENT_RULES_AUDIT.md)
identifies a missing CR 601.2h payment-order constraint in CostPayment.Begin.
The earlier-retained-property fixture below pays mill before sacrifice; its
passing result establishes restoration mechanics, not legal payment ordering.
Correct ordering and fixture classification take precedence over extending
retained-conflict recovery. No production change or new test run in this audit.

Earlier retained changes no longer falsely block movement recovery. Four normal
activation/replay cases preserve tap or charge counters from a retained shuffle
when a later sacrifice is reversed after failed mill, including commanders.
All four reproduced refusal (library-prior-retained-movement-before.log). Conflict
checks now use later retained steps; exact movement snapshots/history remain
validated. Full suites: 1,684 rules / 813 API
(library-prior-retained-movement-rules.log, library-prior-retained-movement-api.log).
Later retained conflicts and general activation pauses remain incomplete.

Cross-payment restoration order is now preserved in serialized stages. Two
replacement pauses (movement, then counters on the arrival) recover after a later
mill fails; sacrifice and exile cases restore original identity/history/order.
Both reproduced refusal in library-recovery-order-before.log. A compatibility test
replays legacy property fields with the new stages field absent. Full suites pass
1,680 rules / 813 API (library-recovery-order-rules-final.log,
library-recovery-order-api.log). These internally started paused paths do not prove
normal activation-pause integration or all retained movement dependencies.

Mixed property-and-movement replacement recovery now has four replay cases:
sacrifice/exile, with counters changed before departure or on the arrival, plus
life/mana changes and tapping an unmoved permanent. All previously refused
(library-mixed-movement-before.log). The descriptor validates property event
placement and complete departure/arrival snapshots using shared validation;
unmoved properties continue through existing recovery. Full suites pass 1,677
rules / 813 API (library-mixed-movement-rules.log, library-mixed-movement-api.log).
This does not establish retained movement dependencies or normal activation-pause
integration. Test fixtures use existing generic instructions, not catalog cards.

Replacement-driven sacrifice/exile can now recover when used instead of mill.
The shared movement descriptor uses actual departure verbs plus origin/destination,
identity, zone-order and history evidence. Four cases (each verb, ordinary or
commander source) reproduced refusal in library-replaced-movement-before.log and
now restore original identities, battlefield order, movement history, life and
commander candidates through serialized choice answers. Full suites: 1,673 rules /
813 API (library-replaced-movement-rules.log, library-replaced-movement-api.log).
Only supported movement-only shapes are established; mixed operations and normal
activation prompts remain unfinished.

Zero-net-change property sequences now retain their entity snapshots in the cost
journal. Three previously refused replacement cases (life, tap and counters) now
recover through replay and suppress reversed gain/loss triggers. Separately, two
normal-command cases reproduced wrong tap state when a retained shuffle sequence
untapped/re-tapped (or tapped/re-untapped) the paid source. Restoration now follows
the last retained tap event even if before/after snapshots match. Red logs:
library-net-zero-before.log and library-net-zero-retained-before.log. Full suites:
1,669 rules / 813 API (library-net-zero-rules-final.log, library-net-zero-api.log).
Normal activation pauses and broader dependencies remain incomplete.

Mixed property-only mill replacements now recover. Two regression cases vary the
order of life gain, tap, counter addition and restricted mana production; each
replays to the original player/object/library state after a later mill fails.
Both previously refused (library-mixed-properties-before.log). Consolidated
PaymentPropertyValidation rejects incomplete event journals and unsupported field
changes through complete entity snapshot comparison. Additional guards cover
pool emptying and counter overdrafts. Full suites pass 1,664 rules / 813 API
(library-mixed-properties-rules-final.log, library-mixed-properties-api.log).
This is internal paused-payment acceptance; normal activation remains gated.

Property-only mill replacement outcomes now recover using the shared property
accumulator. Tap outcomes require only tap-field changes; counter outcomes require
one matching event/object delta with no other state changes. Both tap directions
and counter addition/removal were reproduced as refused before the change
(library-replaced-properties-red.log). Tests answer both competing replacement
options and replay restored life, tap/counters, unchanged library identity, empty
stack and cleared prompt without extra property events. Full suites pass 1,661
rules / 813 API (library-replaced-properties-rules.log,
library-replaced-properties-api.log). Mixed outcomes and normal activation pauses
remain unfinished; no live claim for these internally seeded payment cases.

Life-only outcomes replacing a mill payment now recover through the existing
signed life restoration. Assessment requires only LifeChanged events, matching
before/after life totals and no other player/object/zone/history changes. The
paused replacement regression checks both +1 and +3 branches after life payment,
failure of a later mill, replay, unchanged library identity and no fired gain/loss
triggers. Refusal reproduced in library-replaced-life-before.log. Full suites:
1,657 rules / 813 API (library-replaced-life-rules.log,
library-replaced-life-api.log). Normal activation pauses remain disabled.

Inspector search-selection feedback now reads the board's shared picks input.
Active selected cards have an outline plus “Selected for search” text; historical
disclosures are not marked. Component coverage checks selection limits, markers
and reset. Live four-seat table 144cdcd5-3144-49e1-9344-2b97b6377fa7 passed with
two inspector markers after deselection/reselection, followed by confirmation,
tapped arrivals and privacy checks (client library-inspector-selection-live.log).
Reviewed library-multi-search-four-inspection-selected.png. Client verify passed
1,822 tests and production build (library-inspector-selection-verify.log), with
the existing card-modal stylesheet budget warning. No MTGO equivalence claim.

Collapsed inspector long-name overflow is now corrected by applying the existing
short-viewport wrapping rule at all widths. Four-seat table
9c007a06-cd22-474d-8b69-a2267c2a1312 passed selection, confirmation and privacy;
the harness additionally asserts host scrollWidth <= clientWidth and the complete
owner name in the summary (client library-inspector-label-live.log). Reviewed
library-multi-search-four-selected.png: the name wraps and the horizontal
scrollbar is absent. npm run verify passed 1,822 tests and production build
(library-inspector-label-verify.log), retaining the known card-modal budget warning.
This supersedes the long-name limitation recorded below.

Active library inspection now opens as a board-sized overlay at desktop as well
as short viewports, using a responsive card grid and a visible close-inspection
hint on the native summary control. Closing returns to the existing confirmation
prompt; the inspection menu still delegates selection to the shared choice state.
Four-seat casual search passed at f92bee29-91ae-4da7-8da5-87890f3b4ca5
(client library-inspector-overlay-live.log). Two-seat private look/reconnect,
details and phone checks passed at e3679eeb-6a1e-46bd-90cd-ee9b367fc40a
(library-inspector-overlay-look.log). Reviewed desktop inspection/selected captures
under library-multi-search-four and portrait/landscape captures under library-look.
Portrait retains the board's existing rotate-phone guard; landscape shows the
inspector grid. Long player names can still overflow the collapsed desktop summary,
visible in the selected capture; that remains unfinished. npm run verify passed
(library-inspector-overlay-verify.log), with the existing card-modal budget warning.
The full site-wide verify:ui suite was not run; these are scoped live captures.

Four-player Commander search now has live acceptance: table
117bd73d-a7bf-480e-a3c3-62a6ee263b77, client library-commander-search-live.log.
The existing multi-search harness gained --commander and used Ghalta plus
98 Forest and one Blighted Woodland per legal 100-card deck. Normal turns reached
the paid search on turn 19. Four views agreed on two tapped Forest arrivals and
the library count reduction; observer payloads had no inspected cards/options,
selection limits/deselection worked after refresh, and active inspection expired.
Selected, observer-0 and resolved captures were inspected under client
e2e/screenshots/library-commander-search-*.png. The selected capture exposes a
remaining layout issue: search content is duplicated in a cramped side panel.
This does not complete responsive acceptance or prove shuffle randomness.
npm run verify passed (library-commander-search-verify.log); the existing
card-modal stylesheet budget warning remains (18.05 kB against 16 kB).

Retained mana production is preserved by PaymentManaRecovery, using the same
ManaChanged fold as ordinary event application. Refund reconstruction validates
the retained snapshots and current pool, including restrictions and provenance;
retained spending/emptying remain unsupported. Four normal-command/replay cases
now pass after reproducing refusal (library-retained-mana-before.log): restricted
and unrestricted additions, each with/without a later payment. Final verification
is 1,656 rules / 813 API (library-retained-mana-rules-final.log,
library-retained-mana-api.log). No live or general activation-pause claim.

Paused payment persistence now has two-/four-seat API coverage. A real internal
engine pause is saved as events, loaded into GameSessionService, joined through
GameHub and answered through Command. Owner-only prompt delivery, redacted payment
context, hidden library card text, unauthorized-answer refusal, retained mill,
life refund and duplicate-answer refusal after a second reload all pass. Full API
suite: 813 (library-paused-recovery-api-final.log). API tests use friend-assembly
access solely to seed the still-gated activation pause. Normal activation prompts
and browser interrupted-payment acceptance remain incomplete; no runtime gate
was changed. Latest rules verification remains 1,652.

Retained tap-state transitions now survive recovery of earlier tap/untap costs.
Recovery checks the latest later retained transition after undoing later payments;
it refuses missing identities or inconsistent states. Four command/replay cases
reproduced refusal before the change (library-retained-tap-before.log) and now
pass, including another payment following the retained effect. Full suites:
1,652 rules / 811 API (library-retained-tap-rules.log,
library-retained-tap-api.log). Live interrupted-payment acceptance and broader
dependency handling remain incomplete.

Same-kind retained counter additions now survive payment recovery. Validation
compares the current counter count with the recorded paid result plus deltas from
later retained steps, then refunds only the paid removal. It rejects missing
identities or inconsistent/negative counts. Tests cover charge payment before
and after a retained shuffle/charge addition through normal commands and replay.
The same-kind case reproduced refusal in library-counter-delta-before.log.
Full suites pass 1,648 rules / 811 API (library-counter-delta-rules-final.log,
library-counter-delta-api.log). This is scoped counter dependency handling,
not completion of all retained-effect dependencies or activation pauses.

Counter-kind recovery now restores only the paid type, preserving other counter
types added by retained library effects. The actual ReplaceWith shuffle/storage
counter sequence, followed by failed mill, restores a two-charge-counter payment
through normal Game.Apply and replay. Same-type retained changes remain refused.
Pre-change refusal: library-counter-kind-before.log. Full suites pass 1,646 rules /
811 API (library-counter-kind-rules.log, library-counter-kind-api.log).

Property-specific recovery conflicts now allow a retained counter change on an
object whose tap payment is reversed. The normal-command test replaces mill with
an explicit ReplaceWith sequence that shuffles and puts a counter on the source;
later failure restores untapped state while retaining both effects through replay.
Tap and counter restoration still reject retained changes to the property they
restore, and movement restoration retains its full-object conflict check. Initial
fixture failures omitted the necessary ReplaceWith wrapper and are not a defect
reproduction. Full suites pass 1,645 rules / 811 API
(library-retained-counter-rules-final.log, library-retained-counter-api.log).

Two/four-seat hub recovery acceptance now verifies unauthorized refusal without
save/log/view changes, updates to every legitimate seat, and private projections
after reload. Repeated activation is explicitly tested as a new attempt, consuming
the next mill with a distinct recovery ID; once empty, the next attempt refuses
without saving or charging life. No idempotency or live-browser claim. All 811 API
tests pass (library-four-seat-recovery-api.log); rules evidence remains 1,644.

Immediate recovery API acceptance: GameHub.Command invokes the normal activation
over GameSessionService, saves once and addresses view updates to both players.
A fresh session service reload verifies refunded life, retained mill, empty
stack/no activation event, no prompt, and hidden remaining library content in
both projected views. Primitive fixture definitions only; no catalog definitions
or live browser acceptance. Full API suite passes 810 tests
(library-hub-recovery-api.log); rules evidence remains 1,644.

Normal activation commands now recover immediate failures with completed retained
library actions. Fully reversible failures retain refusal behavior and replacement
pauses remain gated. Normal Game.Apply tests cover retained-only mill and life
plus mill, replay, no activation/prompt event and retained library trigger only.
Integration exposed Game.Apply discarding payment origins when granting priority;
its immutable outcome update now preserves them. Initial refusal and spurious
life-loss trigger were reproduced in library-normal-immediate-before.log and
library-normal-immediate-rules.log. Full suites pass 1,644 rules / 809 API
(library-normal-immediate-rules-final.log, library-normal-immediate-api-final.log).
Live and API-specific immediate-failure acceptance and general pause integration
remain unfinished.

Immediate recovery provenance follow-up: retained-only mill recovery (empty
reversal set) reserves an ID and preserves its trigger. Repeated failed attempts
after replay get distinct IDs and reversal sets, restore life and preserve the
existing stack entry. Tests only; 1,644 rules pass
(library-immediate-provenance-rules.log), with API evidence unchanged at 809.

Immediate pre-prompt failure: the internal activation path now journals failed
initial payments and invokes shared recovery rather than immediately throwing.
PaymentRecoveryApplied records/reserves the journal ID without opening a choice.
Life payment followed by successful mill and failing mill preserves the movement,
refunds life, retains only the library trigger, and replays the ID reservation.
No prompt or activation event is emitted. The initial test attempt did not compile
due to an ambiguous event type; corrected verification passes 1,643 rules / 809
API (library-immediate-recovery-rules-final.log, library-immediate-recovery-api.log).
Normal activation remains gated; broader immediate recovery cases remain open.

Mixed sacrifice/commander batches exposed duplicate object-ID allocation when a
later replacement ran against the original simultaneous board. The ordinary
arrival and replacement arrival both received ID 10 in the diagnostic fixture;
recovery correctly refused. ApplyMove now overlays live allocation counters/RNG
through the helper also used by resumed snapshot evaluation, preserving the
original evaluation board. Four cases verify ordinary/commander cards and
common/mixed graveyard/exile destinations, distinct IDs, restored order/history,
candidate cleanup and removal of undone triggers. Full suites pass 1,642 rules /
809 API (library-mixed-batch-rules-final.log, library-mixed-batch-api.log).
This closes these sacrifice-batch cases, not all replacement dependencies or
normal activation-pause integration.

Selected exile/return batches use shared movement recovery. Four cases cover
two-card exile from battlefield, hand and graveyard and return from battlefield
to hand, restoring identities/order/history while retaining mill through replay.
Battlefield cases reproduced recovery refusal; hand/graveyard initial failures
were fixture filters corrected to omit ObjectFilter.Any's battlefield default.
Full suites pass 1,639 rules / 809 API (library-selected-movement-rules-final.log,
library-selected-movement-api.log). Commander batches and mixed replacement
destinations remain unverified; normal activation pauses remain disabled.

Selected nonrandom discard batches now share sacrifice's validator/restorer.
Two chosen cards, named in reverse hand order with a surviving card between them,
return to their exact identities/order after later mill failure. Serialized
choice recovery preserves history and the completed mill. Pre-change failure:
library-batch-discard-before.log. Full suites pass 1,635 rules / 809 API
(library-batch-discard-rules.log, library-batch-discard-api.log). Random discard,
mixed replacement destinations and broader batch cases remain unfinished.

Selected sacrifice batches now share movement validation/restoration with self
payments. Aggregate changes are checked, and restoration traverses the batch
backwards. A two-card sacrifice chosen in reverse battlefield order, with a
surviving permanent between the cards, restores exact identities, order and
history while retaining a completed mill. Both deferred departure triggers are
removed through serialized choice recovery. Reproduction:
library-batch-sacrifice-before.log. Full suites pass 1,634 rules / 809 API
(library-batch-sacrifice-rules-final.log, library-batch-sacrifice-api.log).
Mixed destinations, commander batches and selected discard/exile/return remain
unfinished; normal activation pauses remain disabled.

Payment integration inventory: LIBRARY_IMPLEMENTATION.md now maps current
CostPayment.One/ObjectCosts.Pay implementations against recovery support. The
next implementation gap is selected-card movement batches (sacrifice, nonrandom
discard, non-library exile and return to hand), not undeveloped cost mechanics.
Random discard explicitly refuses; several other declared cost records have no
payment branch. The audit also identifies immediate failures before the first
pause: ActivateCore throws before establishing the journal, so resumed-choice
tests cannot prove that path. Batch restoration, retained dependencies and this
initial failure path precede normal activation integration. This is source-review
evidence and a concrete work order, not runtime acceptance or completion.

Zero-amount costs: a shared no-op assessment requires empty player/object/zone
changes, no movement change and empty events or zero-delta counter events. Zero
life, tap, mill and counter-removal cases previously blocked later library-payment
recovery; all four now preserve the completed mill and refund life through replay.
Reproduction: library-zero-cost-before.log. Full suites pass 1,633 rules / 809
API (library-zero-cost-rules.log, library-zero-cost-api.log). Declared zero amounts
alone do not qualify a step; retained library effects still take precedence.

Self-exile recovery uses the shared movement validator/restorer for battlefield,
hand and graveyard sources. Six cases cover ordinary and commander identities,
history and candidate cleanup, retained mill, and serialized answer replay. The
initial fixture incorrectly queried a player-specific exile zone; corrected
tests use the movement event's destination, so the initial failure log is not
a defect reproduction. Full rules pass 1,629 (library-self-exile-rules-final.log);
API passes 809 (library-self-exile-api.log). No separate restoration algorithm,
new card definition, or normal activation-pause enablement.

Counter-removal recovery: validates counter-only changes and the recorded delta,
walks successive payment maps backwards, and restores through the existing
PaymentRecoveryApplied event without new CountersChanged occurrences. A regression
removes one then two charge counters before interrupted mill and later failure;
replay restores the original source, including unrelated storage counters, and
retains the mill. Pre-change failure: library-counter-recovery-before.log.
Full suites pass 1,623 rules / 809 API (library-counter-recovery-rules.log,
library-counter-recovery-api.log). Retained mutations of the same object remain
unsupported; general activation pauses remain disabled.

Multi-permanent tap payment recovery: TapOther now shares the existing pure
tap-state validation and restoration path. A named two-creature payment followed
by interrupted mill and a failing later mill restores both original objects,
retains the milled card, and emits no new ObjectChanged occurrence. Reproduction:
library-tap-other-before.log. Full suites pass 1,622 rules / 809 API
(library-tap-other-rules.log, library-tap-other-api.log). Other unsupported costs
and retained dependencies still prevent general activation-pause enablement.

Retained library-arrival trigger verification: tests now include a deferred
life-loss trigger from the payment that will be refunded, and a watcher for the
retained battlefield-to-library movement. Settling and serializing the pause,
then answering through Game.Apply, removes the former and puts exactly one of
the latter on the stack. The reveal-only replacement produces no trigger. All
1,621 rules tests pass (library-arrival-trigger-rules.log); no production change.

Library arrival/reveal recovery: payment assessment now retains completed moves
into a library and reveals with explicit library-origin evidence. The shared
reveal event adds FromLibrary, populated before disclosure from source zones;
public RevealedCard snapshots remain identity-free. Missing legacy evidence
defaults false. Actual primitive replacement effects replace sacrifice with a
library move or reveal before a later mill fails; both command-answer recovery
and serialized replay preserve the action and refund earlier life payment.
Hand/library reveal tests verify the origin flag across serialization. Both
recovery cases reproduced refusal before the fix (library-arrival-reveal-before.log).
Full suites pass 1,621 rules / 809 API (library-arrival-reveal-rules.log,
library-arrival-reveal-api.log). No normal activation-pause enablement or new
named card definitions.

Replacement-shuffle recovery: explicit library ZoneReordered(Shuffle: true)
events now mark the completed payment operation as retained. Real competing
replacement effects shuffle instead of milling, then a later mill payment fails.
AnswerChoice recovery refunds preceding life payment while preserving library
order and RNG state through serialized replay, with no library departure. Both
one-card (unchanged order) and four-card cases pass. Reproduction:
library-shuffle-recovery-before.log. Full suites: 1,617 rules / 809 API
(library-shuffle-recovery-rules.log, library-shuffle-recovery-api.log). Normal
activation pausing and broader recovery remain incomplete.

Latest recovery validation: pure life payment now requires life-only player
changes matching the emitted life events, sharing delta validation with mana
recovery. A malformed-journal regression reproduced acceptance of an unrelated
draw-counter change; both that case and mismatched life totals now refuse
recovery. Full suites pass 1,615 rules / 809 API tests
(library-life-journal-rules.log, library-life-journal-api.log). The earlier
retained-draw regression also proves restoring a discarded source preserves a
drawn card and draw history through event replay of a constructed library
operation. Neither check enables normal activation pausing.

Self-discard recovery: PaymentMovementRecovery replaces the sacrifice-only
validator and owns shared object, source-zone ordering, arrival and history
restoration. Sacrifice expects a battlefield origin and departure history;
DiscardSelfCost expects a hand origin and no battlefield departure/death. The
public restoration payload and event properties now use movement names, retaining
their previous JSON field names for earlier recovery logs. No second movement
algorithm was introduced. A new internal activation test discards its source,
pauses on a mill replacement, then fails another mill; command answering restores
the original hand object while retaining the milled card and unchanged history.
It failed before implementation (library-discard-recovery-before.log).
Full suites pass 1,614 rules / 809 API tests (library-discard-recovery-rules.log,
library-discard-recovery-api.log). This does not enable ordinary activation pauses
or establish every discard replacement/dependency case.

Commander opening-library acceptance: LibraryTable now accepts an optional format,
defaulting to casual. The existing four-seat mulligan harness supports --commander
and prepares a 100-card fixture with the existing Avacyn, Angel of Hope definition
and 99 Plains. Normal deck import, commander selection, lobby admission and start
produce 40 life, one command-zone card and 92 library cards after opening draws
for all players. Every opening hand excludes the commander. Existing keyboard
declaration, free first multiplayer mulligan, second-mulligan bottoming, private
refresh and first-player turn-one draw assertions pass. After mulligans, each
view verifies library plus hand equals 99 and one commander remains separate.

Table e749a96b-23ff-4240-839c-b3ca7a2499d1; client log
library-commander-mulligans-live.log. Reviewed captures under e2e/screenshots:
library-commander-mulligans-bottom-after-refresh.png and
library-commander-mulligans-first-draw.png. No new definitions or production code.
This proves Commander-format opening-library integration, not Commander search,
partner/background variants, MTGO parity or responsive-layout completion.

Recovery-feedback fix: SignalRService associates uncertain-choice warnings with
the game and choice that were sent. A late invocation failure cannot create the
warning after that choice is resolved; both authoritative view handlers remove
an existing warning when the pending choice changes. ToastService.show returns
the created toast ID so only that warning is dismissed. Two regressions failed
before the change and now verify late-error suppression, retaining the warning
for the same pending choice, and preserving unrelated toasts on resolution.

`npm run verify` passes 1,822 tests, coverage checks and production build
(client library-choice-warning-verify.log). Existing card-modal stylesheet budget
warning remains. Live table 2213ed26-feb4-42e6-9cfc-309cc3ffb859 passes the
accepted-result automatic-reconnect scenario, including warning dismissal within
one second of recovery, before its five-second expiry. The one-second bound
allows the existing 150ms exit animation. Reviewed the refreshed
library-search-accepted-auto-reconnected.png capture; no obsolete warning is
visible. Log: library-search-accepted-auto-reconnect-warning.log. This supersedes
the recovery-feedback defect below, without closing packet-level outage coverage.

Accepted-result automatic reconnect follow-up: the shared live-search harness now
also supports `--accepted-auto-reconnect`. It sends one real answer, withholds
ViewUpdated from the acting browser, confirms the underlying invocation completed,
restores the normal view handlers, interrupts the actual long-polling transport
and rejects the wrapped invocation. It observes automatic reconnect, a changed
connection ID and a new authoritative view. The search prompt disappears without
reload and the answer-send count remains one. Both live seats verify the tapped
arrival, library count reduced by one and expired inspection; direct-choice
privacy assertions pass. This controlled delivery fault is not a packet-level
network outage or proof of every ambiguous-delivery ordering.

Table: 432432a7-cdff-40b9-8537-1ad40ef304db.
Client log: library-search-accepted-auto-reconnect.log.
Reviewed capture: e2e/screenshots/library-search-accepted-auto-reconnected.png.
The capture exposes an outstanding UI defect: the generic Choose invocation
catch displays “Choice status unknown. Reload to sync.” after the view has
already synchronized. SignalRService.invoke unconditionally emits this toast;
it does not correlate failure feedback with the answered choice or a later
authoritative view. State recovery passes, but recovery feedback is unfinished.
No production code changed in this acceptance pass.

Automatic reconnect acceptance follow-up: client
`e2e/verify-library-search-live.js --automatic-reconnect` interrupts the actual
browser transport while a selected search answer remains unsubmitted. The browser
uses LongPollingTransport in this environment. The test stops that transport,
not the HubConnection, and observes both automatic reconnect callbacks, a changed
connection ID and a fresh ViewUpdated after the application's normal Join.
It verifies the same selection survives, reconnect sends no answer, and subsequent
Confirm sends one answer. Existing two-seat count, tapped arrival, inspection
expiry and direct-choice privacy assertions pass.

Table: 46911bb2-6d36-48be-84af-08ca050f1039.
Client log: library-search-automatic-reconnect.log.
Visually reviewed: e2e/screenshots/library-search-automatic-reconnect-selected.png.
No production code changed. This is real transport interruption and automatic
rejoin evidence, not an OS/network outage, WebSocket test, or ambiguous-delivery
test. Earlier harness attempts correctly refused a nonexistent WebSocket; the
observed transport determined the corrected injection. Those broader acceptance
requirements remain open.

Library visibility during internal activation payment: the announcement now
retains each player's initial library top. Shared `LibraryDisclosurePolicy`
gates both public reveal tracking and owner-only top-card projection. An unchanged
original top remains available under its existing permission; a different top is
withheld until completion. Four replay cases cover public/private permissions
and repeated replacement choices across two mill costs, followed by availability
of the final top after payment. Full suites pass: 1,595 rules tests and 809 API
tests (`library-payment-disclosure-rules.log`, `library-payment-disclosure-api.log`).
This is scoped to the internal activation continuation; public activation remains
gated, and casting/special-action timing is not established by these tests.
The general timing requirement is CR 401.5; Wizards' update explanation is at
https://magic.wizards.com/en/news/announcements/outlaws-of-thunder-junction-update-bulletin.
No recovered MTGO method counterpart has been established for this policy.

Persisted activation continuation follow-up: internal `BeginActivationPayment`
uses the ordinary activation validation and shared cost queue. Its server-only
`ActivationPayment` retains the command, announced modes and payment history;
source and characteristics remain in the existing payment context. ChoiceSystem
finishes the interrupted zone batch before calling `ResumeActivationPayment`.
Repeated choices retain the frame, a later cost can pause again, and completed
payment creates one stack object and resets priority to the activator. Control
events are excluded from history to prevent recursive continuation growth.
Tests reconstruct state and services after every answer, cover two and three
replacement effects, two separate mill costs, sacrifice of the source between
pauses, retained X, one completion and subsequent resolution. API reconnect
tests retain the frame on the server and withhold it from both player projections.
Full suites: 1,593 rules and 809 API tests in
`library-activation-continuation-rules.log` and
`library-activation-continuation-api.log`.

This path is **internal preparation, not enabled gameplay**. Public Activate
still refuses a replacement pause. Failure recovery for a later unpayable cost,
game-level trigger/disclosure timing, and spell-casting continuation remain
required before enabling it. The test begins internally and answers through
ChoiceSystem, not through a live two-player API game. No MTGO equivalence is
established by these tests.

Activation completion follow-up: `CastingSystem.CompletePaidActivation` now
produces only completion events from an already-paid state. The ordinary
non-mana activation path uses this same method and prepends its payment events
to the returned log without applying them again. Payment history supplies mana
provenance. A serialized replay regression pays mill and life costs, reconstructs
the paid state, then checks that completion emits neither another movement nor
another life charge and creates exactly one stack object. Full suites pass:
1,591 rules and 809 API tests (`library-activation-delta-rules.log` and
`library-activation-delta-api.log`). This removes a replay obstacle; automatic
resume, complete announcement persistence, rollback, mana-ability completion,
and disclosure timing remain unfinished. This is not an MTGO parity claim.

Payment-context persistence follow-up: the internal remaining-payment record now
retains the announced payer, source snapshot, characteristics, X, payment
restrictions and choices. Resume reconstructs its context using fresh runtime
services rather than an out-of-band original context. Replay tests cover both
mill and exile library costs; API projection tests verify that this server-only
context is withheld from both players. Full suites: 1,590 rules tests
(`library-payment-context-rules.log`) and 809 API tests
(`library-payment-context-api-recheck.log`). The API rerun used an unrestricted
testhost after the restricted run stalled. This is local persistence evidence,
not MTGO method parity or completed casting/activation continuation.

Mill-cost implementation follow-up: ObjectCosts now accepts MillCost, evaluates
the count, requires enough cards before payment, and moves the exact top slice
through IZoneSystem.MoveAll. Zero, exact and partial-library payments pass;
insufficient libraries receive the specific 701.17b refusal. No draw events or
randomness changes are introduced. Serialized resulting library order is tested.
The four initial behavioral cases failed with unsupported-cost refusals before
the change (library-mill-cost-red.log, after correcting fixture priority).

This is **partial support**, not completed cost continuation: a replacement
choice during mill payment is explicitly refused before an activation can be
returned as committed. A regression covers that boundary. Resuming payment after
the choice, then finishing casting/activation and disclosure timing, remains
required. Full suites pass: 1,585 rules and 809 API tests in
library-mill-cost-rules.log and library-mill-cost-api.log. The API run was repeated
after stopping a process that locked its DLL. No card definitions were added.

Replacement-payment follow-up: a new engine test replaces each of two mill
movements with life gain, with life costs before and after the mill cost. The
activation commits once, both library cards remain, the graveyard stays empty,
and the four life changes balance exactly and survive replay. This verifies that
replacement-modified payment counts as paid rather than requiring graveyard
arrival (retained CR 118.11). All six LibraryMillCostTests cases pass in
library-mill-cost-replaced.log. This does not close paused payment continuation.

Required continuation ownership: CostPayment must retain the remaining costs and
chosen-object cursor; casting/activation must retain its announcement and finish
only after payment resumes. ChoiceSystem must finish the deferred zone batch
before continuing costs. Replaying the original activation would pay earlier
costs twice and is not an acceptable continuation implementation. Resumption
must preserve replacement ordering, remaining payment, source/target identities,
and the top-card disclosure boundary together. These are implementation
requirements, not claims that the current code already provides them.

Shared payment guard follow-up: the library-exile variant of the interruption
regression reproduced successful activation despite a new replacement choice
remaining open (library-cost-interruption-red.log). The guard now belongs to
CostPayment.Pay rather than the mill-specific handler. It rejects a newly raised
payment choice before later costs or caller completion. An unchanged enclosing
prompt is allowed: the first broad guard broke two existing instructed-cast and
may-pay cases, and was corrected to compare pending choice IDs. The corrected
full rules suite passes 1,587 tests (library-cost-interruption-rules.log).
The separate API rerun passes 809 tests (library-cost-interruption-api.log), after
the earlier combined shell terminated before an API result was produced.
This is a shared refusal boundary, **not resumable payment implementation**.

Payment sequencing foundation: CostPayment.Begin now returns CostPaymentProgress
with the outcome and a serializable RemainingCostPayment (unpaid cost queue and
unused chosen object IDs). Chosen alternatives are expanded into that same queue,
so a pause inside a nested Costs alternative retains the outer remainder too.
CostPayment.Resume requires the interrupted operation's choice to be finished
before consuming that queue. The existing Pay entry point delegates to Begin and
retains the refusal boundary until casting/activation continuation is integrated.

The new regression serializes payment progress and events, answers two mill
replacement pauses, then resumes life/discard costs. It verifies the earlier
discard and life payment are not repeated, the correct remaining hand card is
used, and four distinct objects move exactly once. Eight targeted cases pass
(library-payment-progress-tests.log); the full rules suite passes 1,588 tests
(library-payment-progress-rules.log). This test keeps payment progress separately
from ChoiceContext: automatic persisted game-level resumption is still required,
as are rollback, disclosure timing and caller completion integration. The full
API suite also passes 809 tests (library-payment-progress-api.log).

Object-cost batch follow-up: the serialized progress regression now covers
ExileFromZoneCost selecting two library cards as well as MillCost. Before the
change, only one replacement decision survived the per-card ObjectCosts.MoveEach
loop (library-object-cost-batch-red.log). MoveEach now emits its existing named
cost markers and delegates movement to IZoneSystem.MoveAll, retaining the rest
of the cost's movement through the shared zone-batch continuation. Both choices
complete through replay and subsequent costs use the correct remaining selected
card, without duplicate moves/payments. Full rules pass: 1,589 tests in
library-object-cost-batch-rules.log. Game-level payment continuation and the
timing of cost markers/triggers across pauses still require integration tests;
the legacy Pay entry point continues to refuse newly interrupted payments. The
full API suite passes 809 tests (library-object-cost-batch-api.log).

Activation completion boundary: CastingSystem.Activate now delegates its paid
outcome to one CompleteActivation method. That method owns mana provenance,
mana-ability completion, stack construction, announcement metadata and the single
AbilityActivated event, and rejects a newly pending payment. This is preparatory
refactoring; it does not yet wire payment progress into persisted choices.
Full rules pass 1,589 tests (library-activation-completion-rules.log).
Full API tests pass 809 (library-activation-completion-api.log).
Before integration, account for Game.Settle's deferred trigger collection and
top-card reconciliation: a payment pause must not grant priority, expose a newly
changed library top, or collect the same cost occurrences twice. Completion also
must not apply already-persisted payment events again.

Payment persistence/privacy follow-up: CostPayment.Begin attaches RemainingPayment
to its raised ChoiceContext. ChoiceSystem retains those unpaid steps when answering
one replacement creates another, including a later move in the same zone batch.
ChoiceSecrets removes this server-only field, including when it is the only
private context. API tests reload the event log and verify both projected choices
and direct choice pushes omit it while the server keeps the selected hidden ID
and remaining MillCost. Payment progress tests read it after every serialized
pause. Full suites: 1,590 rules / 809 API tests, recorded in
library-payment-persistence-rules.log and library-payment-persistence-api.log.

This persists the unpaid steps, not the complete casting/activation announcement.
The terminal choice still requires a caller to resume payment and finish the
announcement; the existing Pay path continues to reject newly interrupted costs.
Rollback, source/target/mana metadata, top-card disclosure and trigger boundaries
remain open. No end-to-end support is claimed by this persistence step.

Cost-call-site audit (2026-10-23): direct Draw/Shuffle call-site search finds no
cost-payment call to either operation. This is not evidence that all library
costs work. MtgEngine.Cards/Costs.cs declares MillCost, but CostPayment.One and
ObjectCosts.Pay have no MillCost arm, so it reaches the explicit unsupported-cost
refusal. Implementing mill as a cost is a concrete outstanding library requirement.
It must use shared library movement and enforce payment in full rather than reuse
an effect's permissive "as many as possible" behavior without validation.

ExileFromZoneCost is dispatched through ObjectCosts.MoveEach and can name Library.
MoveEach and CostPayment.Pay iterate without a pending-choice stop; Activate
constructs AbilityActivated/stack events from the payment outcome without an
explicit pending-payment continuation. This is a source-level concern requiring
a replacement-pause regression, not yet a reproduced failure. Rule 401.5 in the
retained rules text requires newly changed top-card disclosure to wait until
casting/activation/special-action completion. No claim of cost/disclosure
completion follows from earlier draw/shuffle or resolution tests.

Manual retry follow-up: two API cases release eight draw/shuffle requests from
one asynchronous start gate. Exactly one applies and saves; seven are refused.
After restoring a fresh session service, the same player's reuse of that request
ID for the other action is refused, while another player's identical ID is
accepted for that player's own draw. Library counts, actor attribution and save
counts are checked. Evidence: library-manual-concurrent-retries.log (2 passed).
This covers service-level concurrency and persistence, not a browser connection
loss after server acceptance. Manual controls remain an honour-system extension,
not recovered MTGO library commands.

Shuffle caller follow-up: EffectInterpreter.Shuffling already retains affected
owners in continuation marks and returns before shuffling when Relocate pauses.
CommanderReplacementTests now serializes/replays each paused stage, checks no
shuffle or RNG advancement while commander choices remain, and compares final
library order/RNG with replay. The shuffle cases cover a first-card pause, a
later-card pause with two owners, and two successive commander choices. All
shuffle events must follow the last completed move. Ten targeted tests pass in
library-shuffle-caller-replay.log. No production defect was reproduced; arbitrary
replacement effects and a complete shuffle-caller inventory remain outside this
specific result.

GameHubTests.LibraryChoicePrivacy now covers both mixed private context and a
context whose only private field is DeferredShuffles. After persistence and a
fresh GameSessionService restore, the chooser's direct ChoiceRequested push and
both player projections omit the deferred list; the server retains its exact
player order, including duplicates. The other player receives no direct choice
notification. Two API cases pass in library-deferred-shuffle-api-privacy.log.

ChoiceProposalPrivacyTests additionally verifies the deferred list at replacement
continuation depths 0, 1 and 3: viewer projections omit it while serialized replay
retains it at every nested level. Together with the existing arrival-before-shuffle
resolution regressions, five targeted rules cases pass in
library-deferred-shuffle-replay-privacy.log. No production behavior changed in this
pass, and these targeted tests do not establish complete library or MTGO parity.

| Requirement | Evidence inspected / verdict |
| --- | --- |
| Clean ownership and no duplicate implementations | Search and arrangement coordinators use runtime interfaces; EffectRun remains inside interpreter adapters. LibraryIdentity owns reorder identity mapping for interpreter and zone batches. See LIBRARY_OWNERSHIP_AUDIT.md and library-identity-owner-rules.log (1,571 rules). Complete call-site and dependency audit remains incomplete; exposed continuation marks and the deferred instruction representation still need review. |
| Draw, shuffle, mill | Prior regression logs cover draw/replacement continuation, limits, empty draws, seeded shuffle and interrupted mill. Conditional draw restrictions/cost legality and a complete requirement audit remain open. |
| Search | Current code freezes participants, choices and destinations; rules suite 1,499 passed after correction. Four-player live search passed. Shared-library contention and genuinely simultaneous movement/trigger behavior remain open. |
| Search client decisions | Select/Confirm method mapping recovered from MTGO; 1,802 client tests and verification passed. Two-player offline/retry live test passed. Ambiguous delivery after server acceptance is not covered by the failed-send test. |
| Opening hands/mulligans | Current four-browser Commander partner run verifies 40 life, two commanders excluded from each opening library, 91 cards after seven drawn, free first mulligan, private bottom choices, refresh, UI reorder/confirmation and first-turn draw. Separate four-seat run verifies kept-hand identity preservation. Configurable Keep/Mulligan bindings, collision checks, unbind/reset and reload were implemented and live-verified; see the shortcut checkpoint below and library-configurable-shortcuts-verify.log (1,814 tests). Complete rule and MTGO method audit remains open. |
| Private inspection and replay | Active search inspection is separate from historical snapshots; normal completion, viewer/owner departure and game end revoke access. Serialized four-seat tests now verify private contents through APNAP decisions and replacement pauses, filtering moved cards, final expiry and retained history (library-multiplayer-inspection-rules.log, 1,512 passed). Two-player active-window live checks passed. Manual inspection expiry and surveil detail expiry now have four-browser evidence in the newer checkpoints. The full inventory of non-search disclosure lifetimes remains unaudited. |
| Scry/surveil/order | Shared arrangement path and four-seat serialized tests exist. Four-browser scry and surveil now pass with one acting player and three observers; see tables ee962c32-1b92-423f-a51a-c2297236c6c8 and 1de2cdfc-18a0-4bb1-a12f-3ebc41e4a779 below. A single all-player effect spanning multiple private decisions in four browsers remains unverified. |
| RevealUntil | Generic planner and engine tests exist. Two-browser Abundance table 9687f8c0-c45d-4ff1-a1f7-b922b7d3588c verifies three revealed cards, a nonempty prefix and remainder ordering, replacement-choice refresh, public history, counts and active-access expiry (client e2e/screenshots/library-within-zone-identity-live.log). This does not prove all replacement combinations or MTGO method equivalence. |
| Continuous revealed/lookable top | LibraryRevealCoordinator tracks disclosure and identity transitions; LibraryIdentity supports operation-local remapping. Direct and resumed revealed-card batch regressions pass. Exact visibility timing during interrupted costs, casting and activation remains unverified; a seeded tracking fixture is not proof of those runtime boundaries. |
| Manual library controls | Draw/shuffle/look/stop-looking, top/bottom reposition, configured search and full arrangement are implemented with explicit table permission and request IDs. Current checkpoints record four-seat search/full-order acceptance, Chrome touch and live draft expiry. Physical devices and full MTGO method parity remain open. |
| UI and responsive acceptance | Active inspection is rendered separately from historical looks, and active details close on expiry. Desktop, landscape, touch-input and live surveil-detail captures are recorded above. Client touch-menu gate passes 1,840 tests and production build. No complete final audit of every library state at the required sizes; physical-device behavior remains unverified. |
| MTGO traceability | LIBRARY_METHOD_PARITY.md now has recovered library select/OK methods. Counts, contextual actions, mulligan prompts and other interactions still need complete mappings. Server algorithms remain unknown. |

## Historical implementation checkpoints (superseded by newer evidence above)

### Configurable Keep/Mulligan shortcuts delivered (2026-10-23)

Settings -> Play now edits device-local opening-hand keys and modifier combinations,
rejects duplicate assignments, supports unbind and resets only library shortcuts.
Bindings and button hints share the same policy; pending/unauthorized choices
remain guarded. The live two-player flow changed bindings through settings,
tested collision/unbind/reset, reloaded, rejected old keys and completed opening
hands with Shift+m/k. Table 8d6a013f-d0f5-4a61-86fd-1f3c1a01b5f0; client
e2e/screenshots/library-configurable-shortcuts-live.log. Desktop and phone editor
captures were inspected, including lower controls. The search-results grid was
corrected to use the available width when category navigation is hidden.

Final npm run verify passed: 1,814 tests, all 31 changed files above the coverage
threshold and production build (library-configurable-shortcuts-verify.log).
Existing card-modal 18.05 kB / 16 kB warning remains. This supersedes earlier
opening-hand remapping gaps; global key configuration and exact MTGO selector
confirmation remain unverified. No commit made.

### Current arrangement browser acceptance (2026-10-23)

Fresh two-player runs after coordinator extraction and identity refactoring:
- Scry table b9a3d540-691f-4748-94fa-9d64933d97a6: private scry 2, refresh,
  top-order controls and completion pass. A subsequent draw and scry confirm
  server top order and the remaining card identity. Evidence: client
  e2e/screenshots/library-arrangement-current-scry.log.
- Surveil table 82ae4994-3d3e-4689-a820-4453ef1bc5a8: private inspection,
  refresh, phone selection and public graveyard result pass. Evidence: client
  e2e/screenshots/library-arrangement-current-surveil.log.

Inspected library-scry-v2-top-order-landscape.png and
library-surveil-completed-landscape.png. These are two-seat flows; they do not
close the four-browser simultaneous arrangement gap. No production code changed
in this verification pass; the existing client gate was not rerun unnecessarily.

### Shared-library shuffle timing and replay (2026-10-23)

The shared-library matrix now includes shuffle-enabled cases with three unmatched
lands remaining, for distinct/overlapping selections with/without replacement
pauses. Assertions prohibit shuffles before all choices and during replacement
prompts, require the first shuffle after the selected library-to-hand moves, and
compare replayed order and random-generator state with the live engine result.
All 14 LibrarySearchSimultaneousTests cases pass in library-shared-shuffle-tests.log.
The initial ordering assertion incorrectly included the resolving spell's later
graveyard move; it was corrected to compare selected-card moves. No production
bug or code change was established by that test failure.

This verifies ordering, conservation and deterministic replay for these shared
library instructions. It does not determine MTGO's server implementation or
establish shuffle-replacement interactions or four-browser acceptance.

### Shared-library replacement continuation (2026-10-23)

Extended Shared_library_selections_do_not_duplicate_cards_and_survive_replay
with two competing library-to-hand replacement effects. All four cases pass
(library-shared-search-replacement-tests.log): distinct/overlapping selections,
each with and without replacement pauses. The paused cases replay serialized
events after each answer, ask once per actual move, expose no zone snapshot or
deferred zone moves in either player's projection, and keep inspected cards
limited to the current library. Final movement/conservation and expiry checks
remain in place. No production change was required. This closes those specific
replacement-continuation scenarios, not shuffle contention, arbitrary replacement
effects, live multiplayer coverage or MTGO equivalence.

### Shared-library selection regression (2026-10-23)

LibrarySearchSimultaneousTests.Shared_library_selections_do_not_duplicate_cards_and_survive_replay
adds two engine scenarios for two searchers authorized to inspect the same owner's
library: they select different cards, or both select the same card. Both pass
without production changes (library-shared-search-tests.log, two targeted tests).
The scenarios verify serialized replay after each answer, no movement before all
choices finish, choice-option privacy, both authorized inspections, inspection
expiry, no duplicated moves/cards and owner-correct hand destinations.

This is a scoped invariant test of an instruction the engine already represents.
It does not establish recovered MTGO shared-library selection semantics, whether
every real card permitting overlapping searches has been represented correctly,
or replacement/shuffle contention and four-browser behavior. Those remain open.

### Landscape inspection label correction (2026-10-23)

LibraryRevealComponent's summary now uses compact text and wraps long owner names
within the short-viewport layout. The new live assertion checks that the closed
inspection host does not scroll horizontally; the same run collapses and reopens
the expanded panel. Two-player table 3e7ca843-55c2-4b79-9cac-24e3c78ba891 passed
the complete search flow (client e2e/screenshots/library-label-wrap-live.log).
Inspected library-search-inspection-closed-landscape.png,
library-search-inspection-landscape.png and desktop library-search-inspection.png.
The captured closed owner name is fully wrapped and the expanded panel remains
usable. This closes the specific label clipping defect recorded below; it does
not establish complete phone-layout or MTGO presentation parity.
Client npm run verify passed: 1,809 tests, coverage gate and production build
(library-label-wrap-verify.log). The existing card-modal stylesheet budget warning
remains. No commit was made.

### Current live search and client gate (2026-10-23)

Two browser players completed the Evolving Wilds search at table
f47e4c3e-434d-4978-93b2-1d77108e074e after the direct-zone and revealed-card batch
corrections. The existing live harness verified private inspection, refresh,
filtering, card details, retained selection, tapped Mountain arrival, matching
counts and inspection expiry. Log: client
e2e/screenshots/library-current-search-live.log. Inspected captures:
library-search-landscape.png and library-search-search-completed.png.

The 667x375 capture shows the closed inspection's owner label cramped and clipped
in the narrow sidebar, with horizontal scrolling. Functional acceptance does not
close that layout defect. The next responsive correction should keep the closed
inspection label readable without horizontal scrolling, preserve the full-screen
expanded inspection, and verify both states in a new live capture.

npm run verify passed (client library-current-verify.log): 1,809 tests, coverage
gate for 28 changed files, and production build. The existing card-modal SCSS
18.05 kB / 16 kB warning remains. This live search is not evidence for the new
manual or interrupted same-library movement cases; those retain their separate
engine-test evidence. No MTGO equivalence claim follows from this run.

### Generic within-library movement identity correction (2026-10-23)

Added identity assertions to the owner-ordering fixture; both top/bottom cases
failed because generic Move replaced hidden library IDs (library-within-zone-identity-red.log).
Search already distinguished within-library placement from zone movement; generic
Relocate did not. It now uses shared LibraryOrder and ZoneReordered for its
within-library branch, retaining proposal-ordinal accounting and existing reveal
identity remapping. The move loop reads remapped IDs as it progresses and avoids
duplicate remembered arrivals. Hidden IDs remain stable; additional revealed-top
cases preserve the requested card order without library ObjectMoved events.

Correction to earlier evidence: the competing-replacement movement fixture used
library-to-library movement as a zone-change proposal. It now uses genuine
hand-to-library movement with a creature replacement and two land arrivals.
Its old result did not validate proper same-zone semantics. Full rules 1,555 and
API 806 pass (library-within-zone-identity-*.log). Source rule reference inspected:
repository comprehensive-rules.txt, effective August 7, 2026, rules 400.5–400.7 and
401.6. The official rules page fetch did not complete and was cancelled.
Direct ZoneSystem.Move/MoveAll callers still require a separate same-zone audit;
this correction is scoped to interpreter relocation, not every zone-service entry.

Live follow-up: table 9687f8c0-c45d-4ff1-a1f7-b922b7d3588c revealed three cards,
ordered the two-card remainder through the browser and completed with matching
counts/history and expired inspection. library-within-zone-identity-live.log
passes. Hidden identity preservation itself is established by the engine tests;
the live count/history assertions do not expose hidden IDs after completion.

### Arrangement coordinator boundary (2026-10-23)

Scry/surveil sequencing now resides in LibraryArrangementCoordinator with its
planner and continuation classes. A library-owned runtime interface keeps the
interpreter cursor out of this domain; the adapter reuses shared prompts,
replacement-aware movement, identity refresh and inspection events. Full rules
1,553 and API 806 pass (library-arrangement-coordinator-*.log). Two-player live
surveil table c4333c73-9a7e-49d8-aa4e-b0f6804060a6 passes refresh, private choices,
phone selection, graveyard result and inspection expiry. Inspected the completed
landscape capture; log: library-arrangement-coordinator-live.log. No client
production changes. The fresh browser run covers surveil; scry remains covered by
the rules/API suites and the earlier live run before this final extraction.

### Search runtime dependency inversion (2026-10-23)

LibrarySearchCoordinator/ILibrarySearchRuntime no longer depend on the nested
interpreter EffectRun type. The adapter holds the cursor per invocation and offers
the required state, answer metadata and shared operations. Existing continuation
keys and movement/replacement implementation remain unchanged. Full rules 1,553
and API 806 pass (library-search-boundary-*.log). Live two-player table
caf10842-950c-44ed-8128-7bd4af49cc95 passed private full inspection, refresh,
filtering, details, select/reselect/Confirm, tapped destination, counts and access
expiry. Inspected library-search-search-completed.png; evidence:
library-search-boundary-live.log. Client production code did not change.
The library contract still exposes broad EffectContext/marks, and arrangement's
overall coordinator remains in the interpreter; this is not a completed SOLID audit.

### Current zero-selection search browser acceptance (2026-10-23)

verify-library-search-live.js --fail-to-find passed at two-player table
e6c58e8d-74fd-4777-9e8a-18dcd755326e using the existing Evolving Wilds definition
and a library containing eligible Mountains. The harness verifies private full
inspection, opponent redaction, refresh, filtering, details without submission,
portrait/landscape overflow checks, Choose none completion, unchanged library
count, no searched Mountain entering the battlefield, retained sacrifice cost,
empty choice cards and expired active inspection. The shared notification privacy
check now runs here too. Updated its old title assertion for owner-labelled panels.
Inspected library-search-choose-none-completed.png. Evidence:
library-choose-none-current-live.log. No production code changed. This confirms
our local zero-selection path, not MTGO's still-unmapped fail-to-find dispatch;
shuffle correctness is established by engine event tests rather than these counts.

### Four-player arrangement API persistence and hub privacy (2026-10-23)

Retry follow-up: both four-player cases now reject another player's answer,
unoffered IDs and the previous participant's stale answer after recreating the
session. Exact serialized event logs and save counts remain unchanged after
rejection. Concurrent delivery of the same valid answer produces exactly one
Applied, one Refused and one save at every decision. Replaying the final answer
after another restart also leaves the log/save count unchanged. Full API suite:
806 passed (library-arrangement-retry-api.log). These tests establish persisted
command behavior; they do not simulate an ambiguous browser transport failure
after server acceptance or simultaneous requests to separate server instances.

Inspected the existing card JSON corpus: its Scry/Surveil effect nodes have no
EachPlayer selector. No named definitions were added to manufacture browser
coverage. GameHubTests.LibraryArrangement instead uses a test-only generic effect
fixture for each operation. It restarts GameSessionService between every answer,
joins all four users at each decision and asserts APNAP order 2,3,0,1, private
inspection contents, chooser-only choice cards/direct push, redacted serialized
push equality with the authorized view, unchanged library counts before all
answers, final per-owner order and inspection expiry. Full API suite: 806 passed
(library-four-arrangement-api.log). An initial assertion compared immutable
dictionary references; it now compares the serialized payloads actually delivered.
This is API integration coverage, not a four-browser all-player acceptance run.

### Arrangement decision planner extraction (2026-10-23)

LibraryArrangementPlanner.Plan now owns pure scry/surveil selection-versus-order
decisions and prompt bounds; it has no interpreter cursor or mutations.
ArrangeLibrary uses the planner through existing Ask/Recall operations and keeps
the previous saved-choice keys. Full rules 1,552 and API 804 pass
(library-arrangement-planner-rules.log and library-arrangement-planner-api.log).
Two-player live scry table 55d570c3-e1b5-4099-a139-a414500a4210 passes private
inspection, refresh, UI ordering/confirmation, expiry and subsequent draw/second
scry verification of the actual server order. Evidence:
library-arrangement-planner-scry-live.log; inspected
library-scry-v2-top-order-landscape.png. Client production code did not change;
the preceding npm gate remains applicable. This extraction is partial: arrangement
participant/disclosure/movement coordination and search's EffectRun dependency
remain in the ownership audit.

### Nested continuation privacy (2026-10-23)

ChoiceProposalPrivacyTests now checks zero, one and three nested replacement
continuation contexts containing a hidden card proposal and private sentinel.
All three targeted cases pass (library-nested-choice-privacy-tests.log): neither
player's projected pending choice retains the continuation, the serialized owner
choice contains neither the hidden definition nor sentinel, and serialized server
replay retains every nested context and proposal. This is structural privacy and
replay evidence, not proof that every nested effect combination executes correctly.

The shared LibraryTable.assertChoicePrivacy now rejects replacementContinuation
in direct notifications as well as the existing event/deferred-state fields.
Two-player live RevealUntil table 32e7ff8f-d467-4d65-8529-bf95394bcbcc passed
the updated checks, including an eight-card reveal and seven-card bottom-order
prompt (library-nested-choice-privacy-live.log). No production changes were needed.

### Ordered movement interrupted by competing replacements (2026-10-23)

Two LibraryMoveOrderTests cases now reverse three cards for top/bottom placement,
move one land, pause on two competing replacements for the middle creature, then
replace that creature's move with life gain and continue the remaining land.
Each response is serialized/replayed. Assertions cover exactly one initial order
prompt plus one replacement prompt, exactly two library arrivals, the expected
final library sequence including the unmoved creature, life gain from the selected
replacement and following instruction exactly once, and one stack resolution.
The initial test counted the spell's graveyard movement as a third library move;
correcting its destination filter resolved that fixture error. No production
defect was established and no production code changed. Full rules: 1,550 passed
(library-order-replacement-rules.log). This covers one owner and a competing
replacement that prevents a move; combinations with four owners, redirected
destinations and nested replacement questions remain unverified.

### Four-owner library movement ordering (2026-10-23)

LibraryMoveOrderTests now includes top and bottom placement across four owners,
with active player seat 2 and resolving spell controller seat 0. Every choice is
serialized/replayed. Assertions establish decision order 2,3,0,1; two choice cards
only for the current owner; all original library contents/order preserved through
all decision prompts; no ObjectMoved or ZoneReordered events before the last
answer; and independently reversed two-card groups at each requested destination
with the remaining card preserved. Both new cases and the previous two pass.
Full rules suite: 1,548 passed (library-multiowner-order-rules.log).
This closes the multi-owner engine-ordering gap for this selector and placement
pair. Replacement-paused movement, simultaneous trigger semantics and live
multi-owner interaction are not established by these tests. No production code
or named card definitions changed.

### Per-owner inspection panels (2026-10-23)

Phone follow-up: extended verify-library-look-live.js runs the active inspection
at 375x667 and 667x375. The first attempt failed because portrait intentionally
shows the existing game-board orientation guard, not the hidden inspection text.
The harness now asserts that guard and no document overflow in portrait, then
asserts the owner's name, three displayed card controls of at least 44px height,
no document overflow and collapse/reopen in landscape. Table
59d3a449-61f1-4443-9f5f-79fe3865019e passed, including the original two-player
privacy, refresh and ordering completion checks. Inspected
library-look-active-landscape.png: owner title and all three cards fit the sheet.
Evidence: library-owner-phone-live.log. This supersedes the missing phone-label
capture below, but does not establish portrait gameplay support or simultaneous
multiple-owner live acceptance. Only the capture harness changed; production
verification remains the preceding 1,809-test gate.

LibraryRevealComponent now groups only already-authorized inspection cards by
ownerId and labels each group with its player's name. Stable owner keys preserve
open panels through view refresh/name changes. The new two-owner component test
also checks disjoint contents, independent expiry and active detail closure.
Two-player live table 0e04841b-59bf-4de1-8f61-4a518c44eb6d passed private inspection,
refresh, completion and opponent privacy; library-look-active.png was inspected.
Evidence: library-owner-inspection-live.log. npm run verify passed 1,809 tests,
changed-file coverage and production build (library-owner-inspection-verify.log).
Existing stylesheet budget warning remains. Concurrent multiple-owner panels
and the new label at phone width are not yet live-captured; the existing harness's
landscape detail capture does not establish that narrower panel acceptance.

### Latest verification correction (2026-10-23)

The summary rows above retain older limitations; this entry supersedes the
RevealUntil nonempty-remainder limitation. Table
da5277e7-2c2e-4ecb-9839-0134c2672c6e revealed six cards and offered the five-card
remainder for bottom ordering. The preceding capture exposed missing images and
Details: ViewProjector's ChoiceCards whitelist omitted LibraryBottomOrder.
That kind is now included. Serialized top/bottom movement tests assert that the
owner receives two choice cards and the opponent receives none, alongside final
order and unchanged-remainder assertions. Full rules: 1,546 passed; API: 804 passed
(library-move-order-rules.log and library-move-order-api.log).

The repeated live harness asserts an image and Details control for every ordering
option, selects the reverse order, confirms, and checks counts, public history and
inspection expiry. It passed; library-reveal-until-remainder-order.png was visually
inspected and shows all five images and Details controls. Evidence:
client e2e/screenshots/library-reveal-until-remainder-fixed-live.log. The existing
library-move-order-verify.log records npm run verify passing with 1,808 tests and
28 changed files meeting coverage; the stylesheet budget warning remains.
This is behavioral engine/browser evidence, not a recovered MTGO ordering-method
match. Multiple-owner movement and replacement-paused ordering remain unverified.

### Generic look/reveal lifetime and ordering gap (2026-10-23)

**Owner-ordered library moves.** Extended live table
26ce895d-e981-49b4-a212-cef74f723612 revealed five cards and moved the four-card
remainder without a choice, failing library-reveal-until-remainder-live.log.
Move ignored OwnersChoiceOfOrder. LibraryMoveOrderTests reproduced missing
prompts for both top and bottom placement (library-move-order-red.log).
The move path now gathers owner decisions in turn order before movement and saves
the selected movement sequence through pauses, reversing individual top insertions
so the resulting group order matches the answer. It reuses movement continuation,
Ask and Relocate; LibraryBottomOrder is a declared choice kind. Engine tests replay
the choice and verify final top/bottom order plus an unchanged remainder. Rules:
1,546 passed; API: 804 passed (library-move-order-*.log).
Live table 4d8f4a85-637d-4ff6-aa67-8cff983e0aef passed: after an immediate hit,
the next tested reveal contained eight cards. The seven-card remainder raised
LibraryBottomOrder; reversed selection and Confirm worked through the UI, final
hand/library counts and public history agreed, and active inspection expired.
Inspected library-reveal-until-remainder-order.png. Evidence: client
library-reveal-until-remainder-fixed-live.log. npm run verify passed (1,808 tests,
changed-file coverage and production build) in library-move-order-verify.log;
existing card-modal stylesheet budget warning remains. The final hidden bottom
order is established by engine tests, not inferred from the browser count checks.

**Paused draw-replacement follow-up.** Compiled LibraryReplacementDisclosureTests
reproduced two failures (library-replacement-disclosure.log): a standalone draw
resumed ReplaceWith as an unsupported interpreter verb, while a stack-based draw
restarted its replacement and asked again. Tree lookup now resumes the same
ReplaceWith body used initially. ProposeThrough retains a private inner
ReplacementContinuation separately from the outer proposal's path and resumes
that context before continuing the outer effect. ChoiceSecrets strips the inner
context from both viewers' projections. No second draw implementation was added.
Six serialized scenarios cover standalone/stack draws, two individual draws,
two successive questions in one replacement, retained private inspection, expiry,
no actual card draw, and the outer instruction executing exactly once. Full rules:
1,542 passed; API: 804 passed (library-replacement-disclosure-rules.log and
library-replacement-disclosure-api.log). This is engine evidence;
the exact replacement-disclosure sequence remains live-unverified. No card
definitions or client production code changed. Full nested replacement-order,
zone-move and other continuation combinations still need broader acceptance.

Competing-replacement follow-up adds two serialized cases (standalone and stack):
each of two draws first chooses Inspect instead over Gain ten instead, then
answers two internal May questions. Inspection privacy/expiry and exact life
totals prove the unchosen replacement does not execute and the outer instruction
executes once. Eight targeted cases and all 1,544 rules tests pass
(library-competing-replacement-disclosure.log, library-competing-replacement-rules.log).
No production changes were needed for these cases; prior API gate: 804 passed.

Two-player live table 9fb52ecb-57fa-4b3d-80a7-e6496b64d398 used the existing
Abundance definition with normal land plays and casting. The nonland choice
survived refresh, a nonland was revealed and added to hand, library count fell by
one, public reveal history matched both views and active inspection was empty
after resolution. Inspected library-reveal-until-resolved.png. This particular
shuffle revealed exactly one card; it does not prove nonempty remainder bottoming.
It also does not establish the generic competing-replacement/LookAt/May sequence
live. Harness: client e2e/verify-library-reveal-until-live.js; log:
library-reveal-until-live.log. No card definitions changed and no commit made.

**Revealed-top identity follow-up.** LibraryDisclosureIdentityTests reproduced
three inspected cards dropping to two after AnyOrder replaced the formerly public
top object's ID and paused on a later May (library-disclosure-identity-red.log).
Reordered now refreshes the executing effect's remembered references and only
its resolution-scoped disclosures using the positional identity mapping already
used by search/arrangement. Mapping construction is shared, with no global alias
or update to unrelated inspections. Serialized replay retains three inspected
cards for the authorized viewer and none for the opponent; after the later choice,
Exile(Remembered) still moves all three and access expires. Rules: 1,536 passed;
API: 804 passed (library-disclosure-identity-*.log). This combined public-top /
AnyOrder / second-choice sequence is engine-verified and remains live-unverified.
No client production change; the previous 1,808-test npm gate is unchanged.

**AnyOrder follow-up resolved and live-verified.** A compiled regression confirmed
the missing prompt (library-any-order-red.log). Reordered now asks the designated
By player (controller by default) through LibraryTopOrder before modifying zones,
then uses shared LibraryOrder.Arrange with the selected order. The regression
serializes the pending choice and answer, checks no early reorder, checks the
reversed prefix and unchanged remainder, and verifies inspection expiry.
Rules: 1,535 passed; API: 804 passed (library-any-order-*.log).
Two-player table 58143aad-6135-426e-8c31-6300e20570c3 passed with existing Sensei's
Divining Top: three active private inspection cards survived refresh, the other
viewer had none, reversed selection and Confirm used the UI, active access expired
and historical details remained. Inspected library-look-active.png and
library-look-details-landscape.png. Evidence: client library-any-order-live.log.
This supersedes the live-unverified result below for this private-look scenario;
public Reveal and off-stack lifetime variants are not established by this run.
Fresh npm run verify passed: 1,808 client tests, changed-file coverage and
production build (client library-any-order-verify.log). The existing card-modal
stylesheet budget warning remains. No named card logic changed and no commit made.

LibraryDisclosureLifetimeTests reproduced missing active inspection after generic
LookAt/Reveal followed by May (library-disclosure-lifetime-red.log, two compiled
failures). Both verbs now use the existing LibraryInspection model, with a
resolution identifier retained in serialized events. Root completion removes only
that resolution's disclosures; paused execution retains them. Resume now carries
the same stack object identity as initial execution. Existing projection filters
departed cards; history remains separate. Tests verify private versus public
viewers, serialized pause/resume, completion expiry, drawn-card filtering and
preservation of an unrelated inspection. Full rules: 1,534 passed; API: 804 passed
(library-disclosure-lifetime-rules.log and library-disclosure-lifetime-api.log).

Live acceptance is **unverified**. The extended existing Sensei's Divining Top
harness failed at table 0f4ebcb4-29ad-4787-bcd2-badb7b66bfc6 because active inspected
cards were already empty. Inspection of EffectInterpreter.Backlog.Reordered shows
that Placement.AnyOrder never asks for an order: it emits ZoneReordered using the
existing remembered order and immediately finishes. Thus the new expiry is
consistent with that execution, but the required ordering decision is missing.
Client library-disclosure-lifetime-live.log records the failure, and
verify-library-look-live.js retains the new active-inspection assertions.
Next: reproduce generic AnyOrder as an engine choice regression, route it through
the shared choice/order machinery, and rerun this live scenario. No named card
definitions were changed. Other lifetime edges (off-stack interruption, reveal
identity resets, nested effects) still need dedicated coverage. No commit made.

### Four live opening-hand seats (2026-10-23)

Commander follow-up reused the existing verify-library-live.js harness, extended
with explicit starting-life, designated command-zone names and opening-hand
exclusion assertions. Run with --commander --partners --ordering passed at table
4841eca4-530f-4f18-9eea-f02e8fa75cfa. Four 100-card Thrasios/Tymna/98 Wastes decks
entered a Commander lobby; every seat had 40 life, two designated commanders in
command and 91 library cards after drawing seven. Free-first-mulligan rounds,
private bottom choice, refresh, card details without submission, two-card UI
reordering/confirmation, post-keep hand/library counts and first-turn draw passed.
Inspected two-card ordering desktop and 667x375 landscape captures. Evidence:
client library-commander-opening-current.log. This honour-system fixture uses the
existing catalog bridge; it does not claim automated partner abilities, all deck
legality cases, or direct observation of hidden final library order. Earlier
engine order tests supply separate evidence for that last contract. Only the
harness changed, so the previous 1,808-test npm verification remains the client
production gate. No card definitions added and no commit made.

Table 24342af1-7272-4f0c-886f-f6d02f370632 passed with four real browser seats.
Two seats mulliganed and two kept; the first multiplayer mulligan retained seven
cards without a bottom choice. One seat mulliganed again and bottomed exactly one
card through the UI after refreshing. Other seats received no private choice
options; kept hands retained their object identities. Final hand counts were
six/seven/seven/seven. Inspected the bottom-after-refresh capture.
Harness: client e2e/verify-library-four-seat-mulligans-live.js. This uses casual
60-Plains decks and does not prove Commander deck validation or commander setup.
An extended run adding no-early-redraw and first-turn-draw assertions initially
hit the local auth rate limit before creating a table; those added assertions
were not covered by the first passing run. After the one-minute window reset,
extended table a7fac584-916b-4862-8999-716491e9662f passed every assertion,
including unchanged hand identities until all initial declarations and the
starting player drawing on turn one. Evidence: client
library-four-mulligans-extended-live.log; inspected first-draw capture.
Production code is unchanged; the last npm gate remains the 1,808-test passing
library-mulligan-shortcuts-verify.log. No commit made.

### Search triggers across pending choices

**Resolved regression: retroactive search trigger (2026-10-23).** Pending choices
now retain detected abilities, controllers and source objects in DeferredTriggers,
instead of rediscovering abilities from deferred events against the later board.
Detection and APNAP stack placement have separate responsibilities. Legacy raw
event continuations remain readable; projected choices strip both representations.
The regression below now passes, together with cases where the original source
leaves before stack placement, with and without another serialized choice.
Rules: 1,531 passed; API: 804 passed (library-search-trigger-detection-*.log).
Follow-up resolution acceptance now passes as well: all four retained-trigger
cases pass priority through Game.Apply, replay serialized events, verify an empty
stack and exactly one life gained, and reject duplicate TriggerFired events.
This covers a source remaining or leaving, with or without a second pending
choice. No production change was needed. Targeted tests: 5 passed; full rules:
1,531 passed (library-search-trigger-resolution.log and
library-search-trigger-resolution-rules.log). Source-leaving resolution remains
engine-verified, not a live-browser scenario or recovered MTGO server contract.

Two-player table 254dfb80-51b9-4f35-adc2-3d5ffb872c3c passed search, refresh,
private pending-data checks, trigger placement, and resolution gaining exactly
one life and drawing one card (client library-search-trigger-detection-live.log).
Inspected on-stack and resolved captures: the ability is labeled Triggered Ability,
then the stack is empty with life 21, hand 7 and library 50.
Fresh npm run verify passed (1,807 tests, changed-file coverage and production
build), recorded in client library-trigger-detection-verify.log. The existing
card-modal stylesheet budget warning remains (18.05 kB against 16 kB).
This live fixture uses an already-present source; the newly found source case
is engine-tested. It is not Commander deck-legality acceptance.
Detection at choice/settle boundaries does not yet prove event-time correctness
through every uninterrupted effect sequence. No MTGO server-method parity or
performance superiority is claimed. No commit made.

Historical reproduction: A normal Game.Apply
priority/choice scenario searches an enchantment with a Searches trigger directly
onto the battlefield. That new permanent incorrectly triggers from the earlier
LibrarySearched event, before it existed on the battlefield. The compiled
regression in LibrarySearchTriggerPauseTests fails in
`library-search-trigger-timing-red.log` (one failure, the two prior retention cases
pass). Earlier full-suite green counts predate this additional case.

Cause: deferred raw events are eventually passed to TriggerSystem.Collect with
the final state. FromObjects enumerates that final state's abilities. The next
fix needs event-time trigger detection retained separately from later APNAP stack
placement, including source/ability/controller information when a source later
leaves or changes. Merely filtering new source IDs would not cover those related
requirements. The fix and current evidence are recorded above. The live Archivist
scenario still demonstrates a source
already present before the opponent searched, not this newly discovered case.

Stack-label correction: ObjectView now projects StackKind from stack metadata
(Triggering/AbilityIndex), including face-down projection. The client transports
the category and maps it directly instead of inferring from source card types;
the controller caption is "Controlled by". The real search-trigger regression
asserts the projected TriggeredAbility kind, and client transport/adapter tests
cover typed abilities with creature source characteristics. Missing legacy kind
data defaults to Spell; it is not reconstructed from a type-line heuristic.
Rules 1,528 and API 804 pass (library-stack-kind-*.log). `npm run verify` passes
1,807 tests, coverage and production build (`library-stack-kind-verify.log`);
existing card-modal style budget warning remains.
Two-player live table `07db17bd-a11c-4a3b-b0d4-db00e7dd0a64` passes the prior
Archivist search scenario plus explicit Triggered Ability / Controlled by DOM
assertions (`library-stack-kind-live.log`). Inspected the corrected on-stack
capture. This resolves the labeling defect recorded below. No commit made.

Live follow-up passed using existing Archivist of Oghma, Plains and Evolving Wilds
definitions, with normal land plays, mana activation and casting. Opponent search
survived browser refresh; the trigger appeared on the stack before any gain/draw;
resolving it gained exactly one life and drew exactly one card, with both views
agreeing. First table `3288f936-e559-4e0b-97ca-98b83345d2ce` passed; second table
`d6a08bd4-b787-436b-810b-9744125d7d05` passed with additional shared privacy checks
for ZoneMoveSnapshot and DeferredTriggerEvents. Evidence: client
`library-search-trigger-live.log`, `library-search-trigger-privacy-live.log`,
`verify-library-search-trigger-live.js`. Inspected on-stack and resolved captures.
The on-stack capture exposes a separate UI defect: this triggered ability is
labeled "Spell". That presentation defect remains open.

Fresh `npm run verify` passed: 1,805 tests, coverage and production build
(`library-search-trigger-verify.log`), with the existing card-modal style budget
warning. The live decks are casual fixtures, not Commander-legal decks. No card
definitions changed and no commit was made. The engine history below records the
reproduction and fix; its former live-unverified status is superseded here.

Game.Apply regression reproduced a lost Searches trigger: normal priority passes
resolve a search, LibrarySearched occurs before the search choice, and answering
the choice previously produced no TriggerFired. Evidence:
`library-search-trigger-pause-red.log` (compiled behavioral failure). Game.Settle
now retains operation events in private ChoiceContext.DeferredTriggerEvents while
a choice is pending; Game.Apply feeds them back when the next command settles.
Choice control events are excluded to avoid recursively retaining continuations.
ChoiceSecrets removes the retained events from every player view.

Tests exercise event-log replay through the search choice and an additional May
prompt, no premature firing, exactly one firing after resolution and no second
firing on a later settle. Full rules: 1,528 pass; API: 804 pass
(`library-search-trigger-pause-rules.log`, `library-search-trigger-pause-api.log`).
This fixes event loss; it does not prove event-time source/condition evaluation
for every trigger or complete simultaneous trigger semantics. Exact live behavior
is still unverified. The existing Archivist of Oghma definition has a Searches
trigger and can supply a live acceptance scenario without adding card definitions.
No client code changed; npm verification was not rerun. No commit made.

### Search ownership audit, 2026-10-22

**Simultaneous search movement regression and resolution.**
Search integration now passes the four reproduced cases. One saved evaluation
board is owned by the search continuation and shared across every participant's
found cards. Relocate delegates batch members through IZoneSystem.MoveInBatch,
which reuses MoveAll and applies its returned events to the live state. The shared
interpreter proposal path retains the final replacement question's snapshot,
removes superseded internal questions and resumes through ZoneMoveSnapshot.
Persisted search plans and the new board key are removed on completion and
withheld from client choices. Full rules: 1,524 pass in
`library-search-batch-integration-rules.log`. This resolves the entering-source
replacement defect below, including serialized pauses. It does not establish
complete simultaneous trigger semantics or shared-library contention behavior.
API compatibility: 804 pass in `library-search-batch-integration-api.log`.
Four-player Field of Ruin search passed at table
`d7f2cd3b-5f06-496b-bf1e-326f580ca095`, with private inspection, APNAP choices,
refresh and one land found per seat (`library-search-batch-integration-live.log`).
Inspected `library-four-search-completed.png`. This live scenario checks existing
multiplayer behavior, not the exact generic-fixture replacement interaction or
Commander deck legality. Client source was unchanged; npm verification was not
rerun for this backend slice. No commit made.
The following failing-run descriptions are historical reproduction evidence.

Live-state accumulation verification: two library cards searched to hand are each
replaced by gaining two life. Tests cover a single automatic replacement and two
competing replacements, with separate serialized choice resumptions for both
cards. Both life gains accumulate (+4 total), exactly two gain events occur, both
cards stay in the library, no hand arrival occurs and inspection scopes expire.
All 1,526 rules tests pass in `library-search-batch-effects-rules.log`. These checks
verify that frozen evaluation does not roll back these live side effects; they do
not establish every replacement-effect dependency or interrupted trigger window.
No production code changed in this verification follow-up; API/client gates were
not rerun. No commit made.

LibrarySearchSimultaneousTests uses two generic creature fixtures in the library,
one with an enters-tapped replacement for creatures. Both are chosen by one search
to enter the battlefield. The second incorrectly enters tapped because the first
arrival becomes an active replacement source before the second is processed.
The identical fixture passed through the existing IZoneSystem.MoveAll operation
leaves the second untapped. The focused run has one pass and one failure in
`library-search-simultaneous-red.log`; earlier green suite totals predate this test.
No named card definition was added. This is an independently reproduced engine
timing defect, not evidence about MTGO's unavailable server implementation.

Code-path diagnosis: search runtime Move calls Relocate separately for each found
card, so each replacement reads the evolving board. ZoneSystem.MoveAll instead
offers moves against the pre-batch board. A direct substitution is insufficient:
MoveAll carries DeferredZoneMoves across interruption, while interpreter
ProposeThrough currently preserves only Event/Extra when reissuing a replacement
question, and search tracks arrived objects per participant. The fix must preserve
deferred work, partial arrivals and original batch semantics through serialized
replacement choices; it must not introduce a separate search-only movement engine.
This acceptance test is intentionally still failing pending that implementation.

Pause/replay follow-up: the same regression now introduces two pre-existing
replacement effects and answers every replacement-order question through
ChoiceSystem, serializing and replaying the answer events. The shared MoveAll path
also incorrectly taps the second arrival after a pause. Results in
`library-search-simultaneous-paused-red.log`: one passing uninterrupted batch case,
three failing cases (uninterrupted search, paused search, paused batch). This
changes the implementation prerequisite: simply switching search to MoveAll is
not sufficient. ResumeMoveBatch evaluates remaining proposals against tape.State,
and replacement resumption also receives the evolving state, so newly arrived
sources can participate after the interruption. The original replacement-evaluation
board must survive the pause separately from the current event-application state
and ID/choice counters. Both paths need the same shared fix. No production fix or
passing full-suite result is claimed for this checkpoint; the regression remains
enabled and failing. No named cards or client source were changed.

Shared batch fix checkpoint: ZoneMoveSnapshot now preserves the original
replacement-evaluation board on interrupted MoveAll choices. ResumeMoveBatch and
direct replacement resumption use that board with live identity/choice counters;
returned events apply to the current state so completed moves are not rolled back.
ChoiceSecrets strips the snapshot from both authorized and other-player pending
views. Snapshot dictionaries use a private entry-array format so zone, commander
and tuple keys survive serialization without changing existing event serialization.
Compatibility tests exercise commander cast/damage keys and turn-history tuple
keys; paused batch tests replay answer events and assert both views hide snapshots.
The snapshot retains the full evaluation state, so interrupted batch log/payload
size needs measurement before claiming a performance improvement.

Current results: 1,522 rules pass, with only the two search timing regressions
still failing; API 804 pass (`library-zone-batch-snapshot-rules.log` and
`library-zone-batch-snapshot-api.log`). Direct batch movement now passes both
uninterrupted and paused cases. Search still uses individual Relocate calls and
must be connected to the shared mechanism; no search timing fix, live verification
or full-suite success is claimed yet. No commit made.

Coordinator extraction follow-up: LibrarySearchCoordinator now owns participant
planning, inspection/choice sequencing, frozen destination execution, finish order
and cleanup. ILibrarySearchRuntime supplies only shared resolution operations;
the interpreter's SearchRuntime adapter delegates to Ask, Relocate, ProposeThrough,
disclosure and identity helpers. EffectInterpreter.Search now dispatches the
coordinator and retains optional-effect eligibility. No proposal counter, zone
movement algorithm or shuffle algorithm was copied into the coordinator.
The coordinator still consumes the existing EffectRun resolution cursor; this
is not a claim of complete independence from the interpreter's state model.
Full rules 1,519 and API 804 pass in library-search-coordinator-*.log. This
structural change does not resolve the separately listed shared-library contention
or simultaneous movement/trigger requirements, and does not prove MTGO server
equivalence. The following earlier extraction notes are historical checkpoints.
Four-player live verification after this extraction passed at table
`e953da65-a485-4d17-b511-61e68b28f102` (client
`library-search-coordinator-live.log`): every seat privately inspected, refreshed,
chose in APNAP order and found a basic land. Inspected the landscape choice
capture `library-four-search-choice-landscape.png`. Existing casual decks are
used; Commander deck legality is not asserted. No client source changed, so the
last npm gate remains library-reselect-verify.log. No commit made.

First extraction completed: LibrarySearchContinuation now owns participant/move
plan serialization, private plan-key recognition, per-player memory prefixes and
plan cleanup. Searching consumes that class, and ChoiceSecrets uses its privacy
predicate rather than an interpreter-owned search constant. Persisted key names
and JSON shapes are preserved. A literal pre-extraction payload test checks
participant/owner IDs, destinations, single/multiplayer memory names and cleanup
that preserves another operation's plans. Existing serialized multiplayer,
replacement, identity and privacy regressions remain on the same execution path.
Full rules: 1,519; API: 804 passed (library-search-continuation-*.log). An initial
test compile flagged a redundant import; it was removed before the successful
run. The ownership table below describes the audit baseline; continuation-format
ownership is now resolved, while orchestration extraction remains unfinished.
Four-player live follow-up passed at table
`7dbc0924-8615-4c01-9a2d-c883c6fcf63d`: private inspection, APNAP choices across
browser refresh and one basic land found per player. Evidence:
client `library-search-continuation-live.log`; inspected
`library-four-search-completed.png`. This uses the existing casual test decks,
not a claim of Commander deck-legality coverage. Client code was unchanged;
the last client verification remains `library-reselect-verify.log` (1,805 tests
and production build). No commit made.

Inspected the current search interpreter, LibrarySearchPlanner, LibrarySearchPolicy,
LibraryOrder, ChoiceSecrets, Relocate and ProposeThrough. The current architecture
does not yet meet the requested complete search-coordinator separation:

| Responsibility | Current owner | Required disposition |
| --- | --- | --- |
| Permitted zones, inspected objects, candidates, minimum/maximum | LibrarySearchPlanner and LibrarySearchPolicy | Retain this single planning path. |
| Destination calculation before movement | LibrarySearchPlanner.Moves | Retain; recomputing per moved card previously caused a reproduced defect. |
| APNAP participants and inspection-before-selection | EffectInterpreter.Searching | Move orchestration to a library search coordinator while retaining shared turn ordering. |
| Serialized participant/destination plans | Searching, inline JSON in ChoiceContext.Extra | Give the search continuation representation one owner. Preserve existing persisted keys or explicitly migrate them. |
| Per-participant selected/arrived/top/bottom progress | Found and FinishSearch via generic remembered sets | Coordinator-owned search progress, with a narrow bridge to existing interpreter continuation. |
| Prompt creation and replacement proposal ordinals | Shared Ask / ProposeThrough | Reuse. Do not duplicate these in the library coordinator. |
| Physical movement and shuffle | Existing IZoneSystem via Relocate / ProposeThrough | Reuse. Do not introduce a second zone engine. |
| Top/bottom permutation | LibraryOrder.Arrange | Already shared by search and scry/surveil; retain. |
| Active inspection / revealed-identity refresh | Shared interpreter library helpers | Preserve operation scope, authorization and replay. A class extraction must not broaden historical snapshots into active access. |
| Private continuation filtering | ChoiceSecrets.Withheld references interpreter-owned SearchPlanPrefix | Move search-format ownership with continuation, updating both producer and filter together. |

The important extraction constraint is observable in ProposeThrough: replay counts
every proposal by the current effect path and skips proposals already applied.
Relocate deliberately consumes an ordinal even for an absent old object when
resuming replacement order. A coordinator that calls IZoneSystem directly or
skips completed participants can therefore duplicate or misalign replacements.
The bridge must retain this shared behavior; copying its code would violate the
user's no-duplicate-implementation requirement.

Required acceptance for the extraction: serialized four-player APNAP search;
no movement before every choice; frozen destinations across replacement pauses;
one move/shuffle per instruction; private plans absent from every projected view;
revealed-top identity refresh; final inspection expiry; and replay of the prior
continuation key format. Existing MultiplayerLibrarySearchTests cover several of
these jointly, but do not cover shared-library contention or prove simultaneous
movement/trigger semantics. Those remain separate behavior gaps, not defects
that a class rename can fix. No extraction or full SOLID completion is claimed by
this audit, and no new runtime test result is claimed.

Interrupted inspection regression: a surveil-2 reorder reidentified a revealed
card before competing graveyard replacements paused the operation. Both cards
remained in the library, but only one appeared in the authorized inspection.
`library-inspection-replacement-red.log` reproduces that mismatch after serialized
replay. Shared identity remapping now updates the current operation's inspection
scope as well as its saved choices, using the existing inspection event. The
regression verifies two inspected cards during the replacement prompt, no private
inspection for the opponent, and complete expiry after the selected replacement
leaves both cards in the library. Full rules: 1,518 passed
(`library-inspection-replacement-rules.log`). The exact interaction remains
live-unverified because the existing corpus cannot supply surveil 2.
API compatibility: 804 passed in `library-inspection-replacement-api.log`.

Conditional draw-limit audit: player static gathering silently skipped every
`While` condition despite the existing IQuery.Holds implementation. A regression
reproduced a missing zero-draw limit while the source's controller controlled an
artifact (`library-conditional-limits-red.log`). Player conditions now use the
same guarded query entry as static filters; the recursion guard is shared rather
than duplicated. The test verifies enabling/disabling with current control,
opponent isolation and actual blocked/allowed draw movement. Full rules: 1,514;
API: 804 (`library-conditional-limits-rules.log`, `library-conditional-limits-api.log`).
This retains the layer system's existing limitation: query-guarded characteristic
reads exclude battlefield statics, so complete dependency handling is not proven.
Conditional object-scoped statics and drawing as a cost remain outside this fix;
the current cost vocabulary has no draw-cost node. No named card was added, and
this conditional interaction is not yet live-browser verified.

Conditional privacy follow-up: 1,517 rules tests pass in
`library-conditional-privacy-rules.log`. Added projection regressions for public
and private top-card permission, opponent isolation, condition becoming false,
face-down source and phased-out source. Search-plan regression verifies that a
conditional prohibition removes only the affected player's library zone while
retaining a permitted graveyard search and the opponent's library access. These
are engine/projection checks, not live-client validation of conditional cards.

Surveil identity regression: `library-surveil-revealed-red.log` reproduced a
selected publicly revealed top card never arriving in the graveyard after the
remaining cards were reordered. Reorder reidentified the selected card; the
surveil move still used its old ID. The existing search-local remapping algorithm
is now shared in `EffectInterpreter.LibraryIdentity.cs` and updates only the
current operation's saved references. Surveil rereads its selected list after
reorder. The regression checks the selected definition, remaining count and
serialized replay. All 1,513 rules tests pass in
`library-surveil-revealed-rules.log`. This exact revealed-top surveil interaction
has not yet been exercised in live browsers; four-player all-player scry/surveil
live coverage also remains open.
API compatibility passed: 804 tests in `library-surveil-revealed-api.log`.

Active scry/surveil inspection now uses the same interpreter scope helpers as
search (`EffectInterpreter.LibraryInspection.cs`). All affected players receive
only their inspected cards while APNAP choices and top ordering are pending;
completion closes each scope. Four-player serialized tests cover private access
and final cleanup for scry/surveil, keeping all or moving selected cards.
Rules: 1,513 passed; API: 804 passed (`library-arrangement-inspection-*.log`).
Client wording is operation-neutral; npm run verify passes 1,805 tests and build
in `library-arrangement-inspection-verify.log`. Exact revealed-top surveil-2 live
validation cannot be produced by the present card corpus: its only implemented
surveil effects are surveil 1. No new card definitions were introduced to hide
that limitation. Initial live scry attempts lost WebSockets and then failed to
connect; the Angular server was confirmed stopped and is being restored.
After restoration, live table `8f35ef9a-1024-467c-bbf8-147fc2b3ffa3` passed
private scry-2 inspection, refreshed top-order selection, completion expiry and
a later scry confirming the chosen order after one normal draw. Evidence:
`library-arrangement-inspection-live-restored.log`; inspected
`library-scry-v2-top-order.png`. This validates two-player scry, not the still-open
revealed-top surveil-2 or four-player simultaneous-operation cases.

Live surveil-1 inspection is now verified in two paths. At
`562914a4-2303-4953-90f1-35215ef09006`, the authorized player opened the inspected
card detail, then answered via a second authenticated connection; authoritative
completion removed both active inspection and the open body-mounted modal.
At `7eefff7a-9668-4c91-8f5f-930df54c884e`, the normal phone choice button completed
surveil. Both verify opponent privacy, refresh, library/graveyard counts and
expiry. Logs: `library-surveil-inspection-live-retry.log` (run with
`--detail-expiry`) and `library-surveil-inspection-selection.log`. Inspected
`library-surveil-active-detail.png` and `library-surveil-completed-landscape.png`.
The first attempt failed because the harness searched for the modal under its
component instead of its actual document-body portal; the selector was corrected.
No production changes or new card definitions were needed for this verification.

Current multiplayer delivery checkpoint: independent account delivery fixes a
measured 14.6-second send delaying subsequent seats. Sequential private payload
preparation remains; sends run independently with per-recipient view-before-choice
ordering. API regression suite: 804 passed. Four live browsers passed inspection
privacy, APNAP selection, refresh and expiry at
`d0d49690-7619-447b-956e-7be3a0860639` with the original timeout. Evidence is in
`library-independent-delivery-api.log` and client
`e2e/screenshots/library-independent-delivery-live.log`.

Local comprehensive rules 401.5, 401.6 and 701.20a/d/e require more than a current
top-card snapshot. Inspection found:

- ViewProjector.Player directly calls Top whenever the current permission is true.
  It does not itself delay a newly exposed card during announcement/payment.
- ZoneReordered.Apply only replaces the ordered ID list. It does not itself end
  active disclosures or create replacement object identities for reordered revealed
  cards.
- The prior test tagged 401.6 moves the old top into the graveyard and compares it
  with a different card. Its documentation/tag have been corrected: that is not a
  test of the same physical card hidden then revealed again.

Implement and test authoritative disclosure lifetime separately from historical
LastLook/LastReveal display snapshots. Reuse existing card identity, movement and
event replay contracts. Do not reset every shuffled card's identity: distinguish
the rules' revealed-card case from ordinary hidden library ordering. Cover
permission removal/reinstatement, top changes, shuffle/reorder, pending costs,
replacement interruptions and serialized replay; then verify both viewers live.
This is a required behavior gap, not a claim about MTGO's unrecovered server code.

### Reproduction and casting-path correction

HiddenInformationTests.RevealIdentity now runs reveal -> effect removal -> reveal
through Game.Settle. The focused run fails: both public observations have object
ID #2. Evidence: library-reveal-identity-red.log. This is a compiled behavioral
failure, not just a missing-test inference. The regression remains failing pending
authoritative disclosure-lifetime implementation.

Ordinary CastingSystem cast/activation payment paths construct and return a whole
event batch before API projection. Therefore ViewProjector's direct Top call alone
does not prove premature visibility for those atomic paths. Investigate interrupted
cost/replacement paths before adding a blanket pending-choice visibility block;
such a block could incorrectly hide an unchanged, legally visible top card.

The reproduced settle-boundary identity regression now passes. LibraryRevealCoordinator
tracks public continuous top disclosures in replayable state. A previously revealed
card that becomes hidden and is subsequently revealed gets a new object ID while
retaining CardInstanceId, deck selection, commander designation and library position.
Dedicated events avoid zone-movement triggers. The test serializes/replays those
events and checks reconciliation is idempotent. Full rules: 1,500 passed; API: 802
passed (library-reveal-identity-rules.log and library-reveal-identity-api.log).

This is partial implementation: reconciliation currently runs at Game.Settle.
Transitions inside one resolving effect, reordered revealed cards even when the
same card remains top, temporary look/reveal lifetime and interrupted-cost visibility
remain unfinished. Old logs without tracking cannot reconstruct prior hidden
intervals from their final state alone. Live acceptance of this rule is unverified.

Shuffle/reorder follow-up: two regression cases failed before the change (revealed
top with changed order; shuffle retaining the original order). ZoneReordered now
ends tracked continuous-top disclosures and reidentifies those cards through the
shared helper, as part of deterministic event application. Hidden library cards
retain their identities. Four cases cover public/hidden and changed/unchanged
order, serialized replay, no extra reidentification on settle, and no change for
a non-shuffle event with identical order. Rules 1,504 and API 802 passed
(library-reveal-reorder-rules.log, library-reveal-reorder-api.log).
Temporary look/reveal cards are not yet tracked by this mechanism. Continuous
permission changes within one effect and live acceptance also remain open.

Search interaction regression: revealed-card identity reset initially invalidated
search placement/result memory. A search-local remap now follows reidentification
from each completed reorder; unrelated remembered effects do not get aliases.
Three tests verify top/bottom placement, shuffle/no-shuffle and serialized later
prompt continuation. Full rules 1,507 and API 802 passed. Evidence:
library-search-reveal-identity-red.log and library-search-reveal-identity-rules.log.
The combined scenario is not yet exercised in a live browser.

Live follow-up supersedes that last limitation for the two-player combined path:
verify-library-revealed-search-live.js passed at table
45a4506a-0aa3-4ad6-8edd-f5988ff9a801 using existing Courser of Kruphix and Worldly
Tutor. Both player views report the new top identity after search/shuffle; the
caster's next draw is the expected creature. The after-search capture was inspected.
This does not establish temporary reveal lifetime or every within-effect transition.

## Four-player live scry ordering (2026-10-23)

Extended the existing scry browser harness with --four-seats, proportional legal-turn
budgets and privacy assertions for all three observers. Table
`ee962c32-1b92-423f-a51a-c2297236c6c8` passed: legal Castle Vantress setup, private
scry and top-order choices, reload preserving the pending choice, browser reorder
and confirmation, inspection expiry, and a subsequent scry proving the first card
was drawn and the second retained its identity at the top. Direct choice pushes
also passed the existing authorized-projection checks. Evidence: client
`library-scry-four-live.log`, inspected `e2e/screenshots/library-scry-four-top-order.png`
and `library-scry-four-top-order-landscape.png` (667x375, no document overflow).

This covers one player's scry with three live observers. It does not establish
simultaneous independent scry prompts, four-player surveil, physical-device input,
or new production validation. Only the acceptance harness changed; no full npm
verify rerun and no commit. The existing API on port 5001 served the test; an
additional host launch failed because that port was already bound and was not
used as acceptance evidence.

## Four-player live surveil and detail expiry (2026-10-23)

The existing surveil harness now accepts --four-seats and verifies each observer's
empty inspection/choice projection and absent active-inspection UI. On table
`1de2cdfc-18a0-4bb1-a12f-3ebc41e4a779`, legal Desert/Arid Archway plays reached
surveil, reload preserved the private choice, the inspection menu opened card
details, and an authoritative answer expired both inspection and the open detail.
All three observers received exactly the same public graveyard list and library
count as the owner. Direct choice-push privacy checks passed.

Evidence: client `library-surveil-four-live.log`; captures under
`e2e/screenshots/library-surveil-four-*`. Active-detail, completed-landscape and
opponent-2 captures were inspected. Landscape document overflow was checked at
667x375. This is browser emulation and one acting player with three observers,
not simultaneous independent surveil effects or physical-device acceptance.
Only the harness and this ledger changed; the full production verify baseline
was not rerun. Nothing committed.

## Live shared search after ownership refactor (2026-10-23)

Table `375a11c7-5923-4e7d-a011-d8f58e8842ca` passed the existing four-browser
Field of Ruin search harness against the refactored backend. All players retained
only their own active library inspection, chose in APNAP order with explicit
confirmation and a reload before each decision, then each found one Mountain.
All library counts, battlefield arrivals, choice clearance and inspection expiry
checks passed, including direct choice-push privacy. The process exited zero after
cleanup. Client log: `library-search-scope-live.log`.

Inspected the refreshed `e2e/screenshots/library-four-search-choice-landscape.png`
and `library-four-search-completed.png` captures. The 667x375 overflow assertion
passed. This verifies one shared search effect across four live players after the
search/arrangement ownership changes; it is not evidence for every replacement,
interrupted payment, Commander deck or physical device. No code changed in this
verification pass and no commit.

## Final library payment visibility through normal commands (2026-10-23)

Final_library_payment_resumes_through_normal_commands_and_replay now checks both
player projections at every serialized replacement pause and after completion.
The original revealed top remains visible while still on top; a newly exposed card
is withheld until activation payment finishes. Completion projects the actual final
top when public reveal applies, otherwise no revealed identity. Two new mill cases
leave one card in the library, verifying post-payment disclosure of a nonempty top;
existing cases cover empty library, library exile, and sacrificed activation source.
All 1,809 rules tests pass in library-final-payment-visibility-rules.log.
This closes the tested normal final-payment engine/projection timing boundary.
It is not browser evidence, casting coverage, or acceptance of arbitrary early pauses.
No production code changes or commit.

## Four-seat API top visibility during final library payment (2026-10-23)

Final_library_payment_hides_new_top_until_completion_across_four_reloaded_sessions
uses authenticated GameHub.Command with a normal final MillCost activation. It reloads
GameSessionService before each of two replacement decisions and after completion.
All four player projections show the original revealed top at the first pause, no
new revealed top at the second pause, and the surviving third card after completion.
Serialized intermediate views omit second/third card names and the payment journal.
Each successful command broadcasts to all four seats; exactly three saves and one
stack entry result. Full API suite: 838 passed (library-top-payment-api.log).
This is in-process hub/session persistence evidence, not a four-browser capture.
No production changes or commit; frontend verification baseline unchanged.

## Four-browser public-top payment acceptance (2026-10-23)

QA table c2944cde-8abe-4e35-bad9-0a8c233036d4 completed normal activation payment,
acting-browser refresh, fourteen replacement decisions, four-seat broadcasts and
final reload. --public-top asserts every seat's live projection retains the original
top until it moves, suppresses the next top during payment, and restores disclosure
on completion. Four fixture seats supply eight competing replacement sources, so
the harness decision bound now scales with seat count. The first browser run hit
the old twelve-decision limit; the corrected run passed with exit zero in
library-public-top-live-verified.log. QA build passed in
library-public-top-qa-build-final.log (first build encountered an old QA file lock).
Inspected library-public-top-pause-8.png and library-replacement-live-four-seat-0.png:
the replacement panel is present while paused; final library shows Canopy Scout,
55 cards and a single activation on stack. The panel covers part of the self rail
while paused, so live projection assertions are stronger evidence of intermediate
identity suppression than that screenshot. This uses synthetic art-free fixtures,
not production card additions. No client production code or commit changed.

## Accepted action followed by failed delivery (2026-10-23)

Manual draw/shuffle API tests now inject IOException on the acting user's ViewUpdated
after the session saves. The other seat still receives its delivery attempt. Retrying
both immediately and after GameSessionService restart is refused, with one save only,
unchanged log within each session, identical player views and retained RNG after reload.
This adds accepted-before-delivery-failure evidence, distinct from failed-send tests.
Full API: 840 passed in library-lost-delivery-api-verified.log. Earlier attempts failed
because raw GameState is not event-log serializable and a serialized set may reorder
across reload; final assertions avoid those invalid assumptions. No production fix
was needed. This does not establish browser UI recovery after a lost response, or the
same scenario for every library action. No commit.

## Real client transport retry defect reproduced (2026-10-23)

LibraryActionsService preserves request IDs on rejection, but SignalRService.invoke
previously caught transport failures and resolved. The real wrapper therefore caused
the service to discard an uncertain request ID. Two integration regressions failed
in client library-transport-retry-red.log: failure/retry identity and unavailable
transport/missing snapshot rejection. The existing mocked-service test did not cover
that boundary. ManualLibrary commands now propagate transport failures and reject
unsent actions when disconnected or missing a snapshot. Other command behavior stays
unchanged. Full client verification is recorded separately when complete.
Browser response-loss acceptance and request persistence across full page reload
remain unverified/incomplete; do not treat this as complete retry acceptance.
Client verification completed: npm run verify passed with 1,851 tests, coverage,
format/lint checks and production build in library-transport-retry-verify-final.log.
The initial full run caught the missed disconnected branch; it was corrected before
this passing run. Existing card-modal SCSS budget warning remains 18.05 KB / 16 KB.
The transport bug is regression-tested; live browser response-loss behavior remains
unverified. No commit.

## Four-browser accepted draw with lost response (2026-10-23)

Table 70b0593f-d4c2-45e7-a458-a6a25b3ea29f passed --draw --lost-response --four
(library-lost-response-live.log, exit zero). A browser WebSocket wrapper intercepted
the first draw Command completion, suppressed it and closed the socket after server
broadcasts. Normal reconnect rejoined the table; the UI finished replacement choice,
then retried Draw with the exact same request ID. The duplicate refusal left the
entire owner projection unchanged, hand 6/library 56; all four seats synchronized,
observer card privacy checks and final reload passed. Inspected
library-lost-response-retry.png. Its overlapping generic transport warning, library
warning and duplicate-action toast expose a feedback cleanup item; do not describe
the error UI as finished. This proves same-page uncertain-send retry behavior with
the corrected transport. Full-reload retry-ID persistence remains incomplete.
Only QA harness/docs changed here. Client baseline remains npm run verify 1,851 tests
plus build from library-transport-retry-verify-final.log. No commit.

## Library uncertainty warning ownership (2026-10-23)

SignalRService no longer adds its generic transport warning for ManualLibrary calls;
those errors still reject to the existing library UI handler. Missing-snapshot errors
also delegate warning ownership. Two strengthened integration assertions reproduced
duplicate messages before this change (client library-warning-owner-red.log).
Full npm run verify passed with 1,851 tests, coverage and production build in
library-warning-owner-verify.log; existing card-modal style budget warning remains.
Four-browser table d653cbcb-5958-4407-8666-5aadd3e987b1 passes injected response loss,
exactly one library uncertainty warning with no generic duplicate, reconnect, same-ID
retry, one draw, all-seat synchronization and reload (library-warning-owner-live.log).
Inspected library-lost-response-warning.png. It exposes an additional presentation
issue: the warning is dimmed behind the still-open native dialog backdrop. Duplicate
message ownership is fixed, but error visibility in the dialog remains unfinished.
Full-reload request persistence is also still open. No commit.

## Inline library dialog uncertainty status (2026-10-23)

ZoneViewerComponent now displays uncertain-action feedback inside the native dialog
below its header, with role=status, instead of a toast behind the backdrop. It clears
on retry, zone change and close. The strengthened DOM regression failed before this
change (client library-dialog-status-red.log). npm run verify passes 1,851 tests,
coverage and production build in library-dialog-status-verify.log; existing card-modal
SCSS budget warning unchanged. Four-browser table
4673c71c-204d-437f-b8bd-89f9713aaff4 passes response loss, reconnect, inline status,
same-ID retry, unchanged result and final reload (library-dialog-status-live.log).
Inspected library-lost-response-warning.png plus -portrait.png and -landscape.png:
message is readable in the dialog at desktop, 375x667 and 667x375. Script verifies
message bounds and no document overflow; no duplicate library/generic toast appears.
This supersedes the dimmed-warning issue noted above. Full-reload retry identity
persistence remains incomplete. No commit.

## Reload-safe library request journal (2026-10-23)

LibraryRequestJournal owns sessionStorage request IDs keyed by table/player scope
and exact action. LibraryActionsService owns the busy gate/submission and delegates
begin/complete; it no longer holds a separate retry map. SignalRService supplies a
scope only when the latest view belongs to the connected table. Storage is written
before sending; storage failure prevents the command and releases busy state.
Service recreation, scope separation, successful cleanup and storage-failure tests
pass. Two initial regressions failed in library-request-reload-red.log.
Client npm run verify passes 1,854 tests, coverage and production build in
library-request-journal-verify.log; existing card-modal size warning unchanged.
Four-browser table f000c917-d8b9-43b6-acfc-24d0db84d1dc passes --draw
--reload-response --four (library-request-journal-live.log): accepted draw response
is dropped, connection rejoins, replacement completes, page reloads, and UI retry
uses the same stored ID. The whole owner projection is unchanged after retry; all
seats synchronize and drawn-card privacy holds. Inspected library-lost-response-retry.png
shows hand 6/library 56 and the already-applied notice. Earlier notes calling full
reload persistence absent are superseded for this shared implementation and verified
draw scenario. Other library action variants are not all live-tested after reload.
No commit.

### Shuffle response loss and full reload — 2026-10-23

Four real headless browser seats passed on isolated table
`67d052ed-be58-4d69-8450-7e087788f5de` with
`node tools/TableQa/verify-library-payment.js --shuffle --reload-response --four`.
The browser loses the accepted command completion, reconnects, completes replacement
choices, reloads, and retries through the library UI. Wire request IDs match. The
server records exactly one shuffle before retry and no additional library events
or projected-state changes after retry. All-seat broadcast and final reload checks
pass. Evidence: `library-shuffle-reload-live.log`; retry capture
`tools/TableQa/library-lost-response-retry.png` visually inspected.
Only QA checks and documentation changed in this increment; production client
verification remains the 1,854-test `library-request-journal-verify.log` baseline.

### One all-player scry/surveil instruction in four browsers — 2026-10-23

Added an isolated TableQa libraryOrdering fixture and browser verifier. The synthetic
spell begins on the stack and normal hub passes resolve it; all four decisions use
browser controls. No production card definitions changed. TableQa build passed with
zero warnings/errors (`library-all-ordering-build.log`); regular API was restarted.

Scry table `77b813d9-7fad-40b5-a52f-957b71fd850f` passed in
`library-all-scry-live.log`; surveil table `679d47e6-5839-440a-9e19-1b947f86a033`
passed in `library-all-surveil-live.log`. Every seat reloads before choosing one of
two cards. All views contain only their own inspected library cards; only the active
chooser receives options and choice cards. Counts remain unchanged through the first
three decisions. Final library counts and each player's graveyard oracle-ID multiset
match the expected result, including the resolved spell. All four final projections
remain unchanged across reload. Landscape captures for the fourth seat in each mode
were visually inspected; choice controls are visible at 667x375.

The first scry attempt completed all decisions then failed in the verifier's incorrect
root-level graveyard lookup; corrected to player graveyards and reran on a fresh table.
These runs cover one-selected-card decisions, not the keep-all/top-order branch,
nonzero active-seat APNAP rotation, or exact hidden bottom order in the browser. Those
have rules tests but are not established by this live scenario. No client production
change; npm verification was not rerun for this QA-only increment.

### All-player keep-all ordering and rotated active seat — 2026-10-23

Both keep-all branches passed with active seat 2 and decisions in order 2, 3, 0, 1.
Scry: `04b5ecb5-ae07-4956-85f1-507e4e01e8e1`, `library-all-scry-keep-live.log`.
Surveil: `c41dad95-f84a-44f3-864b-68f55de4da2c`, `library-all-surveil-keep-live.log`.
Every player chose none, reloaded at the resulting top-order prompt, and selected
both cards in reverse order through the UI. An authenticated observation endpoint
confined to TableQa proved all original library orders remained unchanged before
the final player confirmed and all four final orders exactly matched the choices.
No cards entered graveyards except the resolving spell. Final projections survived
all four browser reloads. Desktop numbered-order captures for seat 3 in both modes
were inspected. These runs close the prior keep-all and nonzero active-seat live gaps.

QA build passed with zero warnings/errors. The first scry browser launch preceded
host readiness and exited without reaching a table; after checking host readiness,
the same fixture completed successfully. No production engine/client changes or npm
verify run in this increment. Portrait top-order layout and physical-device behavior
are not established by these desktop captures.

### Independent deferred library movement history — 2026-10-23

Added two LibraryNestedMovementTests cases for a standalone three-card library-to-hand
batch with two competing movement replacements. Every answer rebuilds state from the
serialized accumulated log and uses fresh subsystems. Both selection orders pass.
Each card is offered both replacements, deferred count decreases from two to zero,
the pending proposed card matches the expected next source, and neither viewer's
projection exposes DeferredZoneMoves. Completion has exactly three distinct movement
arrivals, no duplicate source move, an empty library and three cards in hand.

No production change was required: consumed replacement history from one independent
card must not suppress candidates for the next. Full rules suite 1,816 passed
(`library-deferred-move-history-rules.log`). This establishes the standalone batch
case; replacement-created PayCost still needs separate investigation because its
CostContext currently does not carry inherited interpreter replacement history.

### Inherited history survives deferred library batches — 2026-10-23

Extended the three-card batch test with an already-used gain-nine replacement plus
two eligible movement replacements. Both inherited variants failed after reload:
a later card offered three replacements instead of two (`library-inherited-batch-red.log`).
Non-inherited variants already passed.

ChoiceContext now stores inherited replacement sets keyed by deferred source identity.
ZoneSystem records the original inherited set for each remaining card, supplies it
when resuming that card, and remaps remaining keys with resolved identities on later
pauses. ChoiceSystem carries the map with deferred movements through nested choices.
ChoiceSecrets strips the map alongside deferred proposals from every player view.
The map stores inherited exclusions only, not replacements consumed by the preceding
independent card. The default empty map preserves replay of prior logs.

All four cases pass with fresh subsystems and serialized replay at every answer:
each card offers two candidates, the expected source is proposed, hidden deferred
data is absent for both viewers, and exactly three distinct movements complete.
Full rules suite: 1,819 pass (`library-inherited-batch-rules.log`). This does not add
interpreter PayCost continuation support; that separate restriction remains.

### Optional library effect-payment branches — 2026-10-23

Extended LibraryEffectPaymentTests with MayPay(MillCost(2)): replay the initial
may-pay prompt, answer yes, then replay before each of two replacement decisions.
Neither Then nor Else runs while payment is pending; Then occurs once after both
cards move. Existing private-continuation checks apply to both viewers at each pause.
Two additional cases replay and decline a payable mill, or accept an unpayable mill.
Both run Else exactly once and leave the original library card untouched.

Full rules suite: 1,825 passed (`library-optional-effect-payment-rules.log`). No
production change was needed beyond the previously verified shared payment path.
API baseline remains the previous 840-test run; it was not rerun for test-only changes.
Consumed-source and replacement-created paused-payment cases remain unverified.

### Library payment after ability-source removal — 2026-10-23

Added mandatory and optional two-card mill variants to LibraryEffectPaymentTests.
The resolving stack object names a separate battlefield artifact as its source.
After the replacement prompt appears, the fixture moves that artifact to the
graveyard and verifies the original source identity is absent. Every subsequent
answer uses serialized event replay and fresh subsystems. Both library cards move,
Then runs once, and one StackObjectResolved event completes the resolution.
Private continuation data remains absent from both player views.

The initial diagnostic mistakenly removed the resolving stack object itself; that
is a different condition and is not evidence of a payment defect. The corrected
fixture preserves that object and removes only its source. This is subsystem-level
coverage, not proof of a live card interaction that removes its source mid-payment.

Full rules suite: 1,827 passed (library-source-payment-rules.log). No production
changes. API/client checks were not rerun for these test-only changes. Replacement-
created paused payment and later unpaid cost steps remain open.

### Replacement-created paused library payment — 2026-10-23

LibraryNestedMovementTests now replaces a library-to-hand movement with
PayCost(MillCost(2), Then: GainLife(3)). Two separate movement replacements force
a choice for each milled card. Both candidate orderings pass, with serialized
replay and fresh subsystems before every answer. Life remains unchanged until
both choices finish; exactly two original cards move to the graveyard, the hand
stays empty, and the follow-up life change occurs exactly once. No stack-resolution
event is emitted for this off-stack replacement. Both player projections omit
payment continuation, remaining payment and deferred movement data.

Full rules suite: 1,829 passed (library-replacement-paused-payment-rules.log).
This required test coverage only, no production changes. API/client verification
was not rerun. The fixture keeps the replacement source present; its removal
mid-payment is a separate unverified case. Later unpaid cost steps remain open.

### Later library cost steps — 2026-10-23

PayCost and MayPay now retain a paused first mill and resume remaining costs through
CostPayment.Resume after the current batch completes. Two separate mills and three
separate mills with source removal pass serialized replay and privacy checks, with
one follow-up and one stack completion. The original failure is recorded in
library-later-effect-cost-red.log. Rules: 1,833 passed; API: 840 passed
(library-later-effect-cost-rules.log and library-later-effect-cost-api.log).

Known incomplete behavior: a later unpayable mill refuses the answer without
publishing its partial events, but can leave a prompt permanently pending when no
alternative succeeds. The diagnostic test documents this boundary, not acceptable
final recovery. Effect-payment journaling/recovery must be completed before commit
or completion acceptance. See LIBRARY_PAYMENT_RULES_AUDIT.md. No client changes,
live captures or npm verify in this backend turn. No commit.

### Shared recovery evidence extraction — 2026-10-23

Recovery assessment/restoration now consumes PaymentRecoveryJournal (steps,
journal identity, event-origin availability), with an adapter for existing activation
payments. Recovery no longer requires an activation command internally; its caller
supplies the priority holder. This preserves current behavior and serialized shapes.
Full rules 1,833 and API 840 pass (library-recovery-journal-rules.log and
library-recovery-journal-api.log). Effect-payment failure recovery remains incomplete;
the pending-prompt defect is not fixed by this extraction. No client change or commit.

### Recovery preserves resolution priority — 2026-10-23

Shared recovery now supports a serialized PreservePriority policy, defaulting to
false to preserve activation behavior. A resolving-effect caller can retain the
current holder/pass sequence while refunding supported payment steps. Two tests
exercise life refund, reversal provenance, journal identity reservation, absence of
new life/priority occurrences and serialized replay using no ActivationPayment.
Rules 1,835 and API 840 pass (library-recovery-priority-rules.log and
library-recovery-priority-api.log). This is shared infrastructure verification:
interpreter effect-journal capture and recovery invocation remain unwired, and the
stuck-prompt defect remains open. No client changes or commit.

### Private effect-payment journals — 2026-10-23

Initial and resumed effect payments now retain PaymentRecoveryJournal under their
private RemainingCostPayment. It records snapshots and events for completed and
interrupted steps, excludes choice control events, and survives serialized reload.
Expanded existing regressions verify exact step/movement counts at each pause,
completion flags and redaction for both viewers, including optional payments and
removed sources. Full rules 1,835 and API 840 pass (library-effect-journal-rules.log
and library-effect-journal-api.log). HasEventOrigins remains false; recovery is not
yet invoked and the stuck-prompt defect remains open. No client changes or commit.

### Interpreter origin transport — 2026-10-23

Interpreter results now retain payment origins across event merges and result
creation. Rewrapped replacement prompts remap event indexes after removing old
ChoiceRequested events. Two tests inject marked replacement events and verify
synchronous resolution and two-pause continuation. Full rules 1,837 and API 840
pass (library-interpreter-origins-rules.log and library-interpreter-origins-api.log).
Effect cost-step origin generation and recovery invocation remain unfinished;
these tests do not establish end-to-end recovery or fix the stuck prompt.
No client changes or commit.

### Deferred-choice wrappers preserve origins — 2026-10-23

Corrected five wrappers that discarded PaymentOrigins while appending a replacement
choice. Movement/draw/shuffle deferral tests assert unchanged origin indexes and
replay the new pending state. Full rules 1,840 and API 840 pass
(library-continuation-origins-rules.log and library-continuation-origins-api.log).
Draw proposal expansion still needs origin mapping review; cost-step tagging and
recovery invocation remain unfinished. The stuck-prompt issue remains open.
No client changes or commit.

### Draw expansion origin mapping — 2026-10-23

Reproduced lost origin maps in three draw-expansion cases, then preserved proposal
origins through terminal movement/draw events and across the batch. Tests cover
zero/one/two library cards, two requested draws, unmarked adjacent events, correct
origin indexes and terminal event replay. Full rules 1,843 and API 840 pass
(library-draw-origins-rules.log and library-draw-origins-api.log).
Effect-payment step tagging and recovery invocation remain unfinished; the stuck
prompt remains open. No client changes or commit.

### Effect payment-step tags — 2026-10-23

Effect payment events now carry journal/step/occurrence origins using the shared
PaymentOrigins mapper also used by activations. Replay tests verify library movement
origins after each answer and absence of tags on follow-up/control events. Existing
nested origins are preserved; complete nested ownership is not yet verified, so
HasEventOrigins remains false and recovery is still unwired. Full rules 1,843 and
API 840 pass (library-effect-step-origins-rules.log and library-effect-step-origins-api.log).
The stuck-prompt defect remains open. No client changes or commit.

### Recovery clears the reproduced unpaid-library prompt — 2026-10-23

Four mandatory/optional one/two-card regressions now finish a paused multi-cost
payment when its last mill cannot be paid. Serialized replay before each answer
preserves recovery: the prompt clears, prior life is refunded, milled cards remain
in the graveyard, the spell resolves once, Then is skipped, and optional Else runs
once. The interpreter retains recovery outcome/state while reporting unpaid status.
Overlapping nested journals are guarded, not recovered; unsupported plans and
initial unpaused partial failures remain open. Full rules 1,846 and API 840 pass
(library-effect-recovery-rules.log and library-effect-recovery-api.log).
No live multiplayer or client verification in this backend turn. No commit.

### Immediate and paused recovery agree — 2026-10-23

Four additional immediate-failure regressions reproduced completed mills being
silently discarded. Immediate payment failure now shares RecoverEffectPayment with
paused failure, reserving a journal and preserving completed cost origins. Eight
mandatory/optional one/two-card cases pass with/without replacement pauses: completed
library moves remain, prior life is refunded, Then is skipped and Else/resolution
occur once. Full rules 1,850 and API 840 pass (library-immediate-recovery-rules.log
and library-immediate-recovery-api.log). Other cost combinations, overlapping nested
journals and full-engine trigger cancellation remain open. No client changes or commit.

### Recovery trigger settlement — 2026-10-23

Two tests now run effect-payment output through Game.Settle, and the paused variant
replays the deferred trigger then answers through a fresh Game.Apply. A life-loss
trigger caused by refunded payment disappears, refund creates no life-gain trigger,
and exactly one library-to-graveyard movement trigger remains on the stack. Final
life is restored and the payment prompt is clear. This covers real settlement and
answer routing, not only the trigger-filter helper. Initial spell resolution is
seeded through the interpreter rather than a full cast/priority command sequence.

The first fixture used Mills and found no trigger. Source inspection confirms Mills
exists in the card vocabulary but has no matcher, and MillCost currently emits only
movement events. The recovery test now uses the existing ZoneChange trigger to
isolate cancellation behavior. Dedicated mill occurrence/trigger support remains a
required library gap; generic library-to-graveyard movement is not always milling.

Full rules: 1,852 passed (library-effect-trigger-recovery-rules.log). No production
changes this turn; API baseline remains the previous 840-test run. No commit.

### Mill event/matcher contract — 2026-10-23

Added and registered CardMilled and a dedicated Mills matcher. Three tests verify
serialized explicit occurrences in graveyard/exile, player/card filters, and that
ordinary library movement is not milling. Full rules 1,855 and API 840 pass
(library-mill-trigger-rules.log and library-mill-trigger-api.log).
Actual MillCost/interpreter operations do not emit the event yet; producer integration,
replacement pauses and end-to-end mill-trigger acceptance remain unfinished.
No client changes or commit.

### Actual mill-cost events — 2026-10-23

MillCost now uses LibraryMillContinuation to emit CardMilled from completed selected
movements and retain private PendingMills through replacement choices. Shared batch
completion resumes emission; choice views hide source identities. Existing replay
cases assert exactly one mill event per card. Game settlement/recovery now passes
with the dedicated Mills trigger, not merely ZoneChange. Full rules 1,855 and API
840 pass (library-mill-cost-events-rules.log and library-mill-cost-events-api.log).
Interpreter Mill emission and broader replacement/multi-player acceptance remain
unfinished. No client changes or commit.

### Mill effect event integration — 2026-10-23

Interpreter Mill now emits through the shared coordinator and carries private source
identity through replacement choices. Five new cases verify empty/short libraries,
two-card milling, exactly-once occurrences, delayed follow-up and reload/privacy.
Focused tests pass; the first full run hit an internal CLR/JSON-metadata crash, and
the full retry passes 1,860 rules tests (library-mill-effect-events-rules-retry.log).
API 840 pass (library-mill-effect-events-api.log). Redirected/prevented/multiplayer
and hidden-destination cases remain open. No client changes or commit.

### Redirected and replaced milling acceptance — 2026-10-23

Eight cases cover MillCost versus Mill, redirection to exile versus replacement by
life gain, and immediate versus paused/replayed replacement choices. Actual redirected
arrivals emit exactly two CardMilled events with exile snapshots and two detected
Mills triggers. Replaced movements leave the library intact, emit no mill event and
fire no Mills trigger. The original instruction finishes once in every case.
Initial redirected assertions incorrectly inspected a player-specific exile zone;
the fixture now checks the engine's shared exile zone. No production fix was needed.
Full rules: 1,868 passed (library-mill-replacement-rules.log). API was not rerun for
this test-only change; its previous 840-test baseline remains.

Source inspection found WouldMill matching ObjectMoved solely by library source and
graveyard destination. It does not distinguish an actual mill instruction from an
ordinary move. This replacement-intent gap needs its own regression and correction;
the new tests use WouldBePutIntoZone and do not establish WouldMill correctness.
Hidden destinations, extra replacement-created cards and multiplayer acceptance
remain open. No commit.

### WouldMill distinguishes intent — 2026-10-23

Reproduced ordinary movement incorrectly activating WouldMill, then added MilledBy
to movement requests/proposals. Both mill producers mark their proposals, including
deferred batch entries. WouldMill now requires this intent. Six cases verify ordinary
Move, Mill and MillCost with/without competing replacements and reload. The fallback
zone seam rejects marked requests it cannot preserve. Full rules 1,874 and API 840
pass (library-mill-intent-rules.log and library-mill-intent-api.log). Hidden destinations,
multiplayer and migration of pre-marker paused logs remain unverified. No commit.

### Four-player mill engine acceptance — 2026-10-23

MultiplayerMillTests seeds four Commander players at 40 life and active seat 0 or 2.
Each-player mill-two runs with two competing WouldMill replacements. Four cases use
equal two-card libraries or uneven zero/one/two/three-card libraries. Every choice
replays the full accumulated event stream and uses fresh subsystems. Assertions
verify APNAP player order, private PendingMills redaction for all four viewers,
correct player/card attribution for every CardMilled, exact remaining library sizes,
and one follow-up after all milling finishes. No production change was needed.

Full rules: 1,878 pass (library-multiplayer-mill-rules.log). API was not rerun for
this test-only change; previous 840-test baseline remains. This is engine acceptance,
not a live browser/mobile multiplayer run. Hidden destinations, extra cards created
by replacements and pre-marker paused-log migration remain unverified. No commit.

### Hidden-hand mill privacy — 2026-10-23

Two cases redirect WouldMill into the owner's hand through a competing replacement
choice, using both Mill and MillCost. Serialized replay before/after the answer
preserves one CardMilled occurrence and the final hand card. Before movement neither
player's serialized view contains its unique OracleId. Afterward the owner's hand
contains it and the opponent's entire serialized view does not. No production fix
was needed. Full rules: 1,880 pass (library-hidden-mill-rules.log). API was not rerun
for this test-only change; its previous 840-test baseline remains.

This verifies view privacy, not hidden-zone trigger semantics. Card-filtered Mills
and effects referring to the milled card require separate checks against 701.17c and
zone-change reference rules. The current matcher consults its recorded snapshot,
so that behavior must not be assumed correct for hidden destinations. Face-down
exile and live browser behavior also remain unverified. No commit.

### Hidden-destination mill follow-up references — 2026-10-23

Extended both HiddenMillDestinationTests cases with an unfiltered Mills trigger
whose effect attempts to exile Triggering. Before the fix, both tests failed:
the card incorrectly left the hidden hand (library-hidden-mill-reference-red.log).
TriggerMatching.Milling now retains only the player subject when the arrival is
Hand or Library. CR 701.17c permits finding the milled card only in a public
arrival zone; CR 400.2 defines hand and library as hidden even when revealed.
Primary reference: https://media.wizards.com/2026/downloads/MagicCompRules%2020260619.pdf

After the fix, the trigger still fires and resolves, but cannot move the hidden
card. Serialized replay preserves the hand card and empty exile. Both cost and
effect variants pass. Full rules: 1,880 pass
(library-hidden-mill-reference-rules.log). Card-filtered hidden-destination trigger
matching remains unverified; this change does not decide its semantics. Face-down
exile and live UI acceptance remain outstanding. No commit.

API regression suite: 840 pass (library-hidden-mill-reference-api.log).

### Face-down mill destination snapshots and filtering — 2026-10-23

Four new replay cases cover Mill/MillCost redirected to face-up or face-down exile
through a replacement-order choice. The initial face-down cases failed because
CardMilled captured a face-up ObjectMoved snapshot. Exile previously moved first
and then emitted ObjectChanged(FaceDown). Exile now passes face-down status through
Leave/Relocate to the existing zone movement proposal, making arrival atomic.
The existing exile tests now assert the movement's arrival state instead of
requiring the old separate flip event.

The shared LayerSystem printed-characteristics calculation now distinguishes
face-down exile (no characteristics, CR 406.3a) from the usual face-down 2/2
spell/permanent. This fixes mill card-type filtering without library-specific
characteristics logic. The tests verify one unfiltered mill trigger in either
case, a creature-filtered trigger only for face-up exile, serialized CardMilled
face-down state, and no type/power/toughness for face-down exile.

Evidence: library-facedown-mill-red.log; full rules 1,884 pass in
library-facedown-mill-rules-final.log. The first broad run's sole failure was
InterpreterExileTests requiring the superseded flip event; its corrected arrival
assertion passes. Hidden hand/library filtered-trigger semantics and live UI
acceptance remain unverified. No card definitions added; no commit.

API suite: 840 pass (library-facedown-mill-api.log).

### Face-down mill replay-prefix privacy — 2026-10-23

Extended the four exile destination cases with a unique OracleId and both-player
view assertions. For face-down arrivals, every event-log replay prefix is projected
and serialized: neither player receives the identity, and each visible exile back
is face down with empty OracleId and no power/toughness. Face-up control cases show
the OracleId to both players after replay. This covers both Mill and MillCost
through the competing-replacement choice, and catches transient face-up publication
that a final-state-only test would miss.

No production changes were needed. An initial test expected null rather than the
existing empty-string OracleId redaction contract; the assertion was corrected.
Full rules: 1,884 pass (library-facedown-mill-privacy-rules-final.log). API was not
rerun for this test-only extension; previous 840-test baseline remains. This is
backend projection evidence, not a browser capture or four-player live test.
Hidden hand/library filtered-trigger semantics remain unverified. No commit.

### Normal activation with face-down mill replacement — 2026-10-23

One- and two-card cases now start with Game.Apply(ActivateAbility), pay two life,
and pause the final MillCost on competing WouldMill replacements. Each answer
uses a fresh Game after replay of the entire event log. The test checks that no
activation reaches the stack before the payment completes, a repeated answer is
refused, every milled arrival is face down, the library empties, and exactly one
ability is put on the stack. Normal priority passes then resolve that ability
once, yielding life 21 (20 - 2 + 3), with no payment recovery event.

Full rules: 1,886 pass (library-facedown-activation-rules.log). No production change
was needed. API/client suites were not rerun for these backend tests. This is
normal-command engine acceptance, not a live browser session.

The earlier-payment gate remains: CastingSystem.ActivateCore admits a pause only
when no costs remain and the current cost is mill/library exile. PaymentRecoveryPlan
still marks incomplete steps unsupported; PaymentRecovery refuses unsupported
plans; ChoiceSystem.Resume evaluates the resumed operation before RetainPayment
can capture it; overlapping effect-payment journals are still refused. Successful
final-cost command tests do not justify removing those protections. Next work on
the gate must address failure outcomes inside the resumed operation and journal
overlap, rather than adding more successful final-cost permutations. No commit.

### Nested library payment continuation loss — 2026-10-23

A supported-effect reproduction pays two life, replaces its mill with an inner
payment of three life plus a mill, and pauses that inner mill on two replacements.
Initially the inner replacement-order choice lost its interpreter continuation:
its five-life Then never ran. With a later unpayable outer mill, the engine returned
life 17 instead of retaining the inner follow-up and refunding outer life.
Evidence: library-nested-recovery-diagnostic-result.log.

ProposeThrough now distinguishes bare replacement-order questions from questions
already wrapped by an interpreter proposal. It preserves the latter context,
so nested Then/Else executes before the outer payment resumes. The success case
now finishes at life 27 (20 - 2 - 3 + 5 + 7), empties the library and resolves once
after replay. The later-failure case now reaches the existing overlapping-journal
refusal instead of silently skipping the inner follow-up. Its last committed
choice/life/library remain unchanged. This refusal is diagnostic coverage, not
completion of nested recovery.

Full rules: 1,888 pass (library-nested-payment-continuation-rules-final.log).
The first broad run found a choice-kind source-scan false positive when the
proposal context key appeared in the kind expression; a separate boolean lookup
keeps the expressions distinct without changing the scanner or choice constants.
Next recovery work must track nested origin ownership per outer step. The current
boolean HasOverlappingOrigins cannot establish which nested events belong to a
retained library step versus a reversed independent step. No gate removed, no
card definitions added, no UI acceptance claimed, no commit.

API: 840 pass (library-nested-payment-continuation-api.log).

### Nested payment origin ownership — 2026-10-23

PaymentRecoveryJournal now records NestedOrigins by outer cost-step index.
CompleteOperation uses the interrupted step; RecordEffectPayment uses the shared
PaymentOrigins step/event index mapping. The overlap guard remains unchanged.

The real nested-payment regression initially found no origins after replay.
ReplacementSystem's Applied adapter and Continue/Resume loop discarded payment
metadata from interpreter outcomes, and ZoneMoveSnapshot/MoveInBatch rebuilt
outcomes from state/events alone. Replacement execution now retains and offsets
origins as events accumulate; zone evaluation wrappers change only State on the
original outcome. No alternative event-index or recovery implementation was added.

Both nested success/failure tests now verify that the serialized outer journal
records nested occurrences under mill step 1, that their journal IDs differ from
the outer journal, and that both player views redact EffectPaymentContinuation.
Focused tests: 2 pass (library-nested-origin-ownership-focused-final.log).
Full rules: 1,888 pass (library-nested-origin-ownership-rules-final.log).

This establishes ownership recording, not nested recovery acceptance. Before
relaxing the guard, recovery must prove complete ownership coverage (including
legacy logs missing the new field), classify retained versus reversed nested
occurrences, and preserve/cancel their triggers consistently. Missing mapping
must never imply permission to refund. No gate removed and no commit.

API: 840 pass (library-nested-origin-ownership-api.log).

### Recovery with nested events confined to retained library steps — 2026-10-23

New effect-payment journals explicitly mark nested-origin coverage complete;
legacy deserialization defaults that marker to false. Observing an origin without
an outer-step owner clears completeness. Failure paths now retain ownership from
newly attempted steps as well as the interrupted operation.

Shared PaymentRecovery permits overlapping origins only when coverage is complete,
there is a nonempty ownership record, every owning step belongs to plan.Retain,
and every foreign origin in the current outcome is recorded. Nested events that
would need reversal, missing coverage, and unsupported ordinary plans remain
refused. No normal activation pause gate was removed.

The serialized nested library reproduction now recovers a later unpayable outer
mill: the outer two life is refunded, the nested three-life payment and mill stay,
the inner five-life follow-up runs, and the unpaid outer seven-life follow-up does
not run. Final life is 22; the library is empty; recovery and stack resolution
occur once. Settlement using the combined origin maps leaves exactly one life-loss
trigger (the retained nested payment), cancelling the refunded outer occurrence.
A success control finishes at 27. Removing the coverage field from serialized
journal JSON verifies that legacy recovery is refused with the committed choice,
life and library unchanged.

Full rules: 1,889 pass (library-nested-retained-recovery-rules.log). Trigger evidence
here uses engine settlement over the combined outcomes, not a live browser or
full normal-command failure flow. Nested reversal, multiple nested journals,
repeated nested pauses, incomplete-operation recovery and broader activation gates
remain unfinished. No commit.

API: 840 pass (library-nested-retained-recovery-api.log).

### Nested recovery through priority and answer commands — 2026-10-23

Extended the nested retained-library-step regression to begin resolution with two
Game.Apply(PassPriority) commands and answer both replacement choices through
Game.Apply after full event-log replay. The fixture supplies the spell on stack;
this is not cast/deck-loading or live-browser acceptance.

The initial command-path run failed with an extra life-loss trigger and a two-item
trigger-order choice (library-nested-command-recovery-focused.log). TurnSystem.Log
merged state/events but dropped payment origins, so the outer life-loss trigger
was deferred without provenance and could not be cancelled on refund. Log now
holds an Outcome and delegates merging to Outcome.Then. CastingSystem.ResolveTop
also uses Then when completing stack departure, preserving origins on resolutions
that finish without a choice. No duplicate origin-offset logic was introduced.

After the fix, the command path produces the same life 22, retained mill, one
recovery and one completed spell as the direct interpreter path. Automatic
settlement retains exactly one life-loss trigger, belonging to the nested payment;
there is no spurious trigger-order choice. Full rules: 1,890 pass
(library-nested-command-recovery-rules.log). Broader activation pauses, nested
reversal and repeated/multiple nested payment cases remain unfinished. No commit.

API: 840 pass (library-nested-command-recovery-api.log).

### Repeated pauses inside a nested library payment — 2026-10-23

Two-card nested MillCost recovery now runs through both direct interpreter and
normal priority/answer command paths. The first and second replacement choices
use opposite option positions, with full accumulated event-log replay before the
second answer. While paused, life remains 15 (no premature follow-up/refund),
ownership remains complete and contains every previously recorded nested origin,
and both player views redact the payment continuation.

Both paths emit exactly two CardMilled occurrences, empty the library, refund only
the outer two life, retain the inner payment and five-life follow-up, finish at
22, and resolve once. Settlement retains only the nested life-loss trigger. No
production change was needed. Focused six-case set passes; full rules: 1,892 pass
(library-repeated-nested-recovery-rules.log). API/client suites were not rerun for
this test-only extension; their previous baselines remain. Multiple independent
nested journals, nested reversal, activation gates and live-browser acceptance
remain unfinished. No commit.

### Whole-step nested reversal after a library payment failure — 2026-10-24

A new command-path regression exiles a hand card as a cost; a replacement instead
pays three life and exiles that same hand card, then gains five life. Both replacement
choices reload from the accumulated event log. A later MillCost fails on an empty
library. Before implementation, the second answer was refused
(library-nested-reversal-red-final.log). The preliminary life-replacement fixture
was inapplicable because PayLife does not route through replacement proposals; it
was replaced with the hand-exile fixture rather than used as defect evidence.

PaymentNestedRecovery now owns nested provenance classification. It validates
coverage, retains nested origins owned by retained steps, and adds nested origins
owned by wholly reversed steps to the recovery event's reversed set. It refuses
partial outer-step reversal without finer ownership and conflicts where the same
nested origin would be both retained and reversed. Property/movement restoration
still uses the existing shared PaymentRecovery mechanisms.

The new replay test restores the original hand object and life 20, empties exile,
cancels both nested life-loss and gain triggers, skips the unpaid outer follow-up,
and records exactly one recovery and one spell resolution. The recovery carries
both journal identities. Two policy regressions verify partial-step and conflicting
ownership refusal. Existing retained-step and legacy cases pass unchanged.

Full rules: 1,895 pass (library-nested-reversal-rules.log). Multiple independent
nested journals, partial nested reversal, incomplete-operation recovery, activation
gates and live-browser acceptance remain unfinished. No card definitions added;
no commit.

API: 840 pass (library-nested-reversal-api.log).

### Independent nested journals before a library-cost failure — 2026-10-24

Expanded the whole-step reversal command test to two distinct hand-exile costs,
filtered to a creature and a land so each choice is forced without choosing for
the player. Each cost's replacement opens its own nested life/exile payment. Four
normal AnswerChoice commands run against fresh subsystems with full accumulated
log replay between answers, followed by failure of the final mill on an empty
library.

Recovery restores both original hand objects in their original order and life 20,
leaves exile and stack empty, records three distinct journal identities (outer and
two inner), and reverses the combined net four-life gain exactly once. No reversed
life-loss/gain triggers or outer unpaid follow-up remain. The one-payment control
still passes. No production change was needed.

Focused eight-case suite passes. Full rules: 1,896 pass
(library-independent-nested-recovery-rules.log). API/client suites were not rerun
for this test-only extension. This verifies two independently reversed nested
journals; mixed retained/reversed nested journals and deeper nesting are not
established by this case. Partial nested reversal, incomplete-operation recovery,
activation gates and live-browser acceptance remain unfinished. No commit.

### Current-engine four-browser mill-payment acceptance — 2026-10-24

Started an isolated TableQa host from the current engine with libraryReplacement,
librarySeats=4 and port 4340, using the existing development client build. Ran
verify-library-payment.js --replacement --four against table
d369d632-ed3a-4e35-8b7b-d91f26e44c81. All four authenticated browsers passed
cancel/reopen, payment confirmation, reload during the real replacement choice,
replacement answers, broadcasts, final counts and final actor reload. Revisions
were 19/17/17/17. Actor library 57 -> 55, life 40 -> 39, and the activation is on
the stack; this run stops before resolving that activation.

Evidence: library-mill-current-live.log and current captures
library-replacement-live-four-prompt.png, -pending-landscape.png, -seat-0.png
through -seat-3.png in tools/TableQa. Visually inspected the desktop payment
prompt, 667x375 pending choice and final actor board. The landscape choice list
is scrollable. Synthetic fixture cards have no production card artwork; these
captures establish this library interaction, not full board resemblance or
production-card coverage. No physical-device acceptance is claimed.

No production/client source changes were needed and npm verify was not rerun;
its recorded baseline remains. Redirected/face-down milling and nested payment
failure still need corresponding live browser scenarios. No commit.

## 2026-10-24 — live face-down mill redirect

Four authenticated headless browsers passed on table
9542ebbd-6a3b-4714-b45e-f5a17c956253. QA-only --libraryFaceDownMill selects
competing WouldMill replacements, including exile face down. Existing payment
cancel/confirm, pending actor reload, replacement decisions and final actor reload
passed. All four final projections show two new face-down exile objects with empty
name/OracleId/types and null power/toughness; every recorded broadcast was checked
for accidental identity disclosure of those arrivals. Life 40→39, library 57→55,
activation stack count 1. Ability resolution was not exercised.

Evidence: library-face-down-mill-live.log (exit 0), corresponding host logs,
tools/TableQa/library-face-down-mill-live-four-*.png. Desktop final seat 0 and
667x375 pending-choice captures visually inspected. Synthetic QA cards have no
artwork. No production/client source changed; npm verify was not rerun. Existing
rules/API/client baselines remain the applicable suite evidence. No commit.

## 2026-10-24 — live nested library payment recovery

Table 178fbf4d-99b3-494d-b3a6-a6f20f9df2e6 passed four authenticated headless
browser sessions. The QA-only spell begins on stack; four normal priority passes
start resolution. Three browser replacement decisions each survive actor reload.
Private payment contexts are absent from all player projections; observers have
no choice options. Nested payment mills both remaining cards; the later outer
mill fails. Final life is 42 (outer life refunded, nested life/gain retained),
library empty, exactly two CardMilled events, one recovery, one resolved spell,
and one retained nested life-loss trigger. Every seat's final reload preserves its
projection. No hub refusals. The remaining trigger is not resolved by this test.

Evidence: library-nested-payment-live.log (exit 0), corresponding host logs and
library-nested-payment-choice-{1,2,3}.png / final-seat-{0,1,2,3}.png under
 tools/TableQa. Visually inspected inner choice at 667x375 and final actor desktop.
The final stack sidebar also shows Forest / Tap: Add {G} beneath the trigger;
this is a visible detail-panel issue requiring separate diagnosis, not accepted
as correct UI. The first harness launch preceded host readiness and received
ECONNREFUSED; reran only after TABLE_QA_GAME confirmed readiness.
QA host/harness/docs only changed. No npm verify rerun or production-card changes.
No commit. Casting-from-hand and broader nested recovery cases remain unverified.

## 2026-10-24 — retained nested trigger resolution

Extended the same four-browser recovery harness on fresh table
 eb9959e2-0589-4fc9-986d-729fc35401f2. After the verified recovery and all-seat
reload, four normal priority commands resolve the sole retained trigger. All four
projections have life 43, empty stack and no pending choice. Exactly one additional
StackObjectResolved event occurs; mill/recovery counts stay unchanged. Another
reload of every browser preserves each full projection and all observed event
counts. library-nested-resolution-live.log exited 0. Final actor capture
 tools/TableQa/library-nested-payment-resolved-seat-0.png visually inspected.

Correction to the prior visual diagnosis: game-board.component.html renders
.quick-card from quickCard$ (ui.hoveredCard) separately from stack entries. The
Forest / Tap: Add {G} text is that hover preview, also present in the empty-stack
capture. It is not evidence of incorrect trigger text. No client fix is justified
by that observation. No production sources changed; no npm verify rerun or commit.
Casting the fixture from hand remains outside this scenario's verified scope.

## 2026-10-24 — casting into nested library recovery

Fresh four-seat table b0354131-3b1b-4834-acd5-4753c6bdb0ce uses
--libraryNestedCast=true: the QA spell starts in hand with explicit {0} mana cost.
The browser clicks Play, Review spell, then Cast. All four projections confirm
one card leaves hand and the spell enters stack. Existing three-choice/reload,
nested mill recovery, retained-trigger resolution and all-seat final reload checks
then pass. Final actor life 43, hand 4, library 0, stack 0. No hub refusals.
Evidence: library-nested-cast-live.log, host logs and refreshed
 tools/TableQa/library-nested-payment-*.png. Final resolved actor capture inspected.

The initial harness attempt omitted Review spell and timed out before any game
command; corrected the harness after inspecting the casting panel and template,
then reran on the unchanged table. This proves zero-mana casting integration for
this library scenario, not all mana/target/alternate-cost casting paths. Changes
remain QA-only; no npm verify rerun, production card expansion or commit.

## 2026-10-24 — nested library choice phone checks

Table 643354e7-7e92-457c-8bcf-8f22a43579f5 passed the complete casting/recovery/
resolution/reload scenario with viewport transitions at all three choices. At
667x375, every replacement button measured at least 44x44, stayed within the
viewport after nearest scrolling, and passed center-point hit testing. No document
horizontal overflow. Pending choice IDs survived both orientation changes.
Landscape first-choice capture visually inspected and readable.

Portrait 375x667 intentionally uses .portrait-notice with Turn your phone sideways,
consistent with earlier library acceptance. The initial new test incorrectly
required interactive portrait controls and failed against this intentional overlay;
corrected to test the notice and choice preservation. The notice isDisplayed/text
checks pass, but the resulting portrait capture is blank, so portrait visual
acceptance is NOT established. Capture timing/rendering needs follow-up; do not
infer a production defect or visual pass from the DOM checks alone.

Evidence: library-nested-phone-live.log (exit 0), corresponding host logs and
 tools/TableQa/library-nested-payment-choice-{1,2,3}-{portrait,landscape}.png.
An earlier launch terminated with an empty log before browsers opened; Node version
and syntax checks passed before retry. QA harness only; no npm verify rerun or commit.

## 2026-10-24 — portrait capture correction

Reopened tools/TableQa/library-nested-payment-choice-1-portrait.png individually:
the saved 375x667 image clearly shows Turn your phone sideways and The game board
is designed for landscape, centered and readable. SHA-256 of all three portrait
choice captures is identical:
19D642AC4E7A98A1E3048E9AD69022A086053BF1BF7E6B6DCA624189322FC3B5.
The previous blank-image diagnosis was incorrect; there is no evidence of a saved
capture defect. Existing library-nested-phone-live.log supplies the successful
orientation-preservation and landscape-control checks. No rerun or code change
was necessary. This closes portrait visual verification for this specific nested
payment scenario, not the full library UI inventory. No commit.

## 2026-10-24 — replacement source sacrificed during library payment

New LibraryReplacementSourceTests reproduces an off-stack replacement paying
SacrificeSelf followed by MillCost(1/2), interrupted by competing replacements.
Both cases originally refused on answer: the effect that asked cannot be found
again (library-replacement-source-tests.log). The source identity was already gone.

The interpreter now snapshots the off-stack ability effect in private ChoiceContext
when Run has an ability index. Ask serializes it; Elsewhere resumes from that tree
and carries it through subsequent pauses. Existing source lookup remains the
fallback for earlier contexts without the snapshot. No closure or duplicate payment
implementation. Player projections omit the saved effect.

Both replay regressions pass: source sacrificed once, one/two cards milled exactly
once, Then gains five life once, original spell resolves once, private context
withheld from both players. Full rules 1,898 and API 840 pass in
library-replacement-source-rules.log and library-replacement-source-api.log.
This is interpreter/replay evidence; the sacrificed-source case has not yet been
verified through live browsers. Older paused logs with an already-missing source
cannot reconstruct a snapshot that was never recorded. No client change, npm verify
rerun, production card expansion or commit.

## 2026-10-24 — live sacrificed replacement source

Four-browser table d9739c9e-9ea7-4023-8202-378eec2c95a0 passed with the new engine
snapshot fix. Host --librarySacrificeSource=true and verifier --sacrifice-source
reuse the nested casting/recovery scenario. The replacement sacrifices its own
artifact before milling two cards; two separate scout permanents supply the
competing replacements after that source is gone. All four projections show the
artifact absent from battlefield and exactly one artifact in its owner's graveyard
during both inner choices. OffStackEffect and other private continuations remain
absent from player views. Each choice survives actor reload and orientation changes.

Final life 45, library 0, hand 4, graveyard 5, stack empty. Two mill events, one
recovery and one resolved spell; all-seat reloads append no events. This variant
has no life-loss watcher/retained trigger, despite the shared log result sentence
mentioning retained-trigger resolution; its assertions require zero extra resolved
events. library-sacrificed-source-live.log exited 0. Visually inspected
 tools/TableQa/library-sacrificed-source-choice-2-landscape.png and
 library-sacrificed-source-resolved-seat-0.png. QA changes only this turn; backend
baseline remains 1,898 rules / 840 API. No client change, npm verify rerun or commit.

## 2026-10-24 — mixed retained/reversed nested payments

Extended the existing normal-command/replay recovery theory with one or two
independent nested hand-exile payments followed by a nested two-card mill payment
and an unpayable final mill. Every answer is replayed from serialized events with
fresh subsystems. Independent hand cards return with original identities/order;
their nested life changes are reversed. The nested library payment remains:
two CardMilled events, empty library and final life 22. Exactly two life-event
triggers remain, and their ordering is answered through the normal command path.
Recovery records only the expected reversed journal identities; original spell
resolves once and its unpaid Then gain does not occur.

The first test bound omitted the legitimate trigger-order choice; corrected the
test to answer that choice explicitly. No production change was needed. Targeted
four cases pass (library-mixed-nested-tests.log); full rules 1,900 pass
(library-mixed-nested-rules.log). API baseline remains 840; no API/client change or
npm verify rerun. This verifies whole-step mixed recovery with a retained library
step, not partial nested reversal or arbitrary deeper nesting. No live/browser
acceptance claimed for these mixed variants. No commit.

## 2026-10-24 — two levels of retained nested library payments

Extended mixed-recovery theory with two consecutive WouldMill replacement payments:
the first pays life and mills one; the second pays life and mills two. Competing
replacement choices pause the inner movement. One/two independent nested hand-exile
payments precede them; a final unpayable mill triggers outer recovery. Fresh normal
commands and serialized replay between every choice retain both nested library
payments while reversing the independent payments. Final life 24, original hand
identity/order, empty library/exile, exactly two CardMilled events and four retained
life-event triggers with an explicit ordering answer. Original spell resolves once;
its unpaid Then is absent. Recovery reverses the expected independent journal count.

Six targeted cases pass (library-deeper-nested-tests.log); full rules 1,902 pass
(library-deeper-nested-rules.log). No production change. This verifies the described
two-level whole-step shape, not arbitrary nested trees or partial-step recovery.
Live/browser verification of the mixed/deeper variants remains outstanding. API
baseline 840 and client baseline 1,854 unchanged. No commit.

## 2026-10-24 — persisted nested occurrence ownership

PaymentRecoveryJournal now stores NestedPaymentOccurrence records linking each
outer event origin to its nested origin, plus separate NestedOccurrencesComplete
coverage. Begin enables coverage for new journals; defaults remain false for older
journals. Missing outer event indexes or conflicting ownership clear coverage.
Whole-step NestedOrigins remains for existing recovery behavior. CompleteOperation
now offsets resumed owner indexes by the saved step history count.

NestedPaymentOccurrenceTests verifies two repeated resumes with choice-control
events excluded from numbering and JSON round-trips after each; owner indexes
continue 1,2 rather than aliasing at zero. Conflicting ownership cannot claim
coverage. Existing nested replay cases additionally require complete, nonempty
ownership and cumulative records after repeated pauses. Full rules 1,904 and API
840 pass (library-occurrence-rules.log, library-occurrence-api.log).

This adds evidence only: PaymentNestedRecovery does not yet use occurrence records,
and effect HasEventOrigins remains false. Partial nested reversal is still refused.
Next: classify mapped nested origins against actual event reversals, preserve
legacy/conflict refusal, and prove generated partial-recovery behavior before
opening that capability. No client change, npm verify rerun or commit.

## 2026-10-24 — partial nested-origin classifier

PaymentNestedRecovery now consumes complete occurrence ownership when an outer
step is partially reversed. It requires the step to be in the reverse plan,
event-origin support, separate occurrence completeness, exact nested-set coverage,
valid journal/index references and unambiguous outer ownership. Each nested origin
is classified using its owner's actual event reversal. Retained/reversed overlap
is checked in both directions, including a retained whole step versus a reversed
individual event. Legacy whole-step behavior remains available; incomplete partial
evidence is refused.

Six new cases cover exact partial classification, legacy coverage, missing mappings,
shared/conflicting origins, whole-step overlap and out-of-range indexes. Eight
targeted tests pass; full rules 1,910 and API 840 pass in
library-partial-classifier-rules.log / library-partial-classifier-api.log.
These are classifier tests with constructed journals. Effect journals still start
with HasEventOrigins=false; generated partial movement recovery and live acceptance
remain unverified. Do not treat this as completion of partial effect-payment recovery.
No client change, npm verify rerun or commit.

## 2026-10-24 — generated partial nested recovery

PartialNestedLibraryRecoveryTests now uses actual cost/replacement events. A nested
sacrifice payment changes life, then a later mill replacement returns that artifact
to library and shuffles; the final mill cannot be paid. Recovery reverses the nested
life changes while retaining the sacrifice/library dependency. Replay ends at life
20, two library cards, original artifact identity absent, one resolved spell in
graveyard, and exactly one retained departure trigger. A legacy HasEventOrigins=false
control refuses the same partial recovery. Nested ownership coverage is asserted.
The fixture uses interpreter resolution/choice APIs and settlement, not the full
Game.Apply priority/answer path or browser UI.

The test exposed duplicated identical sacrifice markers and dropped nested origins.
Movement recovery now validates distinct marker values (both original markers remain
in history); sacrifice/discard wrappers compose Outcomes without dropping metadata;
CostPayment.Continue preserves and offsets origins while combining step events.
New effect journals now enable HasEventOrigins. Existing replay regressions were
updated to require that flag. Synchronous nested payment without a pause did not
produce a separate nested journal; the final regression deliberately pauses the
nested sacrifice to exercise occurrence ownership rather than claim it indirectly.

Targeted two cases pass (library-partial-generated-fixed.log); full rules 1,912 and
API 840 pass (library-partial-generated-rules.log, library-partial-generated-api.log).
Normal-command and live partial-recovery acceptance, other dependent property shapes
and earlier activation pause gates remain outstanding. No client changes, npm verify
rerun or commit.

## 2026-10-24 — partial recovery through normal commands

Added a normal Game.Apply variant to PartialNestedLibraryRecoveryTests. Two priority
passes begin resolution, with replay between them; every replacement answer uses a
fresh Game/Subsystems and serialized replay before the next command. The original
interpreter and legacy-refusal controls remain. All three targeted cases pass in
library-partial-command-tests.log. Automatic game settlement produces exactly one
retained artifact-departure trigger after cancelling refunded nested life events.
Life returns to 20, two cards remain in library, original source identity remains
absent, and the resolving spell finishes once. No additional production change.

Full-suite baseline remains the prior 1,912 rules / 840 API run; this targeted run
adds one passing command-path case without claiming a new full-suite count. The
fixture begins with a stack object. Live-browser partial recovery and broader
unsupported cost combinations remain outstanding. No npm verify rerun or commit.

## 2026-10-24 — four-browser partial nested recovery

Table 91d2cdad-f135-48b4-93df-69e42ed7f548 passed browser casting from hand,
three replacement decisions with actor reload/orientation checks, partial life
recovery, retained library return/shuffle, departure-trigger resolution and final
reloads for every seat. All four projections end at life 40, library count 3,
hand count 4 and empty stack. Exactly one recovery, one retained shuffle, no actual
mill events, one resolved spell and one resolved departure trigger. Final reloads
preserve complete projected state and append no additional observed library events.
Private continuation fields remain absent from all player views.

The initial fixture's departure trigger gained life and therefore correctly created
another gain-life trigger, invalidating the harness's empty-stack expectation.
Changed only the QA departure body to Draw(0), isolating exactly-once resolution;
production behavior was correct. Evidence: library-partial-live.log (exit 0), host
logs, tools/TableQa/library-partial-recovery-*.png. Visually inspected third choice
at 667x375 and final actor desktop. Fixture flags: host --libraryPartialRecovery=true
--port=4340; verifier --partial. QA-only changes this turn; production baseline remains
1,912 full rules / 840 API, plus the separately passed normal-command regression.
No npm verify rerun, production card expansion or commit. Other partial property
combinations and earlier activation pause gates remain incomplete.

## 2026-10-24 — pending mill ownership across replay

Extended `MillTriggerTests` to assert that every pending mill source still belongs
to the player's library at each replayed replacement choice. Existing effect cases
passed before any production change; no stale-source defect was reproduced.
Added four payment cases covering exactly sufficient/spare library cards, with and
without replacement pauses. They verify one event per milled card, consumed source
identities absent from pending work, private projected continuation data, and one
follow-up/resolution. All 20 tests in the class pass (`library-mill-pending-tests.log`).

Only tests changed. This does not establish a supported-operation failure inside
resumption, nor implement the partial-failure contract. The full-suite baseline
remains 1,915 rules / 840 API from the preceding production change; this run adds
four targeted cases without claiming a new full-suite total.

## 2026-10-24 — accepted search response loss and reload retry

Extended the browser delivery probe and existing library verifier to cover default
manual search. Four-player table `a3aa46ab-21ec-438d-a24f-a3a2f57adfe3` passed:
accepted search completion dropped, reconnect, pending actor reload, private
Land selection, explicit confirmation, one search/one shuffle, second actor reload,
and same-form retry using the original wire request ID. The retry leaves the
authoritative view and library event counts unchanged. All seats receive the
completed state; final actor reload preserves it. Other seats are not reloaded in
this scenario. Search-to-battlefield delivery loss remains untested.

Evidence: `library-search-retry-live.log`, host log, and the inspected
`tools/TableQa/library-lost-response-retry.png` capture showing the already-applied
notice and final library/hand counts. Command:
`node tools/TableQa/verify-library-payment.js --search --reload-response --four`.
Host flags: `--libraryDraw=true --librarySeats=4 --port=4340`.
Only QA tooling changed; no production card or client changes, and no new npm
verify run. The isolated fixture intentionally has no artwork. No commit.

## 2026-10-24 — battlefield search retry and all-seat reload

Four-player table `966e8592-7ec1-4ee7-9f82-f0477b212247` passes the lost-response
scenario with two Basic Forests, battlefield destination, enter tapped, reveal on,
and shuffle off. Reconnect and pending actor reload preserve the search. After
confirmation and another actor reload, the same form reuses the original request
ID. There is exactly one search, one reveal, zero shuffles, and two new tapped
Forests. Retry leaves state/event counts unchanged. Every browser is then reloaded;
each authenticated projection matches its prior view and event counts stay unchanged.

Evidence: `library-search-battlefield-retry-live.log`, host log, inspected retry
capture (preserved as `tools/TableQa/library-search-battlefield-live-four-retry.png`)
and opponent seat-1 capture showing Forest x4 / 2 tapped and library 55.
Command: verifier `--search-battlefield --reload-response --four`, same draw-enabled
four-seat QA host as the default-search scenario. Shared `configureSearch` avoids
separate initial/retry form logic. Only QA tooling changed; no production behavior
or card expansion, no npm verification rerun, and no commit.

## 2026-10-24 — off-stack effect redaction

`ChoiceSecrets.Withheld` omitted the new private `OffStackEffect` field from both
its no-change predicate and its cleared fields. A serialized choice carrying only
that field reproduced the defect (`library-offstack-redaction-red.log`): the helper
returned the private tree. Both branches now account for it. The regression checks
direct redaction, owner/opponent ViewProjector output, unchanged options, idempotent
redaction and preservation of the original server effect through event-log replay.
Full rules suite: 1,920 passed (`library-offstack-redaction-rules.log`).

This verifies the shared helper/base projector boundary; it does not claim an
observed production-network disclosure. Higher-level projection checks previously
passed and remain covered. Hidden-destination mill characteristic rules are still
unresolved; no new observation metadata or card definitions were introduced.

## 2026-10-24 — inspection delivery loss

Four-seat table `134b50f0-5795-4b14-b85f-89c33864c88b` passed accepted Look response
loss, reconnect, actor reload into active inspection, Stop looking as a distinct
action/request ID, and all-seat final reload. Only the actor receives inspected
cards; every view loses active inspection afterward. The strengthened run compares
the exact authoritative order of all four libraries before and after, and confirms
unchanged library event counts. No duplicate look command is sent automatically.

Evidence: `library-look-delivery-order-live.log`; inspected
`tools/TableQa/library-look-live-four-delivery-recovered.png`. The first run,
`library-look-delivery-live.log`, covered counts/privacy without the later exact-order
assertion. Both runs used fresh browser sessions on the same table; the first had
finished inspection before the second began. Flags: verifier
`--look --reload-response --four`, host `--libraryDraw=true --librarySeats=4 --port=4340`.
Lost Stop completion, reposition and arrangement delivery loss remain unverified.
Only QA tools changed; no production/client changes, npm verify rerun, or commit.

## 2026-10-24 — repeated inspection cycles after response loss

Lost Stop completion initially passed closure/reload, but extending the scenario to
a second look/stop cycle reproduced a client defect. The later Stop reused the
uncertain old Stop request ID, so the server treated it as already applied and left
the new inspection active. `library-stop-cycle-red.log` timed out; an authenticated
snapshot confirmed active inspection with 57 cards. Before-fix capture is preserved
as `tools/TableQa/library-inspection-cycle-before-fix.png`.

`LibraryRequestJournal.complete` now retires an older uncertain opposite inspection
action when completing the matching current request: Look supersedes old Stop,
and Stop supersedes old Look. It preserves same-action retries and leaves other
action keys and table/player scopes alone. Four added client regressions cover
both directions and preservation when the opposite request also fails.

Client `npm run verify` passes with 1,858 tests, coverage, formatting/lint and
production build (`mtg-client/library-inspection-cycle-verify.log`). The existing
card-modal 18.05 kB/16 kB stylesheet warning remains.

The rebuilt client passes both four-browser scenarios on fresh table
`7e6096fa-25cd-4c2f-9064-3c917cda3e7f`: `library-stop-cycle-fixed-live.log` and
`library-look-cycle-fixed-live.log`. Each run uses fresh browser sessions, completes
two inspection cycles, asserts four distinct request IDs, private inspection,
exact unchanged library orders, unchanged library event counts and all-seat reload.
The second run starts after the first has closed inspection. Inspected both final
delivery-recovered captures. The earlier red fixture was
`8f9c359d-fca0-4021-85b7-6039f46fd073`.
No backend behavior or card definitions changed. No commit.

### 2026-10-24: reposition accepted-response loss

Four-browser top and bottom reposition scenarios pass on QA table
`c5e08f74-b837-47e1-9733-3274747beae4`. Logs:
`library-reposition-delivery-live.log` and
`library-reposition-top-delivery-live.log`. Each records the actual outgoing
card identity, drops its accepted completion, reconnects, reloads, reopens
inspection and retries that same card/destination. Assertions establish identical
request IDs and payloads, exact order of every library, exactly one reorder,
inspection privacy and final all-seat reload. Inspected the bottom retry capture.

The initial harness failure assumed the first displayed card was the library top.
Inspection sorts/groups its display. Corrected the harness to track the submitted
card and locate its repeated-name occurrence in the reopened inspection, with an
independent outgoing-payload assertion. No production defect was established by
that initial failure. These changes affect QA only; production verification remains
the previously recorded 1,921 rules, 840 API and 1,858 client tests.
Arrangement delivery-loss acceptance remains outstanding. No commit.

### 2026-10-24: arrangement accepted-response loss

`library-arrange-delivery-live.log` passes on four-player table
`c5e08f74-b837-47e1-9733-3274747beae4`. The browser swaps the first two cards,
confirms, loses the accepted completion, reconnects and reloads. Reopening the
editor and confirming the same order sends the identical full payload and request
ID. Exact orders of all libraries and library event counts remain unchanged on
retry, with exactly one reorder since baseline. All seats reload successfully.
The editor's local draft/cancel checks and portrait/landscape captures also run.
Inspected landscape and retry captures. QA-only changes; no new production fix
or full-suite run. This establishes this single retry sequence, not arbitrary
interleavings of later actions. No commit.

The same command with `--touch` also passes (terminal session 80949, exit 0),
including an explicit assertion that opponents receive no inspected cards after
reopening. This is browser touch emulation, not physical-device acceptance.

### 2026-10-24: confirmed intervening arrangement exposes stale request

New `--arrange --reload-response --four --intervening-arrange` scenario fails
on table `c5e08f74-b837-47e1-9733-3274747beae4`:
`library-arrange-intervening-red.log`. Arrangement A is accepted with its response
lost. After reload, arrangement B restores the original order successfully. The
user then explicitly selects A again. The outgoing request reuses A's original
request ID (`bb7409b3-521f-4a37-8b58-8fe05661e820`), so it is deduplicated rather
than applied as a fresh intent. The assertion fails on the reused ID. This is
distinct from the valid immediate same-order retry established above.

Current journal completion removes only its exact action key, plus opposite
inspection keys. It leaves old arrangement payload keys indefinitely after other
confirmed arrangements. Fix remains outstanding. Retirement must preserve an
immediate uncertain retry across inspection/reload, distinguish later confirmed
mutations, remain scoped to the same table/player, and preserve uncertainty when
the intervening action also fails. Broader cross-action semantics require explicit
tests; do not clear all keys merely on reconnect or on Look completion.

Implementation under verification: `LibraryRequestJournal.complete` now retires
prior mutation keys within the matching table/player scope after a confirmed
mutation. Inspection completions retain mutation keys, and rejected sends never
complete the journal. The current-request equality guard still applies. Two new
service tests cover inspection/reload plus a failed intervening mutation, and
same-scope versus other-scope confirmed mutations. Three older tests explicitly
expected stale IDs after intervening successful search/reposition/arrangement;
those expectations now require fresh IDs while retaining payload checks.
`library-mutation-intent-verify.log` is the verification run; no passing result
is claimed until it completes. Live red-to-green confirmation remains required.

Verification completed: `npm run verify` exits 0 with 1,860 tests, coverage gates,
format/lint/UI checks and production build. Existing card-modal stylesheet budget
warning remains (18.05 kB versus 16 kB).

Rebuilt-client live evidence: `library-arrange-intervening-fixed.log` passes the
four-player A → B → A sequence with a fresh final request and three reorders.
`library-arrange-immediate-fixed.log` separately passes an immediate retry with
identical payload/request ID and only one reorder. Both verify exact orders,
private reinspection and all-seat reload. Inspected the opponent final capture.
The first fixed attempt encountered inspection left open by the red run and
closed it; the recorded passing rerun started with inspection closed. No backend
or card-definition changes. No commit. Other cross-action live interleavings are
not established by these arrangement scenarios.

Cross-action follow-up: `library-arrange-reposition-live.log` passes four players
with `--arrange --reload-response --four --intervening-reposition`. After the
uncertain arrangement, a confirmed right-click top-of-library move restores the
original order. A new arrangement then uses a fresh request, producing exactly
three reorders in total. Exact order, card identity, private inspection and final
all-seat reload are asserted. Shared QA card selection was extracted to avoid
duplicating repeated-name inspection lookup. Production code is unchanged since
the 1,860-test verification above.

### 2026-10-24: supported nested inability after shuffle

Two added LibraryMillCostTests cases place a shuffle before an unpayable nested
mana cost inside the selected replacement. All four variants of that test pass;
the full LibraryMillCostTests class passes 178 tests
(`library-resumed-unpayable-tests.log`, `library-mill-cost-current-tests.log`).
Replay preserves exactly one preceding shuffle, skips the unpaid cost's conditional
shuffle, completes the outer activation, and rejects duplicate answers.
This establishes handled nested inability, not a partial failure escaping operation
resumption. Updated the failure contract with that distinction; production admission
remains unchanged. Initial sandbox runner aborted on testhost connection timeout;
the completed outside-sandbox run passed. The QA host was stopped for backend tests.
No production changes or commit in this checkpoint.

### 2026-10-24: unaffordable nested cast during a library replacement

Added the normal-command/replay regression described in
`LIBRARY_RESUMED_FAILURE_CONTRACT.md`: pay life, begin mill, replacement shuffles,
offer a green instant, refuse the unaffordable yes answer, reload and decline.
Assertions identify the mana refusal, preserve the pending choice, prior shuffle,
library order/RNG and hand card, complete the outer activation and refuse a duplicate
answer. Full LibraryMillCostTests: 179 passed
(`library-mill-cost-nested-cast-tests.log`). No production change. This rules out
treating that invalid answer as automatic outer payment recovery; it does not
establish support for arbitrary failures after nested costs have been spent.

### 2026-10-24: nested cast preflight includes provisional costs

`CheckInstructed` calls the complete immutable cast path; it is not only an
affordability snapshot preceding separate payment behavior. Expanded the nested
cast regression with a three-life additional cost before the unpayable green mana.
The rejected answer does not commit that provisional payment; reload/decline
retains the outer two-life payment and single prior shuffle. All 180
LibraryMillCostTests pass (`library-nested-cast-provisional-cost-tests.log`).
Corrected the proposed failure hypothesis in the contract. No production changes.

### 2026-10-24: replacement-added mill follow-up fix

Reproduced a concrete 701.17d failure: replacing mill one with mill three emits
three unique CardMilled events, but a following Move of the remembered milled
cards moved only one. Red evidence: `library-extra-mill-followup-red.log`.
`MoveObjects` now includes public CardMilled arrivals emitted by the current
mill movement/replacement in its remembered arrival list, deduplicated with the
original arrival. Hidden hand/library arrivals are excluded from that addition.

Direct and replacement-choice/replay variants pass
(`library-extra-mill-followup-tests.log`, two tests). Full rules suite passes
1,927 tests (`library-extra-mill-rules.log`). This verifies the tested replacement
and follow-up action; it does not establish every characteristic aggregation,
cast permission, or nested replacement combination. Primary rule crosscheck:
https://media.wizards.com/2026/downloads/MagicCompRules%2020260619.pdf, 701.17c-d.
API verification is recorded separately when complete. No commit.

API verification completed: 840 passed (`library-extra-mill-api.log`). Live UI
acceptance for this engine change is still unverified; the QA host remains stopped.

### 2026-10-24: hidden arrivals and pauses inside expanded mill

Expanded the replacement-added mill regression to graveyard, exile and hand,
each with and without an additional replacement-order pause. Red evidence in
`library-extra-mill-destination-tests.log`: a hand arrival incorrectly remained
available to the follow-up, and the paused exile variant remembered only one
of three arrivals. The first fix had collected arrivals only after Relocate
completed, too late for a captured choice context; additionally the resumed
missing-source branch did not identify itself as a mill proposal.

Public mill arrivals are now accumulated in ProposeThrough before a pending
choice captures memory, including when the original card has already left.
MoveObjects uses that retained list exclusively for mills, eliminating the old
single-arrival fallback that reintroduced hidden cards. All six focused cases
pass (`library-extra-mill-destination-fixed.log`), including serialized replay
and exactly three unique mill occurrences. Full-suite results follow separately.
No commit; live acceptance remains outstanding.

Full validation passed: 1,931 rules tests (`library-mill-arrivals-rules.log`) and
840 API tests (`library-mill-arrivals-api.log`).

### 2026-10-24: live expanded mill follow-up acceptance

Added a QA-only spell/replacement fixture and
`tools/TableQa/verify-library-mill-followup.js`. Four independent browser accounts
cast through the UI, pass priority, and answer seven replacement choices, reloading
the actor at each pause. The result records exactly three mills and one resolution,
reduces the library by three, and returns all three new card instances to hand.
Recursive opponent projection checks reject those private identities. All four
seats reload without changing snapshots or event counts; no refusal occurs.

Passing log: `library-mill-followup-live.log`, table
`f5cbae21-99a1-49a7-9290-117580e303de`. Inspected actor and opponent captures
`tools/TableQa/library-mill-followup-seat-0.png` and `...-seat-1.png`.
This establishes the public-exile replacement follow-up through the real session
and hub; characteristic aggregation and casting-permission consumers remain
separate outstanding checks. QA host parent 44664, child 50084 remains running.
No production card definitions changed. No commit.

### 2026-10-24: expanded mill value and permission consumers

The same replacement/replay regression now uses cards with mana values 1, 2 and 3
and additionally checks a follow-up gain-life calculation plus MayPlay permission.
Public graveyard/exile cases gain six life and create a permission whose AppliesTo
set exactly equals all three mill arrival IDs. Hand-redirected cases gain no life
and create no permission. Direct and paused paths are included. All ten focused
variants pass (`library-extra-mill-consumers-tests.log`). No production change;
the prior full-suite baseline remains 1,931 rules / 840 API. Immediate optional
casting of each arrival is distinct from a lasting play permission and remains
unverified. QA host was stopped for compilation. No commit.

### 2026-10-24: immediate cast offers skip additional milled cards

New regression `Replacement_extra_milled_cards_each_receive_an_immediate_cast_offer`
fails for both accepting and declining every offer: a mill-one-to-three replacement
produces only one immediate MayCast offer instead of three. Each pause is replayed.
Log: `library-mill-cast-offers-red.log` (two failed tests). The existing immediate
MayCast handler in EffectInterpreter.Retarget.cs explicitly uses cards[0]; the
lasting permission path already accepts the complete list.

Fix outstanding. Preserve a stable offered-card list and completed/declined progress
across replay, including additional-cost subprompts, without reoffering earlier
cards or recomputing indices from a selector changed by earlier casts. The existing
ForEach traversal is relevant to reuse, but a naive count-based wrapper would
change the continuation shape as cards leave their zones. Current worktree has
these two intentional failing regressions; earlier passing suite counts predate
them. No production changes or commit in this checkpoint.

### 2026-10-24: immediate cast offer queue

MayCast now snapshots its offered IDs into path-scoped interpreter memory and
advances the list only after an offer completes or is declined. A paused cost
prompt leaves the current card at the front. The consumed answer is cleared before
asking about the next card, and finished queue memory is removed. The existing
single-card casting implementation remains the authority for announcement/costs;
lasting permissions retain their existing complete-list path.

Both red regressions now pass with serialized replay after each offer
(`library-mill-cast-offers-fixed.log`). Full rules suite: 1,937 passed
(`library-cast-offers-rules.log`). Mixed accept/decline and multi-card additional-cost
prompt scenarios need dedicated coverage; existing whole-suite single-card cases
passing does not prove those combinations. Live acceptance remains pending.

API verification also passes: 840 tests (`library-cast-offers-api.log`). No commit.

### 2026-10-24: mixed offers and additional-cost selection

Expanded the immediate mill cast regression to seven variants: all accept/decline,
both alternating patterns, and accepted cards requiring one life plus a chosen
creature sacrifice. Four sacrificial creatures keep every accepted cast's selection
nontrivial. Every yes/no and cost prompt is serialized/replayed. Assertions cover
three offers, exact cast and payment-prompt counts, life spent, creatures consumed,
and one outer resolution. All seven pass (`library-mill-cast-mixed-tests.log`).
An initial fixture with only three creatures incorrectly expected a third payment
prompt; ObjectCosts.Select auto-selects when the legal set exactly equals the
required count. The fixture was adjusted, not that production behavior.
No production changes this checkpoint. Live immediate-cast acceptance remains.

### 2026-10-24: four-player immediate cast offers

Extended the shared mill-follow-up QA fixture/verifier with `--libraryMillCast=true`
and `--cast`. Three known scouts start on top of the actor's library. The browser
casts the fixture, answers seven replacement choices, then accepts/declines/accepts
the three immediate cast offers. It reloads before every answer, verifies opponent
choice redaction, three mill events, one outer resolution, and exactly two scout
spells on every seat's stack. All four seats reload with unchanged snapshots/event
counts. No refusals.

Passing log: `library-mill-cast-live.log`, table
`2e157b81-3cdd-47fd-b345-c20e780764af` (ten answers, three offers, two casts).
Inspected `tools/TableQa/library-mill-cast-seat-0.png`; both scout stack entries
are visible. This run stops with those spells on the stack and does not claim
their resolution or live additional-cost coverage. QA host parent 38052, child
19620 remains running. No production changes or commit in this checkpoint.

### 2026-10-24: live mill cast costs and final resolution

The shared fixture's `--libraryMillCastCosts=true` mode adds life plus chosen
creature sacrifice to each offered Scout and an extra sacrifice candidate.
`verify-library-mill-followup.js --costs` passes on four-player table
`9190fe23-d124-449d-a917-8a765cd22e24` (`library-mill-cast-costs-live.log`).
Twelve UI answers include three cast offers and two cost selections, with reload
before every answer and opponent option redaction. Accept/decline/accept spends
exactly two life and sacrifices two creatures. Both resulting Scout spells are
then resolved through normal priority passes: three resolutions total including
the outer spell, exactly two new Scouts on the battlefield, and no repeated mills.
All four final snapshots/event counts survive reload; no refusals.

Inspected `tools/TableQa/library-mill-cast-costs-seat-0.png`: life 38, two new Scouts,
empty stack. QA-only changes; no production card definitions or commit. Previous
host was stopped; current QA parent 48224 / child 54564 remains running.

### 2026-10-24: resource exhaustion between cast offers

Added a replay variant where each offered card costs twelve life plus a sacrifice.
The first cast succeeds from twenty life. Accepting either remaining offer refuses
under 119.4; reloading the committed choice and declining advances to the next card
and eventually finishes. Assertions preserve the first cast, eight remaining life,
one sacrifice/payment prompt and one outer resolution, with two refused offers.
All eight immediate-cast variants pass (`library-mill-cast-unpayable-tests.log`).
No production changes; this proves that an invalid later answer does not undo an
earlier accepted cast. QA host 48224/54564 was stopped before compilation. No commit.
# Saved immediate-cast offer compatibility — 2026-10-24

Full rules regression after both checkpoints: 1,947 passed, zero failed or
skipped (`library-legacy-checkpoints-rules.log`).

Follow-up: `library-mill-legacy-payment-tests.log` passes all 42 mill tests.
The additional checkpoint removes the queue from the serialized first card's
pending sacrifice-cost choice, then replays and answers it. Three offers and
three casts finish with exactly three life paid and three creatures sacrificed.
The test shares the serialized-field removal helper with the initial-offer
cases. Later queue positions are not claimed as legacy-compatible: the old
single-card implementation did not create those positions.

`library-mill-legacy-offer-tests.log`: all 41 `MillTriggerTests` pass.
Three added variants remove the offer-queue property from the serialized initial
`ChoiceRequested` event while retaining the remembered public mill arrivals.
Accept-all, decline-all, and acceptance with life/sacrifice costs each resume
through all three cards, replaying between answers with no repeated casts or
payments. This establishes the missing-queue fallback at the initial offer only;
it does not establish migration for arbitrary older replacement/payment logs or
older logs that already lost extra mill arrivals. No production change was needed.

# Independent properties beside retained sacrifice — 2026-10-24

Trigger ownership follow-up: `library-dependent-tap-trigger-tests.log` passes
all 11 dependency variants. The tap cases now install a land-tap trigger and
assert it is deferred at the serialized pause with a step-1 event origin.
Recovery produces no trigger for the reversed tap. When the retained library
operation untaps and retaps the same land, exactly its one tap trigger reaches
the stack. This distinguishes event-level cancellation from merely restoring
the final tapped flag. No additional production change was required.

API regression after the partition change: all 840 tests pass
(`library-dependent-properties-api.log`).

Retained-property follow-up: all 11 dependency variants pass in
`library-dependent-retained-tap-tests.log`. The added case has the retained
library operation untap and retap the independent land. Recovery preserves the
last tap despite identical before/after tap snapshots for that retained step.
This exercises the existing event-based restoration policy through the new
movement partition, with serialized pause and replay.

Full rules regression: 1,948 passed (`library-dependent-properties-rules.log`).

Reproduced a supported completed-step recovery failure in
`library-dependent-tap-red.log`: a sacrifice replacement gains life and taps an
independent land; a later mill replacement returns the sacrificed artifact to
the library and shuffles; the next mill cost cannot be paid. Answering the
serialized pending replacement refused recovery instead of restoring the land.

`PaymentMovementRecovery.Partition` now uses the residual properties already
validated by `Describe` to select reversible event origins. Property changes of
moved objects remain excluded; existing object/mana restoration still evaluates
retained-step dependencies. This removes the blanket rejection of independent
object properties without adding a second property validator.

All 181 `LibraryMillCostTests` pass (`library-dependent-properties-tests.log`).
The new regression verifies the land is tapped at the pause, untapped after
recovery, the sacrifice remains in the library, life is restored, and one
shuffle/recovery is emitted. Initial admission uses the existing internal test
entry; answering uses the normal game command. Public activation admission is
unchanged. No live acceptance is claimed for this change.
