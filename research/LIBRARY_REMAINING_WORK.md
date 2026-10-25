# Library remaining-work register

## Superseded by the user's scope correction

The user clarified that the task defines a deck and its basic interactions,
not keywords. DECK_SCOPE_CORRECTION.md now owns the active cleanup boundary.
The MTGO keyword research, payment/replacement expansion and F5 parity backlog
below are historical; do not continue them as required work for this goal.
Preserve existing tests and unrelated work while separating core operations
from external rules adapters. Nothing is committed.

## Bottom-order evidence and checklist reconciliation — 2026-10-24

Targeted public search surfaced the direct answer in the MTGO discussion
https://www.reddit.com/r/MTGO/comments/167u6hn : a respondent describes sequential
bottom placement with the last selected card becoming bottommost. This matches
our existing tested selected-order append contract. It remains a search-returned
user report, not a newly inspected full discussion or observed server outcome;
full-page retrieval previously failed. No expectation or production change follows.

A more discriminating primary recording lead is MTG Muddstah, MTGO Match-Up:
Grenzo, Dungeon Warden VS ???? EDH CMDR gameplay, February 22, 2016:
https://www.youtube.com/watch?v=od-JTIVaPm8 . Grenzo can expose the bottom card,
so a recorded multi-card scry followed by its activation could establish the
resulting order. The recording's metadata is verified; no qualifying sequence
has yet been observed. Do not implement named cards to research this reference.

F7's stale Not run label was reconciled against the existing current evidence
index and review. Full MTGO equivalence is still unproven (F5), not inferred from
passing gates. No code or tests changed and no commit was made.


## All-graveyard surveil completion — 2026-10-24

Reproduced an extra Confirm step after selecting every offered surveil card:
library-surveil-all-graveyard-red.log timed out awaiting direct completion.
ChoiceSelectionService.select now returns an answer at capacity only for the
full-set surveil contract (ordering, minimum zero, maximum equals offered count),
as well as its existing ordinary-search behavior. Partial selections, unusual
bounds and generic modes retain existing confirmation. All prior assertions,
including the nonordinary-surveil mode test, are unchanged. New regression covers
partial deselection/reselection and an independent submitted-array snapshot.

Full npm run verify: 1,919 tests, coverage and production build passed
(../mtg-client/library-surveil-completion-verify.log). Fresh four-player all-graveyard
table ca3b801c-894f-4c90-ab5a-5b6d73e5995c passed exact remaining library, both
graveyard arrivals, per-seat counts, private choices, deferred movement and reload
(library-surveil-all-graveyard-live.log). Existing partial-selection test passed
unchanged on aa6a7200-4780-4b47-92fb-f9d7c2d0f967
(library-surveil-partial-preserved-live.log). Partial-selection capture inspected.
No named card definitions or engine rules changed. Existing stylesheet warning
remains; no commit. Owned hosts stopped.


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


## Sequential top-placement action — 2026-10-24

Added library-top-order inspection action Put on top of your library. It delegates
to ChoiceSelectionService.placeOnTop, prepending each chosen option to the existing
top-first draft and submitting when complete. Existing explicit top-first controls
and command semantics are unchanged. Inspection labels show the draft position;
cards remain visible for correction, an intentional presentation difference from
MTGO's removal behavior. No named cards, rule algorithms or new command added.

New live assertion failed before implementation on missing menu action
(library-top-placement-red.log). Full npm run verify passed 1,917 tests, coverage
and production build (../mtg-client/library-top-placement-verify.log). Four-player
table b3eea15a-6789-481a-8539-59af030d2bcd passed exact order, local first placement,
last-placement completion, privacy, deferred movement and reload
(library-top-placement-live.log). Desktop and landscape draft captures inspected.
The existing card-modal style warning remains. No commit; owned hosts stopped.

Fixture input correction retained in library-top-placement-input-order.log:
inspection is alphabetically displayed, so clicking DOM positions did not always
choose original top then second card. The verifier now resolves those intended
instances through their authorized names, asserting uniqueness before clicking.
The exact expected arrays and all existing assertions were preserved; no expected
result was derived from the post-action order. Unit regression separately fixes
three-card placement, re-placement and unchanged explicit ordering semantics.


## Mixed graveyard/top instruction observed — 2026-10-24

Primary recording: https://www.youtube.com/watch?v=9JxX6vSPuk8&t=3451s
At 57:31-57:38 a graveyard-selection prompt remains with Prismatic Ending and
Thrilling Discovery in the temporary zone after Narcomoeba was selected.
At 57:42 the prompt changes to putting the remaining cards on top, by clicking
each card. At 57:45 Prismatic Ending has left and Thrilling Discovery remains.
At 57:58 a second Thrilling Discovery appears in hand after the draw. Thus this
recorded instruction has a distinct top-order phase and last-placed-first-drawn
behavior. The earlier 49:26 lead changes its decision at 49:33; it does not prove
two retained cards and should not be used as that evidence.

Current LibraryArrangementPlanner.Plan emits a library-top-order prompt when
more than one unselected card remains; its text explicitly says top card first.
LibraryArrangementCoordinator remembers that ordered answer and applies one
shared LibraryOrder.Arrange. ChoiceSelectionService.move supports correction.
This is phase correspondence with a different ordering gesture: explicit final
list order versus native sequential top placements. Do not reverse existing
expected arrays to fake parity. Any native-style placement affordance must map
to the existing top-first command contract and receive separate acceptance.

This reference predates Otherworldly Gaze's surveil keyword errata and proves
that recorded instruction, not every modern surveil producer or trigger timing.
Browser tab 7 (multiCardReference) is retained paused around 57:58.


## Public three-card graveyard instruction — 2026-10-24

Inspected primary recording Andrea Mengucci VEEDEO, Otherworldly Gaze is amazing
in Dredge | Modern | MTGO, January 12, 2022:
https://www.youtube.com/watch?v=9JxX6vSPuk8&t=289s
At 4:49 the temporary zone contains three inspected cards and the prompt offers
putting any number into the graveyard by clicking cards. Paused frames at 5:04,
5:06 and 5:08 show three, two and one remaining cards respectively. By roughly
5:10 the temporary zone and resolving instruction are gone. The creator states
he is clicking all three. No additional ordering prompt is visible in the
inspected all-graveyard sequence. This is pre-errata Otherworldly Gaze wording;
it establishes this instruction's presentation, not all current surveil producers.

Transcript identifies mixed placement at 15:21 and one graveyard/two retained
cards at 49:26. Those sequences still need frame inspection to establish how the
retained cards are ordered and whether completion is explicit. Browser tab 7,
variable multiCardReference, is paused around 5:10 and retained for continuation.
No production or test changes. Compare this concrete evidence with the shared
arrangement flow before adding a new control; do not implement the named card.


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


## Persistent library window stacking — 2026-10-24

The retained MTGO FloatingPanel.BringToFront reorders sibling z-indices. A new
live assertion reproduced our focus-only mismatch: after clicking the battlefield,
p2 covered the activated p0 window (library-panel-retained-red.log).
FloatingPanelStackService now owns bounded, retained presentation order, preserves
other windows' relative order, and unregisters destroyed windows. The directive
activates on focus; background primary presses focus the panel, while only the
Move handle captures a drag. Desktop CSS uses its rank; phone sheet layers remain
unchanged. No game-state or permission logic moved into presentation code.

Full npm run verify passed 1,916 tests, coverage and production build, exit 0:
../mtg-client/library-panel-stack-verify.log. Existing card-modal size warning
remains. Four-player table 91317592-bbd1-452c-9729-b8db6e93a027 passed the retained
order, background clicks, keyboard activation, unchanged view/events, disclosure,
shuffle expiry and reload assertions (library-panel-stack-live.log). Desktop
retained-order and phone-landscape captures were visually inspected. No commit.

Test correction, explicitly documented: the preceding overlap assertion wrongly
hardcoded p3 as the exposed opponent. A fresh fixture and screenshot exposed p1;
LibraryRevealComponent groups by incoming card-owner encounter order, not numeric
seat order. library-panel-fixture-assumption.log retains that failure. The test
now records the exposed opponent BEFORE clicking and requires that exact owner
at the overlap afterward. This preserves the behavioral contract and does not
derive an expected result from the post-action implementation output. Existing
own-window, no-state-change, responsive and lifetime expectations remain intact.


## Overlapping library activation — 2026-10-24

Reproduced before editing production: background click left p3 above p0 at
(250,250). The fixed assertion failed in library-panel-raise-red.log; retained
image tools/TableQa/library-panel-raise-before.png. FloatingPanelDirective now
allows programmatic focus and focuses a desktop panel on primary pointer-down.
Only its Move handle captures/drags; existing focus-within styling raises it.
No game or selection owner changed.

npm run verify: 1,915 tests passed, coverage and production build exit 0
(../mtg-client/library-panel-raise-verify.log). Four-player live table
cf48a86c-ac86-4ea4-bbbf-87b2f11a5f03 passed background activation for own/opponent,
keyboard focus, unchanged game view/events, all-owner disclosure, shuffle expiry
and all-seat reload (library-panel-raise-live.log). Both activation screenshots
were visually inspected. Existing assertions and geometry expectations retained;
new overlap fixture is separate. No commit.

This establishes activation while focused. MTGO's retained BringToFront also
reorders sibling z-indices; persistence of ordering after focus leaves the windows
is not yet equivalent to our focus-based CSS and remains a concrete next check.


## Recorded all-bottom scry interaction — 2026-10-24

Public primary recording: Andrea Mengucci VEEDEO, Sunday Murktide! Don't cut
Serum Visions, published February 13, 2022:
https://www.youtube.com/watch?v=oOAfDKpCbQo&t=3934s
Inspected paused frames around 1:05:34-1:05:37. Serum Visions is resolving with
Lightning Bolt and Consider in the library inspection. The Lightning Bolt menu
shows top/bottom and the pointer selects bottom; the card leaves the inspection.
Consider then shows the same menu with the pointer at bottom. At 1:05:37 the
inspection is gone and the prompt is precombat main phase. The creator says he
is bottoming both. No intervening bottom-order confirmation is visible.

This supplies recorded correspondence for two per-card bottom choices followed
by direct completion, matching the existing local all-bottom live runs. It does
not expose the final relative order of those hidden cards. That ordering remains
locally specified/tested as selection order, not proven MTGO server equivalence.
The remaining question is hidden resulting order, not whether MTGO requires a
separate all-bottom ordering prompt.

The Reddit reference reached a CAPTCHA in the browser; no challenge was solved
or bypassed. Search excerpts remain corroborative leads only. No production or
acceptance-expectation changes. The gameplay reference tab is retained for
continuation. No commit.

## Native library panel template established — 2026-10-24

Used the already-installed ILSpy 11.1.0.9782 against retained SharedResources.dll
(no target execution, download or new tooling). Reconstructed FloatingPanel.cs
and project resources under ignored decompiled/shared-window-audit. This is
ordinary ILSpy reconstruction, not a new REA Evidence object. Artifact and output
SHA-256 values are retained in library-window-reconstruction-hashes.json.

The complete library route is Overlays.AddCardSelectorsAsChildren: it creates a
FloatingPanel without setting CloseButtonCommand or DockButtonCommand. The panel
XAML (Shiny.Controls/FloatingPanel.xaml:2) defaults to
FloatingPanelTemplateWithNoClose. That template at
sharedstyles/floatingpaneltemplates.xaml:110 has a header, MainContent and resize
handle, with no close, dock or collapse control. FloatingPanel.UpdateTemplate
(305) chooses close/dock only for non-null corresponding commands.
UserControlMouseDown (577) calls BringToFront and starts move/resize for the
header/handle; no collapse gesture is implemented in that path. BringToFront
(597) changes sibling z-order.

Verdict: the previously unknown collapse control is resolved for this retained
library-selector route/build. Local details/summary collapse is an intentional
presentation convenience, with local tests/live evidence that it preserves the
pending decision and emits no game mutation. It must not be claimed as copied
MTGO behavior or universal superiority. This does not describe unrelated native
zone windows or later client builds. Compare local focus/raise behavior separately
before claiming equal multiwindow usability. Bottom-placement observation remains
unverified beyond the public report excerpt. No production edit or commit.

## Independent library window roles implemented — 2026-10-24

LibraryRevealComponent now routes local ownership to library and other ownership
to opponent-library; missing viewer/owner identity opts out of persistence.
FloatingPanelDirective selects one of two UiPreferencesService geometry fields
through one preferenceKey path. Movement, clamping and serialization normalization
remain shared. No player/card IDs are persisted as geometry keys.

The fixed directive regression first failed (library-role-geometry-red.log:
1 failed, 5 passed); production changes then pass 21 focused existing/new tests.
Optional opponentLibraryPanel is included in version-1 layout export only when
present. Old own-only files preserve opponent settings. Malformed opponent
geometry refuses the whole import before mutation. All earlier exact export
expectations remain unchanged; added round-trip/atomicity coverage.

Full npm run verify passes 1,914 tests, coverage and production build in sibling
library-role-geometry-verify.log; existing card-modal style warning. Four-seat
all-owner disclosure with --role-geometry passes table
0d04e91e-0c75-4b12-b1b9-b20055992f76, library-role-geometry-live.log. Every viewer
restores own (20,80,300,200) and opponent (500,80,300,200) rectangles after reload;
existing card membership, collapse reachability, four-shuffle expiry, history
and final-reload assertions are preserved. Desktop and landscape inspected.
Owned host stopped. No commit. This matches the recovered own/opponent settings
routing; it does not prove the exact native collapse gesture or per-opponent
independent settings (the retained source uses a shared opponent role).

## Separate local/opponent window settings: concrete gap — 2026-10-24

Retained client trace establishes more than generic window support:
DuelSceneViewModel.cs:1385/1401 creates separate YourLibraryWindowSettings and
OpponentsLibraryWindowSettings. Its actual AddSelector calls at 5793 and 5798
pass those separate objects for local and other-player library zones.
CardSelectorManager.AddSelector stores the supplied settings (59 onward),
CreateVisual passes them to CardSelectorViewModel (142), and
Overlays.AddCardSelectorsAsChildren binds WindowSettings to FloatingPanel's
TemporaryWindowSettings (188 onward). This is a demonstrated client routing
boundary. FloatingPanel implementation/template is not retained; exact collapse
control behavior remains unknown.

Current local LibraryRevealComponent binds rememberPanel="library" for every
owner; FloatingPanelDirective reads and writes UiPreferencesService.libraryPanel
for that key. Local and opponent geometry therefore share one saved rectangle.
This is a concrete gap to fix, not a claim inferred from an unavailable video.
Preserve the existing fixed version-1 own-library export/import expectations;
add independent opponent geometry coverage without weakening those contracts.
No private card identity should become a persisted geometry key. Separate roles
are sufficient to match the recovered routing; per-opponent MTGO independence
is not demonstrated by this evidence.

Public bottom-order search still yields only the previously recorded player
report excerpt; the full Reddit page was unavailable. No stronger verdict is
claimed from that repeated search. No production changes in this audit.

## Imported layout reaches game inspection — 2026-10-24

Extended the fixed layout browser contract through an authorized manual look
and navigation to the actual game. The unchanged expected rectangle is left 20,
top 30, width 400, height 300. The actual inspection must match each dimension
within one pixel. File/control expectations remain unchanged.

library-layout-panel-final-live.log passes on fresh table
fe35e4a6-69b1-4e25-b1e8-e564b74f63ee. Inspected the visible game panel in
layout-acceptance-isNEk9/imported-game-panel.png. The earlier geometry run passed
but its screenshot was obscured by the intro; the harness now dismisses/waits for
that overlay before capturing, preserving assertions and earlier observations.
This closes the import-to-panel integration gap. Only QA changed after the
1,912-test full client verification; no new production build is claimed.
Owned hosts stopped. No commit or claim of MTGO file-format compatibility.

## Library layout file controls accepted — 2026-10-24

Settings now exposes library-only export and file import through the existing
UiPreferencesService contract. File reads have busy state, a 64 KiB bound, error
feedback and input reset for retry. Export downloads versioned geometry JSON;
unrelated preferences and game/card data are excluded. Existing acceptance
expectations were preserved. Added UI file-import/rejection and download tests.

Full npm run verify passes 1,912 tests, coverage and production build in sibling
library-layout-ui-final-verify.log (exit 0). Existing card-modal style warning.
verify-library-layout.js checks fixed literal geometry against actual upload,
downloaded JSON and local storage, then rejects malformed JSON without mutation.
The initial capture exposed an inline label collision; production spacing was
fixed and an additional separate-row assertion added, without weakening file
expectations. Final run library-layout-ui-final-live.log passes. Inspected all
three captures in tools/TableQa/layout-acceptance-thUKsZ; controls and error are
visible and do not overflow. Earlier observations remain in layout-acceptance-CVUeDc.

This proves file/control behavior; imported geometry's actual application to a
new game inspection still needs a connected UI assertion. Existing panel tests
cover preference restoration, but that is not the whole import-to-panel flow.
Owned hosts stopped. No commit. No claim of MTGO file-format compatibility.

## Library layout contract implemented — 2026-10-24

Defined fixed acceptance cases before implementation in UiPreferencesService
specs: exact version-1 geometry-only export, import preserving unrelated settings,
null/default geometry, malformed JSON and shape/version/numeric rejection without
state or storage mutation, and removal of unrelated/private payload fields.
The first run fails compilation because importLibraryLayout/exportLibraryLayout
are absent (sibling library-layout-contract-red.log); this is a missing-API
checkpoint, not a behavioral red test. The existing preferences owner now
implements both methods using its shared panel normalization. The unchanged
expectations pass all 15 focused preference tests in library-layout-contract-green.log.
Only formatting changed in the test source after the initial contract.

This is the serialization boundary, not a finished user-facing feature. Settings
controls, file handling, rendered round-trip/malformed-file acceptance and full
npm run verify remain required. No UI completion or fresh full-suite claim.
No commit. Existing layout geometry clamping remains owned by FloatingPanelDirective.

## Window capability reconciliation — 2026-10-24

Rechecked the official gameplay guide:
https://www.mtgo.com/getting-started/getting-started-gameplay
The resize/pop-out section explicitly describes settings export/import. Current
FloatingPanelDirective saves library geometry through UiPreferencesService;
SettingsComponent has no layout import/export path. This is a concrete missing
capability, not an unknown MTGO gesture. Any counterpart must stay library-scoped,
use the existing preferences owner, validate imported geometry, exclude private
card/game data and be verified by round-trip UI plus malformed-input tests.
The guide does not establish exact multiwindow collapse behavior. That remains
unverified; generic graveyard collapse documentation is not library evidence.

## Scry draft presentation accepted — 2026-10-24

ChoiceSelectionService.scryDraft projects existing destination decisions into
current-choice, offered-card labels. GameBoard passes that projection to
LibraryRevealComponent; the component adds a selected outline and Top chosen /
Bottom chosen label with tap-to-change guidance. There is no second destination
registry or engine movement. This intentionally retains visible correction
access instead of copying MTGO's removal of the decided card. It resolves the
reproduced ambiguity, not exact display parity. Regression covers correction,
choice identity, option withdrawal and clearing on ordinary selection.

npm run verify passes 1,900 tests, coverage and production build in sibling
library-scry-draft-presentation-verify.log. Existing card-modal style warning.
Four-player all-top table 37a7ee0c-ead8-4f72-9294-834dbbc02d4c and all-bottom table
94929cad-150c-46de-8798-6975b8008f37 pass the visible first-decision assertion,
exact final orders, private views, deferred movement and reload checks; logs are
library-scry-draft-presentation-live.log and library-scry-draft-bottom-live.log.
Desktop/landscape top captures and landscape bottom capture were inspected.
Current library-scry-first-decision captures contain the later bottom scenario.
Synthetic cards have no artwork. Owned hosts stopped. No commit.

## Scry decided-card presentation reproduced — 2026-10-24

Extended the existing four-player ordering harness to capture desktop and
landscape immediately after the first per-card destination, before submitting
the complete answer. The current-build all-top run passes on table
454f7eba-8ebe-4e78-a990-850648015b4d (library-scry-first-decision-probe.log).
The probe counts two inspection cards after the first decision; the inspected
landscape image shows both Island and Quiet Demise under Available in current
prompt with no indication that one already has a destination. This reproduces
the presentation gap instead of inferring it solely from source. Exact final
order, private views, deferred movement and reload still pass. Host stopped.

The next production change must project the shared ChoiceSelectionService draft
into the inspection presentation, scoped to the current choice, distinguish
undecided from decided cards, and preserve correction/rejection/retry behavior.
Do not create a second component-owned destination registry or change the server
library before the complete answer. The observed MTGO recording removes decided
cards from its actionable list; any local correction presentation must be stated
as a deliberate difference, not claimed identical. No production UI fix yet.

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

## Complete scry order contract implemented — 2026-10-24

AnswerChoice/AnswerChoiceDto and ChoiceAnswered now carry optional TopOrder option
IDs. Null retains the previous partial-answer/top-order continuation; an explicit
empty list is a complete empty top partition. LibraryArrangementAnswer validates
that top is exactly the complement of the validated bottom selection, without
duplicates, unknown IDs or overlap, and only accepts it for scry. Validation occurs
before ChoiceAnswered. Resumption passes resolved object IDs into the existing
LibraryArrangementCoordinator and persisted participant memory. LibraryOrder
remains the only ordering implementation. Old pending choices remain answerable.

Client ChoiceSelectionService retains placement sequence, sends top IDs in reverse
placement order, and preserves bottom order. Existing NgRx/SignalR/engine transport
carries the optional payload under the current choice ID; no heuristic second
answer to a later prompt is used. Complete menu choices resolve without another
top-order prompt. Ordinary select/confirm callers retain the existing continuation.

Verification: library-complete-scry-rules.log (11 focused tests),
library-complete-scry-full-rules.log (2,100 tests), library-complete-scry-api.log
(844 tests), ../mtg-client/library-scry-complete-order-verify.log (1,899 tests,
coverage and production build), all exit 0. Existing card-modal style warning.
Regressions cover one-answer completion, rejected malformed partitions, unchanged
pending state on rejection, event serialization/replay and opponent inspection.

Four-player all-top: 4605fe81-b6e5-40de-9d0c-6eced75e19e5,
library-scry-complete-top-live.log. All-bottom: 7fe39e67-1143-4194-8b57-45a992895432,
library-scry-complete-bottom-live.log. Both verify exact hidden order, deferred
movement, privacy, per-seat and final reload; all-top has no extra prompt and
all-bottom exercises explicit empty TopOrder. Landscape capture inspected; owned
hosts stopped. No named cards or commits. The earlier mixed-destination run
predates the optional complete payload; its final integrated rerun remains useful.
MTGO bottom placement order and multiple-window collapse remain comparison limits.

## Same-top scry sequence observed — 2026-10-24

Reference: https://www.youtube.com/watch?v=oOAfDKpCbQo&t=4992s
Andrea Mengucci VEEDEO, Sunday Murktide! Don't cut Serum Visions (Feb 13, 2022).
Paused frames at 1:23:12 show Dragon's Rage Channeler and Expressive Iteration.
Around 1:23:14–15 the Channeler menu's top action is selected; the card leaves
the actionable list. Around 1:23:16–17 the Iteration top action is selected.
Six half-second frame-step observations across that interval show the menu
closing, the remaining card leaving, and return to the main phase, without a
separate ordering prompt. At 1:24:06 Iteration is present as the newly drawn hand
card. This supports last-top-action-first-draw for this recorded case. It does
not establish bottom placement sequence or private intermediate wire messages.

Concrete remaining divergence: local scryDestination records only a set of
resolved decisions plus bottom pick order. All-top therefore submits an empty
selection and LibraryArrangementPlanner asks LibraryTopOrder. The existing
four-seat all-top test demonstrates that extra step, not equivalence to MTGO.

Inspected LibraryArrangementCoordinator.Execute: all players' selected and top
lists are collected through one continuation before movement; LibraryOrder.Arrange
is the single movement-order owner. LibraryArrangementContinuation persists those
lists under operation/player keys. Preserve these boundaries when eliminating
the extra prompt. The next implementation must retain top decision order (reverse
placement order for this observed flow) and transport/validate it as part of the
same current choice. Do not automatically answer an unrelated later prompt based
only on matching card IDs, and do not build a second scry movement algorithm.
The existing explicit top-order path remains necessary for callers without a
complete per-card destination draft and for saved pending choices.

No production edit or new tests in this observation pass. The initial menu feature
remains verified at 1,898 client tests and three four-seat scenarios; complete
same-top interaction parity is now a concrete implementation task, not unknown.

## Scry destination menus implemented — 2026-10-24

LibraryRevealComponent now offers top/bottom scry actions only for an authorized
active card with one current offered option. Pending/reposition busy and stale
inspection/choice guards remain in the existing component. The new output delegates
to GameBoard and ChoiceSelectionService; no manual movement or new engine exists.
The shared service records local per-card destinations, submits the bottom list
when every offered card is decided, and leaves remaining-top ordering to the
existing server continuation. Reset/new choice and ordinary prompt selection clear
the menu draft; same-choice withdrawal removes obsolete destination decisions.

Full initial client verify passes 1,897 tests, coverage and production build in
../mtg-client/library-scry-menu-verify.log. Final verification, including the withdrawal guard, passes 1,898 tests,
coverage and production build in library-scry-menu-final-verify.log (exit 0).

Four-seat live runs pass:
- Mixed: 0d2d8913-a433-4465-8994-150deeb418f0, library-scry-menu-mixed-live.log.
- All top then explicit reversed top order: 417889ab-5345-44f5-91eb-058f76134da4,
  library-scry-menu-top-live.log.
- All bottom in offered order: 087800f8-00bf-4906-ab98-26166ca86a87,
  library-scry-menu-bottom-live.log.

The first per-card decision emits no game update. Each run verifies exact final
hidden order, deferred movement, viewer privacy and all-seat reload. Desktop and
667x375 landscape menu captures inspected; hosts stopped. The live runs predate
only the same-choice withdrawal guard. No named card added and no commit.
This closes the missing scry destination-menu capability. Exact MTGO ordering for
multiple cards assigned to the same destination remains unobserved; local ordering
is explicit and tested, not claimed superior or completely equivalent.

## Public two-card scry menu gap — 2026-10-24

Andrea Mengucci VEEDEO, Sunday Murktide! Don't cut Serum Visions | Modern | MTGO,
February 13, 2022 (visible description),
https://www.youtube.com/watch?v=oOAfDKpCbQo&t=2267s

Actual paused frames inspected at 37:47, 37:52 and 37:57. At 37:47 the Serum
Visions resolution shows two library cards and a scry-two prompt instructing a
top/bottom decision for each card. At 37:52 Lightning Bolt has a context menu
with Put on top of your library and Put on the bottom of your library. At
37:57 the inspection has closed and ordinary main-phase play has resumed.
The first card's destination and same-destination relative ordering were not
established by these sampled frames; do not infer them or intermediate wire calls.

Concrete local gap: LibraryRevealComponent only offers choice destination menus
for single-card surveil. Scry currently uses the shared select/confirm ordering
prompt without corresponding per-card destination menu actions. Manual top/bottom
reposition is a separate permission and must not be reused to mutate a resolving
scry. Existing engine scry/ordering behavior is not thereby disproven.

Next implementation: introduce shared draft destination decisions for offered
scry cards, exposed through the existing inspection menu and selection owner.
Collect bottom choices in their explicit order and retain the existing remaining-
top ordering continuation. Preserve choice identity, cancellation/retry, stale
membership, pending guards and privacy. Verify multi-card all-top/all-bottom and
mixed destinations. Inspect further frames for same-destination ordering before
claiming matching ordering semantics; do not duplicate the rules coordinator or
implement Serum Visions as a named-card task.

## Public unrestricted tutor observation — 2026-10-24

CalebDMTG, Freest Demonic Tutor of ALL TIME | Vintage Cube | MTGO,
published June 14, 2024 (visible video description):
https://www.youtube.com/watch?v=gvkgh2HPWbk&t=1880s

Inspected actual paused browser frames at 31:20, 31:24, 31:29 and around
31:30, using frame stepping to narrow the selection/closure interval. The
library overlay and prompt offer a search for a card, with no stated card-type
restriction. A card row highlights around 31:30; the pointer leaves the list;
the overlay then closes and the main-phase prompt returns with the selected
card arriving in hand. No separate Done/Confirm click or confirmation dialog
is visible in the inspected transition. Transcript text located the sequence;
the interaction conclusion comes from the displayed video frames.

Verdict: bounded correspondence for direct completion of the recorded
unqualified single-card tutor. ChoiceSelectionService already submits an offered
single option directly, irrespective of the search filter; mandatory minimum
validation remains server-owned. The unqualified refusal/replay regression and
full 2,095-test rules pass establish the local rules side, not MTGO wire behavior.
No production edit is indicated by this observation. This does not establish
all context-menu variants, current MTGO server internals or every tutor effect.

This supersedes unqualified single-card completion as an entirely unobserved
scenario in older entries. Multi-card ordering menus and multiple-window collapse
remain specific public observation targets. No new test run or commit this slice.

2026-10-24 action applicability audit: the current MTGO method register now maps
seven concrete local library action families to their enablement and submission
owners. Retained GameCard.ResolveCardActionsHelper was inspected directly at
3409–3595. Generic modal/casting/tap/target flags do not establish a missing library
feature. Remaining observation targets are specifically unqualified-search
click/menu/confirmation, multi-card ordering menus and multiwindow collapse.
No speculative descriptor API or named-card implementation added. This is a
source comparison update, not a new live test or full-parity claim.

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

2026-10-24 unqualified-search validation audit: the previous test only asserted
pending bounds. A new regression now serializes/replays a real unqualified search,
passes an empty answer through ChoiceSystem and verifies refusal with unchanged
pending/library/hand, then accepts the offered card and verifies movement using
the new zone identity, inspection expiry and a single ChoiceAnswered event.
Both focused unqualified tests pass in library-unqualified-replay.log. This is
published-rule/server validation evidence (CR 701.23d), not observed MTGO UI parity.
No production engine change. The initial assertion incorrectly expected identity
preservation across zones; corrected to check ObjectMoved.From and To.Id.

The older sibling e2e/verify-library-multi-search-live.js now expects automatic
inspection opening, tests deselection before reaching capacity and completes on
the final pick without Confirm. Node syntax check passes; this named-card/live-API
harness has NOT been rerun. The previous generic four-seat capacity and partial
runs remain the live evidence. README's stale explicit-confirmation claim corrected.


2026-10-24 multi-search capacity implementation verified: unordered, nondivision
searches submit automatically at maximum selection, matching the public Titan
recording. Legal partial selections retain Confirm. Remaining-pick count and
selection share one owner; pending answers cannot duplicate submission.
Full npm run verify: 1,893 tests, coverage, production build; existing card-modal
stylesheet budget warning only. Log: ../mtg-client/library-search-capacity-layout-verify.log.
Four-seat automatic completion: f6d64c2f-0204-489c-8f26-2bb6149eb769,
library-search-capacity-layout-live.log. Partial one-of-two completion:
c5eca90f-a67f-4599-ab02-21cbe0d57ba4, library-search-partial-capacity-live.log.
Both pass exact arrivals, held single submission and all-seat reload. Corrected
landscape and desktop captures inspected; initial filter overlap fixed by grouping
the search header. QA hosts stopped. No commit. This supersedes the pending
implementation statement below; broader comparison and integrated audit remain.


2026-10-24 multi-search public reference found: d00mwake's Primeval Titan recording
(https://www.youtube.com/watch?v=ZfNenHYTVYk, 24:48–25:08) shows remaining picks
decreasing from two to one, then the second pick completing without a separate
Done click. The decisive 25:03–25:04 interval was inspected frame by frame.
Local ChoiceSelectionService still requires Confirm for every multi-card search.
Next implementation is capacity-based submission for unordered/nondivision search,
while keeping legal partial completion and explicit ordering. The detailed source
discriminator is in mtgo/LIBRARY_CURRENT_METHOD_REGISTER.md. No new code/test claim;
this is evidence that changes the next implementation action, not a blocker.

2026-10-24 required-method reconciliation: mtgo/LIBRARY_CURRENT_METHOD_REGISTER.md
now consolidates all seven method rows explicitly named in the plan, plus library
window methods, against current inspected source. It records input/ownership,
visibility, validation/submission and failure differences, with a verification
index. This removes reliance on stale historical claims of unconditional search
confirmation or missing auto-open/menu behavior. Broader descriptor, multi-card
reference and exact window behavior remain unverified; no full-parity or goal
completion claim is made. No production change or new test run in this audit.

2026-10-24 multi-owner auto-open acceptance: a generic QA Reveal of each player's
top two cards creates four owner panels in every viewer. Four-player table
00fa65f7-513c-447e-9770-0db8f7b6bc13 passes reaching and collapsing all panels at
desktop/landscape, owner separation and two-card counts, pending-choice stability,
four shuffle expirations, retained public history and all-seat reloads. Log:
library-all-disclosure-live.log, exit 0. Owner 3 desktop and owner 0 landscape
captures inspected. Host stopped. No production change or new full-suite claim;
this validates the preceding auto-open implementation beyond a single owner.

2026-10-24 active-inspection opening gap fixed: authorized new library panels now
open automatically, matching the recovered delayed-selector creation and recorded
search/surveil presentation. Manual collapse survives routine updates; historical
panels remain collapsed; revocation removes the panel and a new inspection opens
again. Shared state/permission ownership is unchanged. Full client verify passes
1,887 tests and production build in library-inspection-auto-open-verify.log.

Four-seat search and surveil runs pass on 2dcf827b-68bf-4a6c-bee6-98861ad40777 and
153f4e69-1b68-491d-9584-7d7c9ace926b (library-auto-open-{search,surveil}-live.log).
They verify automatic opening, search selection retention during collapse/reopen,
menu completion, privacy, exact result and reload. Desktop/landscape captures
inspected; hosts stopped; no commit. This is new implementation progress, not a
claim that the broader MTGO descriptors or final integrated review are complete.

2026-10-24 pending-search lifecycle verified: new --search-panel-probe in the
existing four-seat search harness preserves two selected physical cards and the
pending choice across desktop/landscape collapse and reopen without game events.
It then completes normal search, tapped arrivals, privacy and final reload.
Table 0ffc40cd-5a52-4b45-8277-a3198e55ac9a, library-search-panel-choice-live.log,
exit 0. Reopened landscape capture inspected; host stopped. No production edit,
commit or new full-suite run. The MTGO register now separates the disabled generic
close command from the actual floating library overlay; exact MTGO collapse/reopen
interaction is still unknown, not falsely equated with cancel/fail-to-find.

2026-10-24 single-surveil menu complete: the public MTGO recording supplies the
top/graveyard interaction, and current CR 701.25a supplies the rules. The client
now delegates those two one-card actions through existing pick/skip handlers,
with shared current-card lookup and pending/stale/inspection-expiry guards.
No rule coordinator or named card implementation was added. Multi-card ordering
is unchanged. No user-provided MTGO recording is required for this finding.

Full client verify passes 1,886 tests, coverage and production build in
../mtg-client/library-surveil-menu-verify.log (existing stylesheet warning).
Four-player graveyard and top menu runs pass respectively on
fc17830c-90cc-4c0d-8921-ab596fcae3f5 and 1f990514-2ec9-4ead-b38a-1ec88dbfcd39,
library-single-surveil-graveyard-live.log and library-single-surveil-top-live.log.
They check exact library order, private inspection, active-seat-only options,
deferred movement, graveyard contents and every seat's reload. Desktop/landscape
captures inspected; test hosts stopped. No commit. Broader F5 descriptor cases
and F7 final review remain distinct from these demonstrated flows.

2026-10-24 search-policy scope correction: the first shared predicate also made
single-card non-search ordering require confirmation. The unchanged Commander
opening harness exposed that extra step. The policy is now scoped to preserve
the prior immediate behavior for one-card mulligan bottoming; ordered/division
searches retain explicit confirmation. A new unit regression protects this
boundary. The temporary opening-harness confirmation change was removed.

Full npm run verify passes 1,884 tests, coverage and production build, exit 0 in
client library-direct-search-scoped-verify.log (existing style warning). The
original two-player opening harness passes against that build on
93a79aa8-26b0-4134-811d-061e17148ddb, library-direct-search-scoped-opening.log:
Commander counts, paid bottoming after reload, privacy, kept hands and first
draw skip. Bottom-choice landscape capture inspected; host stopped. Earlier
explicit-bottom-confirmation trial results do not describe the final client.
No commit.

2026-10-24 empty-search follow-up: four seats pass the no-match qualified search
on 7ae92f49-be38-49e2-8533-923ec77b9a5a (library-direct-search-empty-live.log,
exit 0). The current client offers enabled Choose none with zero options,
completes search without adding a card to hand, clears inspection and reloads
all seats. Landscape capture inspected. This verifies the no-match completion
control after removal of unconditional Confirm; it is distinct from choosing
zero while matches exist. No production changes or new full-suite run. Host
stopped, no commit. The current method matrix now records the implemented direct
single-card path instead of calling all search confirmations an outstanding gap.

2026-10-24 post-change search protections: the combined four-seat pointer and
option-withdrawal run passes on bd89f443-a212-497b-8856-cd44bc4fa4c1
(library-search-protection-live-final.log, exit 0). It rejects left/right drags,
seven-pixel and diagonal movement and a 600-ms hold; Enter and emulated landscape
touch select normally. Simulated same-ID option withdrawal clears a stale pick
without sending a command, for required/optional choices at desktop/landscape.
Two-card selection then completes with both private identities absent from every
opponent projection and all seats reload. Touch and required-withdrawal landscape
captures inspected. This remains simulated view withdrawal, not a naturally
generated rule event or physical-device evidence.

The first run reached final hand validation but failed because the QA harness
still assumed one received card. It now derives the count from the requested
search quantity and checks privacy/filter membership for every received card.
No production changes since the 1,883-test verify checkpoint. Owned host stopped;
no commit.

## Direct single-card search implemented — 2026-10-24

ChoiceSelectionService now owns one requiresConfirmation predicate used by both
selection and ChoiceControls. An offered single-card, unordered/nondivision
search returns an immediate answer through the existing board/SignalR owner.
Multi-card choices and ordered/division searches retain explicit confirmation.
Single-card non-search choices preserve their existing immediate behavior. Pending
single-search submissions retain only the submitted disabled option; zero-card
answers use Choose none. No new transport protocol or mutable library state.

The direct-search regression failed before the change. The new pending-option
test also caught a mulligan-only filter, which was corrected. Full npm run verify
passes 1,883 tests, coverage and production build, exit 0 in client
library-direct-search-verify.log; existing card-modal style budget warning remains.
Four-seat runs pass direct submission (library-direct-search-live.log,
f47fda78-e75a-49b5-b4f9-aff0bc152b88), two-card explicit confirmation
(library-direct-search-multi-live.log, 5fbe1696-eab1-49e6-922b-9d7475a0eb27),
and intentional zero picks with matches (library-direct-search-skip-live.log,
81a23101-bb5a-44d6-a307-521ec929ae5b). They include pending reload, authorized
inspection/privacy, result broadcasts and all-seat final reload. Direct pending
desktop/landscape, multi-card landscape and zero-pick pending landscape captures
were inspected. All owned hosts stopped. No commit.

This closes the extra confirmation step demonstrated by the recorded qualified
single-card search. It does not prove all MTGO conditional action descriptors,
private server algorithms, or all library search variants equivalent.

## Concrete MTGO search reference found — 2026-10-24

The browser-accessible BoshNRoll tutorial demonstrates a Misty Rainforest search
at 27:06–27:13: clicking Volcanic Island completes selection without a separate
visible confirmation step. See the new recorded-search entry in
mtgo/LIBRARY_METHOD_PARITY.md for the source, observation and limits.
Our maxPicks=1 search currently retains the pick and requires Confirm. This gives
F5 a concrete implementation target; missing reference evidence no longer blocks
that flow. Multi-card/ordered searches and private descriptor fields remain
separate comparisons. No code or verification result changed in this research pass.

## Current reconciliation — 2026-10-24

The full rules suite now passes 2,094 tests, zero failed/skipped, exit 0 in
library-final-rules.log. This includes the opponent qualified fail-to-find and
four mill departure/return regressions added after the 2,089-test checkpoint.
No production code changed in this reconciliation. The API (844) and client
verify (1,881) results below remain separate earlier checkpoints, not reruns.

F1 and F2 in the finish table now reflect their recorded bounded decisions
instead of superseded unresolved questions. The official Comprehensive Rules
were rechecked, including 603.2f, 400.7e and 701.17c; no special hypothetical
card ruling is needed before proceeding. F5 still distinguishes the actual
conditional-action contract gap from unavailable scenario payload evidence.
The official MTGO gameplay guide does not specify those payloads. The method
matrix now correctly records implemented browser-local geometry persistence.
Continue final requirement reconciliation without claiming exact MTGO parity,
superiority, or coverage of arbitrary unsupported replacement instructions.
F7 user review remains outstanding; no commit is authorized or made.

## Rules-derived decision after renewed research — 2026-10-24

For an unrevealed mill departure redirected to hand, retain post-event
characteristic matching, without passing the hidden arrival as a follow-up card
subject. This is an implementation inference from the general rules, not a
card-specific published ruling: CR 603.2/603.10 govern event matching; 603.2f
restricts the trigger source's visibility. CR 400.2 defines hidden zones without
removing characteristics. CR 400.7e and 701.17c restrict subsequently finding the
new object; 609.1 distinguishes an ability's effect from its trigger condition.
The undefined-characteristics exception in 701.9c explicitly concerns discard.
Source: https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf

This resolves the implementation decision previously left waiting for an exact
hypothetical ruling. Existing Creature/Artifact cost/effect tests exercise the
chosen distinction, replay and privacy (42 focused cases passed). No production
change is necessary. Do not claim an explicit Wizards ruling or MTGO runtime
observation. Remaining F2 cases must still be audited separately; this decision
does not certify arbitrary replacement chains. Research continues on MTGO's
scenario-specific confirmation behavior without blocking independent work.

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

Latest F5 geometry checkpoint: authorized library inspections are movable and
resizable on desktop, with keyboard movement and viewport bounds; phone landscape
keeps the fixed overlay. Full client verify passes 1,878 tests plus coverage/build
(library-floating-verify-checked.log). Four-seat drag/resize/keyboard, privacy,
expiry and reload pass library-floating-live.log; both captures inspected.
This supersedes the blanket fixed-versus-movable geometry gap below. Conditional
action confirmation, unresolved rule boundaries and final acceptance remain open;
saved MTGO window positions/full reopening equivalence are not established.

Latest F5 implementation: library search/opening prompts retain the submitted
disabled control and hide alternative actions while sending. Local submission
metadata is held by the existing request owner and never enters engine commands.
Client verify passes 1,875 tests plus coverage/build (exit 0); four-player Confirm,
Choose none and mulligan flows pass, with pending captures inspected. See the
top of LIBRARY_ACCEPTANCE_AUDIT.md for logs, tables and probe limitations.
This closes the bounded pending-alternatives difference, not conditional action
confirmation, movable inspection windows, F1/F2 or final integrated acceptance.

Rendered same-ID search withdrawal is now verified using a clearly labeled
simulated incoming view: desktop and phone landscape discard the withdrawn pick
and distinguish required versus optional empty confirmation. Normal four-seat
search completion/reload follows successfully; captures inspected. Evidence:
library-search-withdrawal-live.log, table a759c5c7-2c2e-412a-a654-d2b52ff6e6df.
This supersedes the live-board verification gap in the checkpoint below, but is
not evidence of a rule-generated withdrawal. Remaining F1/F2/F5/F7 work persists.

Latest implementation checkpoint: withdrawn search picks are removed on rebind
and rejected by canSubmit against current offered IDs. A focused regression
reproduced both failures before the shared selection fix. Client npm run verify
passes 1,872 tests, coverage gates and production build, exit 0
(library-choice-membership-verify-final.log); existing 18.05 kB card-modal style
budget warning remains. Four-seat normal search/reload passed and its pending
desktop capture was inspected (library-choice-membership-live.log). Same-ID
withdrawal itself is service-tested, not yet live-board verified. Details are in
LIBRARY_ACCEPTANCE_AUDIT.md. No commit; pending-prompt presentation remains open.

## Web evidence update — 2026-10-24

Subsequent verification: six hidden-hand mill cases now check opponent privacy at
every replay event through movement and trigger resolution. Full rules gate:
2,085 passed, zero failed/skipped (library-hand-prefix-rules.log, exit 0).
Only test assertions changed; F1/F2/F5 limits and final review remain open.

Official MTGO gameplay/shortcut documentation now supports the F5 presentation
comparison and the F1 draw/mill undo boundary. Sources and limits are recorded
at the top of LIBRARY_METHOD_PARITY.md (under mtgo/) and
LIBRARY_RESUMED_FAILURE_CONTRACT.md. F2's synthetic unrevealed hidden-arrival
question remains unproven by primary sources; see LIBRARY_HIDDEN_MILL_RULES.md.
Unknown private MTGO payloads are comparison limits, not a prerequisite for
continuing local library acceptance. No implementation or tests changed in
this research pass; prior passing counts are not new test executions.

## Finish checklist — authoritative current status, 2026-10-24

Scope remains the approved library plan, including its Commander opening boundary,
shared architecture, client behavior, privacy, replay and MTGO traceability.
This checklist supersedes outstanding-work claims in the historical checkpoints
below. It does not remove requirements from LIBRARY_IMPLEMENTATION.md sections
"Rewrite architecture and acceptance contract" and "Requirements and evidence".

Verified baseline: 2,051 rules / 840 API tests in library-mill-observer-rules.log
and library-mill-observer-api.log. Eight subsequent test-only cases bring the
focused observer suite to ten passing tests (library-mill-observer-replay.log).
Last recorded client verify: 1,869 tests. These are separate checkpoints, not a
claim that the current final combined acceptance gate has run.

Latest backend checkpoint supersedes that baseline: 2,061 rules / 840 API pass
in library-mill-phased-observer-rules.log and library-mill-phased-observer-api.log.
The shared trigger collector now excludes phased-out sources after a public-command
mill regression reproduced an incorrect firing (one failed / one control passed).
This closes that F2 source-availability defect; live phasing remains part of F6.
For F1, selected discard, successive counter payments and multi-object tap recovery
now enter through Game.Apply rather than the internal BeginActivationPayment
helper. All 223 payment tests pass in library-public-prefix-costs.log. Alongside
the prior exile/return conversion, this strengthens public-admission evidence
without widening any gate. Broader replacement and legacy boundaries are unchanged.

| ID | Remaining deliverable | Current classification | Evidence required to close |
| --- | --- | --- | --- |
| F1 | Library payment failure boundaries | Audited supported paths have a bounded acceptance decision; unsupported replacement classifiers remain explicit limits | LIBRARY_RESUMED_FAILURE_CONTRACT.md records the current admission/recovery decisions and 307 passing payment cases. No supported escaping partial-failure branch was reproduced. Retry/decline and unchanged-pending refusal remain intentional. A broader replacement claim requires a concrete legal reproducer, not a generic exception wrapper. |
| F2 | Hidden mill and occurrence-time rule boundaries | Enumerated case table resolved by general rules and regression evidence | LIBRARY_HIDDEN_MILL_RULES.md records the rules-derived hidden-arrival decision, public departure/global predicates, source lifetime/visibility, disclosure lifetime, within-library movement and departure/return. The 42 hidden-destination and 33 observer tests are bounded evidence; arbitrary replacement chains are not certified. No hypothetical card-specific ruling is required to proceed. |
| F3 | Base-operation audit | Mapping completed; unresolved findings belong to their named checklist items | LIBRARY_ACCEPTANCE_AUDIT.md maps the original requirements to source/tests/live evidence, including the UI/transport reconciliation. F1/F2 rule boundaries and F7 final integration remain separate; do not restart mapped operations as absent. |
| F4 | Replay compatibility and single ownership | Scoped audit completed with explicit compatibility limits | LIBRARY_OWNERSHIP_AUDIT.md records single owners and mill event/payment-origin composition. Recorded legacy search/arrangement/payment tests establish supported formats or unchanged-pending refusal for missing evidence. Unknown historical logs are not certified; no speculative migration is claimed. |
| F5 | MTGO comparison | Evidence incomplete; private server algorithms unknown | Complete a method-by-method matrix of recovered client behavior and local counterparts for counts, prompts, actions, selection/submission and mulligans. Label demonstrated equivalence, demonstrated divergence, gap or unknown. For unknown server behavior, identify the missing evidence explicitly; local tests cannot establish superiority. |
| F6 | Live/visual acceptance | Finite inventory completed; final integration remains F7 | LIBRARY_UI_ACCEPTANCE_MATRIX.md indexes every finite viewport row and the mill-observer/phasing live runs. Portrait rotation notices, simulated delivery loss and browser touch are explicitly distinguished from usable portrait board controls and physical-device evidence. |
| F7 | Final integrated acceptance and user review | Current verification checkpoints and review recorded; full comparison verdict remains F5 | LIBRARY_FINAL_ACCEPTANCE_INDEX.md records 2,103 rules, 846 API, 1,919 client tests, inspected captures, four-seat library interactions and separate two/four-seat opening runs. LIBRARY_REVIEW.md is the shown review artifact. Subsequent selection changes have focused four-player evidence and full client verification. No commit authorized; these gates do not resolve F5 unknowns. |

Work order: finish F1/F2 implementation decisions and F5 comparison boundaries,
then F7 final requirement-by-requirement acceptance and user presentation.
Latest API gate: 844 passed, zero failures/skips, exit 0 in
library-admission-full-api.log. This includes four new admission-to-session
SQLite/reload cases for single/paired commanders and two/four players, plus all
existing API regressions. This supersedes the earlier 840-test API checkpoint.
The rules/client baselines remain separate; no new production rule/client change
was made in this integration-test slice. Final completion is still unproven.
Commander-size acceptance correction: libraryOpening now uses 99 basic Forests
and one commander rather than the repeated practice deck. Two-browser table
27b1f764-cc23-477d-8e6b-d8533db8f242 passed explicit 92/7/1 opening counts,
93-card library after paid mulligan bottoming, privacy, reload, kept-hand
preservation and the two-player first-draw skip (library-opening-commander-size.log,
exit 0). Completed seat-0 desktop/portrait/landscape captures were inspected;
portrait retains the established rotation notice. QA uses the real DeckGate with
synthetic card statuses and bypasses GameLobbyDecks/CommanderDeckAdmission, so
this does not establish production Commander construction validation. Four-seat opening size
acceptance subsequently passed on table 62ad65e8-348f-49a7-9d99-ccb9db5681c4
(library-opening-four-commander-table.log, exit 0): free first mulligan, paid
second mulligan with private bottoming/reload, first-turn draw and all four
browser views/counts. Completed seat-0 captures were inspected in all three
viewports. Earlier attempts failed on two-player DOM assumptions (summary
presence and Focus mode); the verifier now selects Table view for four-seat
count checks. No production gameplay change was needed.
Latest F5 implementation checkpoint: search pointer selection now matches the
recovered main card route's six-pixel radial and 500-ms click limits. Client
verify passed with 1,871 tests (library-search-click-gate-verify.log); four-seat
table f283984f-c68d-43ae-a230-37fb7710518d passed the expanded gesture probe,
keyboard/touch selection, privacy, confirmation and reload
(library-search-click-gate-live.log). Four new/current captures were inspected.
This closes that bounded gesture difference; action metadata parity remains open.
The integrated checkpoint after the finite UI inventory passed: 2,085 rules tests,
840 API tests and 1,870 client tests (library-ui-combined-rules.log,
library-ui-combined-api.log and client library-ui-combined-verify.log). The subsequent
search gesture change passed npm run verify with 1,871 tests and a four-seat live
test (library-search-pointer-fixed.log; table 35ae99ef-3bd5-4b77-98ca-4c17a05fb097).
Both horizontal drag directions were rejected; keyboard and emulated landscape
touch selection, private search confirmation and all-seat reload passed. All three
gesture captures were inspected. Passing these checkpoints does not close the
remaining rule or comparison questions, or constitute final user review.
New work must attach to one of these requirements with a concrete failure or
missing acceptance cell. Hypothetical combinations are not automatically new
completion requirements; neither may a required case be silently excluded.

Original-contract cross-check (2026-10-24): LIBRARY_IMPLEMENTATION.md sections
"Rewrite architecture and acceptance contract" and "Requirements and evidence"
require shared movement/replacement/randomness owners, replayable pauses,
authorized projections, the listed library operations, and end-to-end verification.
They do not demand implementing every card effect or reconstructing unavailable
MTGO server algorithms. Therefore an F1 extension must demonstrate a legal library
interaction using the supported instruction vocabulary, rather than an injected
Unsupported diagnostic or arbitrary missing card capability. F2's characteristic-
sensitive hidden arrival remains a concrete mill-policy question. F5 still needs
honest method-level verdicts and evidence for claimed improvements; unknown server
behavior must stay unknown, not become an invented implementation requirement.
This clarification preserves the original contract and does not close those items.

F6 update: the mill-observer live run now passes on four-seat table
`896c82ab-3ef7-43b8-bf28-3e13fd5ec9c8` (library-mill-observer-live.log).
The actor reloads at the pause, declines, loses its observer, retains one trigger,
then gains exactly one life after resolution. All seats reload at each stage;
event counts establish one mill, one firing and two resolutions. The actor's
observer departure was additionally inspected through the QA projection, and
trigger landscape / resolved desktop captures were viewed. The owned host was
stopped. This closes that F6 scenario, not the remaining phasing/visual acceptance
or F1–F5. Next work returns to F1/F2 implementation boundaries.

### Already evidenced; do not restart as unimplemented

Integrated checkpoint after the finite UI inventory: full rules suite exits 0
with 2,085 passed, no failures/skips (library-ui-combined-rules.log); full API suite
exits 0 with 840 passed, no failures/skips (library-ui-combined-api.log).
Client npm run verify also exits 0 (session 78296): formatting, lint, UI checks,
all 1,870 tests, changed-file coverage and production build pass. Its log is
mtg-client/library-ui-combined-verify.log. The existing card-modal stylesheet
warning remains (18.05 kB versus 16 kB). This checkpoint does not close unresolved
F1/F2/F5 decisions or substitute for F7 requirement-by-requirement user review.

F5 selector-lifecycle source recheck removes a stale combined-list claim from the
method matrix. The current component groups by owner and preserves stable section
identity; its existing regression verifies rename, another owner's expiry and
detail cleanup. Retained MTGO manager hash matches the recorded evidence and its
create/remove delayed-selector responsibility is mapped. Inline sections versus
selector windows, exact update timing and runtime reopening remain explicit
differences/unknowns. No new runtime equivalence or superiority claim is made.

F6 finite UI inventory now has no remaining partial viewport rows. The final
mode-guard run passes on table d2ae0ad9-fc03-4257-9225-f46b454e86ec
(library-mode-guard-viewports.log): no reposition during a spell draft, no revision
change on cancellation, then successful reposition/expiry and all-seat reload.
All three guard captures were inspected; portrait is the rotation notice.
Verifier exited 0 and owned host stopped. Mill-observer and phasing live runs are
already recorded below. Physical-device evidence remains explicitly unclaimed;
F1/F2/F5 decisions and F7 final integration are not closed by this UI checkpoint.

F6 draw/shuffle prompt and warning viewport cells are now indexed to fresh runs:
draw fc864542-b33e-456f-8251-ad508caacc23 (library-draw-prompt-viewports.log),
shuffle 10806f3b-ca99-47f1-9bc4-ae4d924a7572 (direct session 62232, exit 0).
Six pending captures and three uncertainty-warning captures were inspected.
Portrait prompts show the rotation notice; the warning modal is readable at all
three sizes. Four-seat completion/reload and shuffle dropped-response retry pass.
Both owned hosts stopped. No production change; mode-guard viewport proof remains.

F6 look/stop viewport indexing is complete for table
6827e24d-a9cb-41fc-85f5-6e95849421db (library-look-stop-controls-live.log).
Active desktop/landscape inspection and all three stop-control modal captures
were inspected. Private active reload, stop/expiry and final all-seat reload pass;
the existing uncertain-delivery runs remain separate evidence. Verifier exited 0
and owned host stopped. Draw/shuffle prompt and other partial-row indexing remain.

F6 opponent-library viewport cell passes on four-seat table
a37a8cef-7b90-4a83-ad22-21391926bdcd (library-opponent-controls-live-final.log).
All three inspected modal captures show count/privacy without card identities or
manual actions. Opening/resizing changes no events/revisions. The run also passes
own-library look/stop and all-seat reload. An uppercase-text assertion was corrected
in the harness; no production change. Verifier exited 0 and owned host stopped.

F1 restriction review reconciled against current source: the resumed-failure
contract no longer labels ordinary-prefix admission or its live panel acceptance
as missing. The remaining discriminator is an exact unclassified legal replacement
event or a legal interpreter branch requiring an escaping partial result. The
synthetic Unsupported-effect retry regression is not such a reproduction, and
missing/corrupt journal evidence is an explicit refusal contract. No new production
change or broad F1 completion claim follows from this reconciliation.

F6 maximum-pick search now passes on four-seat table
e2e2b0bb-364b-4998-8561-9ca36ccdb37c (library-search-max-pick-live.log).
The actor selects two Forests and attempts a third; exactly two remain selected.
Confirmation yields two revealed tapped arrivals, no shuffle, unchanged hand
count, expired inspection and consistent all-seat reload. Desktop/landscape
captures show selection and confirmation; portrait shows the rotation notice.
All three were inspected. The verifier exited 0 and the owned host stopped.
This supersedes the maximum-pick gap noted below; other F6 partial rows remain.

F6 intentional zero-pick search passes on four-seat table
8e6f7d55-a69a-4831-9136-787474d0fe9d (library-search-zero-pick-live.log).
Matching cards are offered after reload, but confirmation selects none. One
search/shuffle, unchanged hand/library count, private inspection expiry and
all-seat reload pass. Desktop/landscape captures show usable confirmation;
the inspected portrait capture shows the rotation notice and is not claimed
as usable portrait selection. Maximum-pick capture reconciliation remains.
The verifier exited 0 and the owned host stopped; no production change.

F6 finite inventory is reconciled at the top of LIBRARY_UI_ACCEPTANCE_MATRIX.md.
Later keyboard/404-detail, busy-feedback, empty-library and server-rejection runs
supersede those original pending rows. Remaining partial rows name concrete
viewport evidence. Search destination-dependent controls are now verified below. No new
live acceptance is claimed by this reconciliation and no extra scenario scope
was added.

F6 search-form destination controls now pass on four-seat table
4a655a17-75ba-4871-acf8-860d236305f9 (library-search-destination-live.log).
The verifier checks five destinations across desktop, portrait and landscape,
conditional placement/tapped fields, in-viewport controls, no horizontal overflow
and no command/state change from editing. It then completes the private search
and all-seat reload. Six conditional-control captures were visually inspected;
the acceptance matrix distinguishes these from the other nine automated states.
No production change was needed. The owned process handles are absent.

F1 successful-prefix counterpart: PartialNestedLibraryRecoveryTests now includes
a successful activation after the nested ordinary-prefix pause. It uses a zero
final mill cost instead of the unpayable three-card cost, retains the first
library operation, orders the three surviving triggers through a public answer,
replays the batches and asserts exactly one AbilityActivated and no recovery.
Duplicate payment answers are refused and settling produces no repeated trigger.
All six cases pass in library-prefix-success-tests.log. This test establishes
successful payment/stack creation, not resolution of the activated ability or a
new live successful-payment run. No production change was necessary.

F2 characteristic-free filters: four added cost/effect replay cases exercise an
unrestricted filter and an owner-only filter for milling redirected to hand.
Triggers occur without granting the body a hidden-card reference, and opponent
privacy remains intact. All 38 HiddenMillDestinationTests pass in
library-hidden-known-filter.log. This narrows the open case to characteristic-
sensitive matching; no production behavior changed.

F3 original UI/transport row reconciliation is now recorded at the top of
LIBRARY_ACCEPTANCE_AUDIT.md, completing the outstanding mapping task. It links
private preview expiry, opening variants, empty boundaries, search/confirmation,
ordering/touch, pending/error, retry/reconnect and verification to their recorded
acceptance while preserving limits. This is reconciliation of existing evidence,
not a new live run or closure of every F6 viewport cell or the F7 final gate.

Post-prefix client gate: npm run verify exits 0 in sibling
mtg-client/library-prefix-panel-verify.log: formatting, lint, UI-registration,
1,870 tests, changed-file coverage and production build pass. The existing
card-modal stylesheet warning remains (18.05 kB versus 16 kB). Together with
the latest 2,080 rules / 840 API gate and prefix-panel live run, this verifies
that change; F7 remains open until the other checklist requirements close.

F1 activation-panel acceptance now passes on table
b529279d-d282-40fc-9727-44f4c148d3b4 (library-prefix-panel-live.log): card click,
payment order display, cancel without state change, reopen/confirm, three choices,
recovery and all-seat reload. The panel capture was inspected. A missing QA-only
descriptor route was enabled; production client behavior required no change.
This closes the previous panel gap. The owned host was stopped; no commit.

F1 live ordinary-prefix acceptance passes on four-seat table
97edfb3b-e9ec-4301-bcf2-0a846c83e8e4 (library-prefix-activation-live.log).
Normal activation hub command pauses before library costs; three browser choice
answers with reloads complete recovery. Life returns to 40, library count is 3,
one shuffle/recovery and one surviving trigger are verified, then that trigger
resolves once. All seats reload without additional events. Choice landscape and
final actor desktop captures were inspected. The initial QA payload used an object
instead of a numeric source ID and was corrected before the successful run.
The owned host is stopped. Activation-panel UI and final combined acceptance
remain separate; no production/client change or commit in this follow-up.

F1 ordinary-prefix admission is implemented after two public nested-recovery
regressions reproduced refusal. A prefix replacement pause can now continue into
library-only remaining costs using the existing journal and shared completed-prefix
assessment. Public replay, recovery, duplicate-answer and missing-legacy-evidence
checks pass. Full gates: 2,080 rules / 840 API in library-prefix-admission-*.log.
See LIBRARY_RESUMED_FAILURE_CONTRACT.md for scope and updated fixture entry points.
The next acceptance task is a live multiplayer run of this newly admitted prefix
path; earlier library-cost-pause runs do not close it. No commit is authorized.

F1 restriction decisions are now explicit in LIBRARY_RESUMED_FAILURE_CONTRACT.md.
The concrete remaining public-admission case is a replacement pause during an
ordinary prefix followed by library costs; PartialNestedLibraryRecoveryTests
currently asserts public refusal then uses an internal seam. This is not closed
by the passing final/earlier-library-pause tests. Ordinary costs after library
costs are rejected during preparation; random discard is a separate unsupported
cost capability. No admission gate was widened during this source review.

F4 mill payment/event-origin composition is now traced through Outcome.Then,
LibraryMillContinuation, TriggerObservation, Game.Apply/Settle and shared recovery
filtering in LIBRARY_OWNERSHIP_AUDIT.md. A new serialized-event regression verifies
notification cause index/origin preservation and exclusion from duplicate
detection. The focused observer/recovery run passes 35 tests in
library-mill-origin-audit.log. This closes that scoped ownership review, not F1
admission restrictions, unknown saved-log formats or the final full gate.

F4 inspection lifecycle ownership is traced in LIBRARY_OWNERSHIP_AUDIT.md:
one active inspection registry, scoped manual/resolution owners, shared identity
mapping, event-owned shuffle/departure cleanup and viewer/current-zone filtering.
Relevant lifecycle assertion bodies were inspected. No duplicate registry or
algorithm was found in that boundary, and no refactor was justified. Payment and
event-origin composition remains a separate outstanding audit.

F2 same-library replacement: four new public-command cost/effect cases pass,
including serialized replacement pauses, in library-mill-reposition.log. Replacing
mill with top-to-bottom rearrangement preserves identities/order, emits no mill
or trigger, continues resolution and refuses duplicate answers. Primary-rule
rationale and the precise scope are recorded in LIBRARY_HIDDEN_MILL_RULES.md;
this does not close hidden-arrival characteristic matching or leave-and-return
replacement chains. No production change was required.

F4 search follow-up: two new emulated pre-batch saved-choice cases now resume
through Game.Apply after serialized replay, selecting one card or none. Both
avoid repeated inspection, shuffle exactly once, clear inspection and reject
duplicate answers. All three LibrarySearchContinuationTests pass in
library-legacy-search-resume.log. No production change was necessary; captured
historical logs and other continuation positions are not certified by this test.

F4 saved-choice contract is now consolidated in LIBRARY_OWNERSHIP_AUDIT.md.
A fresh focused run passes 61 tests in library-replay-contract-check.log.
It distinguishes literal legacy search/arrangement format support, first-offer
mill-cast replay, and explicit unchanged-choice refusal for missing nested
payment evidence. Historical search end-to-end replay and later legacy offer
positions are not claimed as proven; inspection/event-origin ownership remains
part of the bounded audit. No production change or migration was added.

F6 detail-preview follow-up: table e425af95-9552-4947-bc31-42ce9651d428
passes the separate --phasing-detail variant (library-phasing-detail-live.log).
The verifier opens the private top card's detail preview before phasing its
permission provider, then requires the preview, context menu and viewer card
to disappear. Private top inspection and legal casting are revoked; all four
seats reload at revision 8. Before/after landscape captures were inspected:
Canopy Scout's text is visible before revocation and only the face-down library
count remains afterward. This closes the detail-preview gap noted below, not
physical-device testing or the final integrated gate.

F6 phasing acceptance now passes on four-seat table
fdfcc229-93ad-4171-a1cd-5a813d1dc5f3 (library-phasing-live-final.log).
After normal activation/resolution, private top inspection and legal casting are
revoked, the open zone-card menu/card disappear, and all seats reload consistently.
Before/after landscape captures were inspected. ZoneViewer now reconciles its
context card against the current visible cards/readiness; the rendered regression
passes. Client npm run verify: 1,870 tests, coverage and production build, exit 0
(mtg-client/library-phasing-verify.log); existing stylesheet budget warning only.
The owned QA host was stopped. This closes the phasing scenario and supersedes
the diagnostic timeout/stale-menu checkpoints below. An already-open global card
detail preview was not exercised by this specific run.

F6 projection diagnosis: QA stage timing isolated legal-action generation at
31,825 ms versus 133 ms for view projection. LayerSystem now caches gathered
static-effect mappings by exact immutable GameState in a ConditionalWeakTable;
new states recompute, and old snapshots are not strongly retained. The same
four-player fixture measured 86 ms view / 698 ms total after the change.
Full backend gates pass 2,073 rules / 840 API in library-static-snapshot-*.log.
The subsequent browser run reached phasing and revoked server access but left
the zone viewer's context menu stale; that client cleanup is being verified.

F6 phasing attempt exposed a new diagnostic requirement: the new isolated
four-seat --libraryPhasing fixture builds/starts but GET /api/games/{id} exceeds
a 15-second deadline before browser acceptance can begin. The browser run in
library-phasing-live.log failed waiting for the board. Broad/self recipients,
owner-scoped recipients and finally explicit owned battlefield-creature
recipients all produced projection timeouts; this does not prove recursion or
identify a specific method. Latest table was d9fe511c-54ff-486d-8f4e-578a407468cd.
All owned diagnostic hosts were stopped. The fixture and --phasing verifier are
retained for reproduction. Next work must isolate the projection cost/failure
against a direct-permission control, rather than shrink the deck and declare
phasing accepted. No production change was made during this attempt.

F3 boundary reconciliation: Commander redirects, authorized manual operations,
concurrent/stale requests and four-seat search restart now have source/assertion
mappings in LIBRARY_ACCEPTANCE_AUDIT.md. The inspected tests show owner prompts,
deferred shuffle, exact inspected arrangement, enforced-table refusal and one
save for concurrent retries. No new implementation gap was found in those
boundaries. The original UI/verification rows still need consolidated mapping;
this does not close the specific outstanding edge cases or final gate.

F3 core-operation reconciliation: LIBRARY_ACCEPTANCE_AUDIT.md now maps inspected
draw, shuffle, search-bounds/participants and ordering implementations to actual
assertion bodies. These basic operations are implemented; missing whole-operation
claims are stale. Remaining F3 reconciliation covers Commander redirects, manual
permission boundaries and transport/UI evidence. Specific F1/F2 cases and the
final integrated acceptance remain separate and are not closed by this audit.

F5 reconciliation: LIBRARY_METHOD_PARITY.md now begins with current verdicts for
eight recovered responsibilities. Older missing-keyboard/pending-disable/manual
control/top-land claims are superseded by inspected local code and recorded
acceptance. Actual differences (hide versus disable and offered-action metadata)
remain explicit; generic retained client code cannot prove library-scenario
metadata or private-server parity. No new MTGO runtime evidence was acquired.

F2 pause follow-up: all 12 public-departure global-condition cases pass in
library-mill-global-condition-pauses.log. New cases serialize/replay pauses
before and after replacement movement for cost/effect and revealed/hidden
variants, verify private deferred-trigger redaction, refuse duplicate answers,
and prevent repeat settling triggers. This closes the precise paused-replacement
gap in the checkpoint below; no additional production change was needed.

F2 public-departure global-state fix: a library-count trigger condition reproduced
two failures for publicly revealed mill departures redirected to hand; hidden
controls passed. TriggerObservation now uses its local pre-movement replay state
for occurrences carrying public departure evidence. No state snapshot is saved.
Full gates pass 2,065 rules / 840 API in library-mill-global-condition-rules.log
and library-mill-global-condition-api.log. The precise paused-replacement variant
remains to verify; see LIBRARY_HIDDEN_MILL_RULES.md for the bounded claim.

F6 two-player opening run: table 6c0c98bb-4dc3-4f54-b876-c5cbe0e80625
passes one paid mulligan, UI bottom selection after both-seat reload, private
options, kept-hand identity preservation, first-turn draw skip and final reload
(library-opening-two-live.log). Actor bottom-choice landscape and final desktop
captures were inspected. The latter exposed a concrete remaining UI defect:
the actor summary says Library 65 while its pile correctly says 66 after bottoming
one card. Reproduce and reconcile the summary's count before marking this visual
acceptance complete. The synthetic fixture does not prove Commander deck legality
or catalog artwork. The owned QA host was stopped.

Correction after DOM verification: the apparent 65/66 summary mismatch above was
a screenshot-reading error, not a reproduced application defect. Fresh table
31272840-6204-46b6-81ea-c9ebbe7aad9d passes explicit assertions comparing each
player's summary and library pile text against the authoritative counts in both
browsers (library-opening-two-counts.log). No production fix was warranted.
The verifier also explicitly requires PrecombatMain before accepting the skipped
first draw. Both reload stages and responsive captures pass; the host was stopped.

F3 reconciliation: the original Commander admission/separation and opening-hand
rows now have inspected source/assertion mappings in LIBRARY_ACCEPTANCE_AUDIT.md.
The 99+1 and 98+2 startup boundary, printing/finish retention, multiplayer free
mulligan, paid two-player first mulligan and pending-bottom replay are implemented
and covered by the recorded backend gate. The two-player opening browser variant
remains an explicit F6/F7 acceptance item; unrelated two-player flows do not close it.

F4 privacy checkpoint: both API saved-log/direct-choice variants now retain a
deferred mill trigger server-side while withholding its source/appearance and
private chosen marker from both player views and the chooser's hub push.
library-deferred-trigger-privacy.log records 2 passing targeted tests. Existing
allowlisted context projection needed no production change. This closes the new
mill continuation's API privacy check, not the whole F4 compatibility audit.

F4 compatibility checkpoint: activation recovery with missing legacy nested-origin
evidence now reports an explicit saved-payment incompatibility (733.1), instead
of returning only the later unpaid-cost reason. PartialNestedLibraryRecoveryTests
removes nestedEvidence from serialized activation data, verifies the public
refusal and unchanged serialized pending choice, then verifies successful recovery
with complete evidence. This is explicit incompatibility, not migration support
or proof for all old log schemas. Full verification: 2,061 rules / 840 API in
library-legacy-refusal-rules.log and library-legacy-refusal-api.log. The browser
presentation of this specific diagnostic is unverified.

F1 clarification: the current ordered cost vocabulary cannot place ordinary
life/mana costs after a library pause. Its only non-library deferred remainder is
random discard, which ObjectCosts explicitly does not implement. The existing
admission restriction is appropriate for that capability boundary; do not count
a generic widening of this gate as required library work. See the reachable-cost
audit in LIBRARY_RESUMED_FAILURE_CONTRACT.md. Completed-prefix recovery assessment
and legacy continuation evidence remain open.

- Four-player Commander partner opening, free mulligan and private bottom ordering;
  kept-hand identities and configurable opening shortcuts have recorded acceptance.
- Shared draw/search/order coordinators, manual controls, search selection and
  confirmation, and multiple response-loss/retry scenarios have recorded tests/live runs.
- Four-player all-player scry and surveil, including keep-all ordering and rotated
  active seat, pass in the all-ordering logs recorded in LIBRARY_ACCEPTANCE_AUDIT.md.
- Public Reveal and private Look shuffle expiry pass four-player reload/visual runs
  in library-disclosure-shuffle-live-current.log and library-private-shuffle-live.log.
- Static library selectors, stable permission origins and phased providers have
  backend regressions. Top-library land/spell actions have earlier live acceptance.
- Mill observer lifetime now passes public-command replacement/replay cases for
  both removal orders, pauses and graveyard/hand destinations. Its live run remains F6.

These entries record bounded evidence, not complete operation or MTGO equivalence.

## Historical checkpoints (superseded totals and remaining-work statements)

Latest production verification: 2,049 rules tests and 840 API tests pass
(`library-phased-grant-rules.log`, `library-phased-grant-api.log`). A reproduced
phasing defect allowed an indirect static grant to keep authorizing library lands
and spells after its provider phased out (two failures in
`library-phased-grant-red.log`). The shared LayerSystem.Statics reader now excludes
phased-out providers under the repository's CR 702.26b. The regression verifies
serialized phasing, command refusal, removal from legal IDs and restoration on
phasing in. This is backend evidence; the particular phasing UI sequence has not
been exercised live. This supersedes the full-suite totals immediately below.

The historical entries below are not the current verification totals. The last
full production-change run passed 2,044 rules tests and 840 API tests. Three
subsequent test-only additions pass in the 18-case LibraryStaticPermissionTests
suite: independent static-grant providers, copied permission stability after
keyword removal, and distinct permissions across serialized face changes.
Client verification last passed 1,869 tests; readiness/reconnect and lost-response
live evidence is recorded at the top of LIBRARY_UI_ACCEPTANCE_MATRIX.md.

Library top-play permissions now enforce static selectors and use stable ability
origins rather than changing effective-list positions. This closes the reproduced
permission defects, not the entire library plan or MTGO method parity.

Four-seat browser verification now passes for shuffle revocation of both public
Reveal and private Look inspection, including all-seat reload reconnection and
opponent views. See the latest LIBRARY_UI_ACCEPTANCE_MATRIX.md entry and logs.
Forced transport interruption during the shuffle itself remains untested. The broader
resumed-payment and hidden/global-state observation boundaries below remain open.
Do not invent partial-payment failure semantics from unsupported interpreter
errors: the nested-cast prepayment discriminator established atomic retry/decline,
not a committed partial failure (LIBRARY_RESUMED_FAILURE_CONTRACT.md).

Reconciled 2026-10-24 against current source and recorded acceptance results.
Explicit reveals now carry resolution-scoped provenance into bounded mill
departure observation. Cost/effect, replacement-local reveal, serialized pause,
private Look-to-all and expired-reveal controls pass. Identity-refresh and legacy
inspection regressions also pass. A subsequent shuffle regression exposed stale
inspection access; the shared reorder event now revokes access to that library.
Full rules verification passes 2,029 tests (`library-shuffle-disclosure-rules.log`).
Browser acceptance of this shuffle transition remains unverified. These updates
supersede earlier explicit-reveal gap descriptions below; they do not close
general trigger-source/global-state look-back or old paused-payment compatibility.
Revealed-top mill look-back now has bounded public-card appearance evidence and
shared query matching. Cost/effect, serialized-pause and compound-predicate
regressions pass, including removal of the characteristic provider. Explicit
replacement reveal lifetime and broader hidden/global-state look-back remain open;
see LIBRARY_HIDDEN_MILL_RULES.md.
Latest admission change: public earlier library pauses now pass when only library
costs remain and the completed prefix passes shared recovery assessment. Public
success/recovery/replay tests pass. Four-player live success passes in
`library-public-earlier-live.log`; four-player later-cost failure/recovery also
passes (`library-public-earlier-failure-live.log`) with exact mill/recovery counts,
life refund, no activation on the stack and all-seat reloads. Non-library
pauses and unsupported payment combinations remain restricted. This supersedes
the earlier blanket-gate descriptions in historical entries below.
This register does not replace or narrow the approved library plan. Historical
checkpoints retain their evidence; they are not current outstanding-work lists.

The simultaneous movement/property mismatch is traced in
`LIBRARY_BATCH_PROPERTY_RECOVERY.md`, including its serialized resume path and
the distinction between replacement evaluation and actual departure evidence.

Admission follow-up: recovery now handles the reproduced battlefield → exile →
graveyard sacrifice chain, including retention when its final identity later moves
to the library. Full rules coverage passes in `library-chain-recovery-rules.log`.
Twelve targeted variants now verify pauses between hops, three-hop paths, and
commander bookkeeping when the owner keeps the intended hand/library destination
(`library-chain-paused-tests.log`). The reproduced command-zone refusal at library
entry is now fixed by following dependencies into already-retained payment steps.
The commander redirect and shuffle remain applied while independent life is refunded.
Four-player live acceptance now passes for that library-entry return on the admitted
spell-resolution payment path (library-commander-chain-live.log). Earlier hand-entry
return now passes replay recovery tests after adding validated terminal Command
movement support. Four-player live acceptance also passes, including restoration
of the original commander and cancellation of the undone tap trigger
(library-commander-hand-live.log). Mixed independent chains now pass both movement
orders with and without a serialized pause after supporting validated incidental
roots without cost markers. Four-player mixed-chain acceptance also passes,
including independent tap cancellation and exactly one retained trigger
(library-mixed-chain-live.log). Earlier activation admission remains to verify.
The reproduced token-creation obstacle now has shared creation/history recovery
and exact trigger-provenance mapping. Independent creations are removed; creations
consumed by later retained movements are preserved. Both outcomes now pass
four-player live acceptance, including opposite token-history/trigger dispositions
(library-token-recovery-live.log and library-token-retained-live.log).
Same-step creation → optional tap → graveyard now passes recovery/replay tests,
including creation/entry/death history removal and occurrence-time token entry
observation. Four-player live acceptance now passes creation/death history removal,
trigger disposition and reloads (library-token-movement-live.log). Broader mixed
creation/movement layouts remain outstanding; earlier activation admission is unchanged.
Multiple-token recovery and selective retention now pass targeted replay tests:
one created token may remain dependent on the library operation while another is
undone, preserving a pre-existing token. Exact retained history/trigger identity
is checked. Four-player selective multiple-token acceptance now passes in
`library-token-selective-live.log`, including two creations before the choice,
one retained creation/trigger, restoration of the original artifact, private
choices and all-seat reloads. Exact retained identity is established by the
rules tests; the live test checks counts and resulting views.
See LIBRARY_RESUMED_FAILURE_CONTRACT.md.

Live follow-up: table `e240698f-ad33-499f-8324-5c749db94708` passes the admitted
spell-resolution recovery path with a retained tap trigger, three UI prompts,
private contexts and four-seat reloads. This closes that effect-path acceptance
case only. A follow-up table `51390545-ad5d-42b6-a611-d5d023606dd5` also passes
live tap-and-move within one uninterrupted replacement, with four-seat reloads
and exactly one retained trigger/resolution. Earlier activation admission remains
unverified live; see LIBRARY_BATCH_PROPERTY_RECOVERY.md.

The finite UI state inventory is now recorded in `LIBRARY_UI_ACCEPTANCE_MATRIX.md`.
It distinguishes existing successful permission/mode/retry runs from missing
empty-library, invalid-form, busy-request and viewport-specific visual evidence.

| Requirement | Current evidence and boundary | Next necessary proof or change |
| --- | --- | --- |
| Earlier interrupted payments | `ActivationPaymentAdmission.Allows` admits a library pause when remaining costs are library costs and shared recovery assessment accepts the completed prefix. Public-command replay tests and four-player success/failure runs pass (`library-public-earlier-live.log`, `library-public-earlier-failure-live.log`). | Non-library pauses, unsupported prefixes and non-library remaining costs are still restricted. Compare those boundaries with required library interactions before widening admission; the recorded success run does not resolve the stacked ability. |
| Failure inside resumed operation | `ChoiceSystem.ResumeOperation` returns only on success/pause. Unsupported-effect diagnostics establish atomic refusal/retry, not a legal unpaid-cost recovery. | Reproduce a supported failure, then implement the typed partial-outcome contract in `LIBRARY_RESUMED_FAILURE_CONTRACT.md`. Do not convert arbitrary interpreter errors into paid/unpaid results. |
| Incomplete/mixed payment steps | `PaymentRecoveryPlan.Assess` rejects incomplete steps. Movement/life partition and tested mana/property combinations work; not every dependency combination is supported. | Define and implement partial-step assessment with provenance, retained library operations, and correct trigger cancellation. |
| Nested payment acceptance | Recorded effect/internal-activation tests and selected live scenarios pass. Arbitrary deeper combinations are not established. | Verify the combinations required by admitted production behavior, using normal commands, replay, private views, and live multiplayer. Avoid claiming universal support from synthetic examples. |
| Hidden mill semantics | `LibraryDepartureObservation` captures the continuously revealed library top's evaluated appearance before a hand/library departure. Shared observed query matching passes cost/effect, paused replay and compound-filter regressions; API tests verify persisted evidence stays out of both players' views and direct choice pushes. | Explicit reveal lifetime, unobserved hidden-card filtering and trigger-source/global-state look-back remain open in `LIBRARY_HIDDEN_MILL_RULES.md`. Bounded card evidence does not establish complete pre-event state semantics. |
| Replacement-created extra mill cards | Fixed reproduced mill-one-to-three replacement follow-up loss. Direct and paused/replayed variants now move all three remembered public arrivals. | Graveyard/exile/hand variants pass with pauses and replay after fixing hidden-arrival fallback and lost paused arrivals. Four-player public-exile follow-up passes seven replacement choices/reloads and hand privacy (`library-mill-followup-live.log`). Mana-value aggregation and lasting play permissions pass focused public/hidden/replay tests. Immediate MayCast now queues every arrival; accept-all/decline-all replay regressions pass. Mixed accept/decline and per-card life/sacrifice prompts pass seven replay variants. Four-player immediate accept/decline/accept passes ten UI prompts/reloads and two resulting spells (`library-mill-cast-live.log`). Live life/sacrifice costs and both resulting spell resolutions pass twelve prompts/reloads (`library-mill-cast-costs-live.log`). Broader nested replacement provenance remains to verify. |
| Existing paused logs | Missing activation ownership evidence now refuses recovery; missing off-stack effect snapshots cannot recreate a departed source. | Specify supported migration versus explicit incompatibility, and test representative serialized logs. Silent reconstruction is not valid evidence. |
| Remaining delivery-loss interactions | Draw, shuffle and both search variants have live response-loss/retry evidence. Lost Look and Stop completion pass two inspection cycles after fixing stale inspection request reuse. Top/bottom reposition and full arrangement pass four-player lost-response retries, identical payload/request ID, exact library order, one reorder and all-seat reload. | Stale arrangement IDs are fixed by scoped retirement after confirmed mutations; A → B → A and immediate retry both pass live. Service tests cover failed intervening actions and scope isolation. Arrangement → reposition → arrangement now also passes live; arbitrary cross-action interleavings remain unverified. |
| UI acceptance | Several desktop/portrait/landscape scenarios and browser touch interactions are recorded. | Complete a finite inventory of library UI states and required viewports, including errors, pending actions and permission loss. Physical-device behavior remains unverified. |
| MTGO method comparison | Recovered client methods map to local responsibilities; exact equivalence is not established. Server algorithms were not recovered. | Complete method-by-method behavioral comparisons where source evidence exists. Label unavailable server behavior unknown; do not claim superiority from local tests or class names. |
| SOLID ownership | Library coordinators and shared payment evidence remove documented duplication. Resumption now has an explicit operation boundary. | Re-audit ownership after implementing the remaining failure contract; each policy must have one authoritative implementation. |
| Tap followed by movement inside a payment replacement | Fixed the reproduced missing trigger through command-start-state replay and occurrence-time tap detection. The retained artifact trigger passes with zero/one/two pauses, serialization and exact payment ownership; independent tap cancellation still passes (`library-tap-observation-rules.log`, 1,963 rules tests). | Verify live acceptance and specify legacy deferred streams/direct Settle callers lacking a starting state. Other trigger kinds retain existing observation behavior. |
| Property change before another movement in the same batch | Added occurrence-time MovementDepartures to the payment journal. Both selection orders now recover, including zero, one and two pauses, with replay and duplicate-answer refusal. Missing, altered and duplicate evidence refuse. Full rules suite: 1,960 passing (`library-batch-reverse-rules.log`). | Complete live acceptance and remaining recovery requirements before widening public admission. Tap-trigger evidence is in the row above. Replacement evaluation and departure-history semantics are unchanged. See LIBRARY_BATCH_PROPERTY_RECOVERY.md. |

Current production verification: 2,006 full rules tests
(`library-public-mill-compound-rules.log`; final cleanup passes 18 hidden-mill
tests in `library-public-mill-compound-final-tests.log`) and 840 API tests
(`library-mill-observation-privacy-api.log`), including observation redaction,
compound bounded revealed-top mill look-back, assessed earlier library activation admission, same-step token movement/entry observation, token creation recovery, mixed-chain recovery, Commander hand-entry recovery and library-entry redirect dependencies,
chain recovery, tap observation, batch departure evidence and
the empty-library loss priority-transfer fix.
The latest rules run includes public activation success/recovery, multiple-token
reversal and selective retention with exact retained trigger identity. The API
suite was rerun after the bounded look-back change and passes all 840 tests.
This includes the four additional mill-payment cases and the off-stack effect
redaction regression. Latest client verify: 1,860 tests plus coverage/build
(`mtg-client/library-elimination-label-verify.log`).

Recorded search live acceptance: `library-search-battlefield-retry-live.log`, table
`966e8592-7ec1-4ee7-9f82-f0477b212247`: one search, one reveal, zero shuffles,
two tapped Forests, same-ID retry, unchanged event counts and all-seat reload.

The library is incomplete. No commit is authorized. Card-definition expansion,
general board redesign and unrelated deck-builder changes are outside this work.
