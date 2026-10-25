# Current library method register

## Additional bottom-order reference screening — 2026-10-24

Screened two further primary recordings through their public YouTube transcripts:

- MTGGoldfish, Commander Clash S4 Episode 17: Anything Goes, February 17, 2018,
  https://www.youtube.com/watch?v=LEAu68FwIOI . The transcript's submitted `scry`
  search reports No results found. The initially exposed transcript includes
  bottom-library discussion at 1:28 and Vessel of Endless Rest at 8:49, neither
  identifying the needed multi-card scry sequence. No qualifying frame was found.
- MTG Muddstah, Grenzo versus Olivia Voldaren, July 25, 2016,
  https://www.youtube.com/watch?v=pcoeEYIAvGU . The exposed transcript through
  10:22 contains no scry, Crystal Ball, Read the Bones or bottom-placement match.

Transcript screening cannot prove that an unspoken interaction is absent from
a recording. These are screened leads, not parity evidence, and should not be
reopened as newly discovered sources without a specific timestamp or new clue.
The existing separately observed single-card bottom sequence remains bounded;
the multi-card bottom-order question is unchanged. No test or code change.

## Latest-build opening verification — 2026-10-24

Reran the unchanged two- and four-player opening verifier against the latest
production dist, after the top-placement and all-graveyard surveil changes.
Four-player table 9dfaac2f-8709-454a-9656-3deb55452ea2 passed with revisions
[19,19,19,19] (library-latest-opening-four.log). Two-player table
9a294086-0b0c-4278-84de-9943642ed0c9 passed with revisions [10,10]
(library-latest-opening-two.log). Both processes exited 0. These cover Commander
counts, private decisions, paid bottoming after reload, kept-hand preservation
and the player-count-specific first draw. Inspected fresh four-player landscape
bottoming and two-player desktop completion captures. Synthetic fixture cards
have no artwork; this run does not establish real collection admission or art.
No tests, expected results or production code changed. Owned QA hosts stopped.

## Observed sequential bottom placement — 2026-10-24

Inspected the public MTG Muddstah recording in the browser (no media download):
https://www.youtube.com/watch?v=od-JTIVaPm8 , published February 22, 2016.
At approximately 17:25–17:27 the one-card temporary zone and context menu identify
Goblin Razerunners; the bottom action is selected. At 19:01 the next one-card
scry menu identifies Bojuka Bog and offers the same bottom action. At 20:10–20:17
the narrator identifies Bog as the next Grenzo result, then Razerunners; at 20:22
the resolved Razerunners is visibly present and enlarged on the battlefield.
The Bog identity at resolution is supported by narration, not a readable enlarged
graveyard card in the inspected frame. Do not overstate this distinction.

This supports later single-card bottom placement below an earlier bottom card.
It is NOT an observed multi-card scry ordering sequence: these are separate
one-card decisions. That remaining comparison stays unproven. Existing local
LibraryOrder.Place appends at bottom and LibraryOrder.Arrange appends the supplied
bottom sequence; InterpreterLibraryOrderingTests.Scry_bottom_order_preserves_objects_and_does_not_emit_zone_changes
fixes [rest, second, first] for supplied [second, first]. No expected result,
test baseline or production code was changed for this research.

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

## New concrete multi-search finding — 2026-10-24

Public recorded MTGO behavior now narrows the multi-card unknown below:
d00mwake, "Cultivating wins with Primeval Titan | MTG Modern Amulet Titan",
https://www.youtube.com/watch?v=ZfNenHYTVYk&t=1493s.
Actual paused browser frames were inspected at 24:48, 24:53, 24:58, 25:03,
25:03 plus 30 frames, and 25:04. At 24:48 the Titan trigger offers a yes/no
decision. At 24:53 the library is open with two selections remaining and a Done
button. At 25:03 one remains. Frame stepping through the final selection closes
the library by 25:04 and advances to Vesuva's copy choice, without the pointer
moving to Done. By 25:08 the two land arrivals and their triggered abilities are
visible. The transcript timestamps were used only to locate the sequence; the
interaction conclusion comes from video frames, not the transcript text.

Implemented and verified: ChoiceSelectionService now submits accumulated physical
option IDs when an unordered, nondivision search reaches its maximum. Legal
partial selections still complete with Confirm; ordered/divided choices retain
their existing completion policy. A remaining-pick count uses the same selection
owner. Pending submission retains the draft and prevents duplicate sends.

Full client verify passes 1,893 tests, coverage and production build in
../mtg-client/library-search-capacity-layout-verify.log. Four-seat automatic
completion passes on f6d64c2f-0204-489c-8f26-2bb6149eb769
(library-search-capacity-layout-live.log); partial one-of-two completion passes
on c5eca90f-a67f-4599-ab02-21cbe0d57ba4
(library-search-partial-capacity-live.log). Held responses verify one command;
results verify exact public arrivals, no shuffle and all-seat reloads. The
initial landscape capture exposed filter overlap; the header grouping fixes it,
and corrected landscape plus final desktop captures were inspected.

This establishes bounded correspondence for the recorded unordered search.
Intermediate MTGO wire messages, private algorithms and all search variants
remain unproven. No new transport or named-card implementation was added.

Reviewed 2026-10-24 against retained decompiled client methods and current local
source. This is the current comparison entry point; chronological findings remain
in LIBRARY_METHOD_PARITY.md. Scope is library behavior and Commander opening,
not general targeting, casting, catalog completeness or other zones.

The acceptance contract is LIBRARY_IMPLEMENTATION.md, "Required acceptance
standard: method traceability". A responsibility counterpart is not proof of
complete behavioral equivalence. No row below claims superiority from class names,
test totals, or unavailable server code.

## Required method mappings

| Recovered source and input | Current counterpart and boundary | Evidence and verdict |
| --- | --- | --- |
| Game.ProcessPlayerStatusElement, Game.cs:2063, receives server library counts and calls UpdatePlayerInfoValues in server player order. | ViewProjector.Seat, ViewProjector.cs:80, computes the public library count from authoritative zone membership; engine transport maps it to the named player. Counts do not grant inspection. | **Responsibility implemented; bounded count behavior demonstrated.** Opening, search, draw and shuffle runs validate local transitions. Wire formats and private MTGO computation are different/unknown. |
| Game.ProcessInteractStateElement, Game.cs:2506, receives prompted player, text, timestamp and UI element descriptors; builds GamePrompt and offered actions. | ViewProjector.ChoiceCards and pending projection expose options to the chooser; engineView maps bounds, kinds and cards; ChoiceControlsComponent renders prompts, optional skip and confirmation. | **Bounded prompt behavior implemented.** Four-seat tests establish chooser-only options and active inspection. Local kind/bounds protocol does not reproduce every MTGO UI descriptor. |
| GameCard.ResolveCardActionsHelper, GameCard.cs:3409; optional confirmation fields at 3487. Inputs include action lists, priorities, flags and targets. | LibraryRevealComponent.choiceCardOption, searchOption, surveilOption, playLabel and canReposition derive only authorized current actions; performAction/performExtraAction delegate to shared owners. | **Recorded single-search, capacity-search, scry and single-surveil correspondence demonstrated; broader descriptor mapping unknown.** Single search submits directly, capacity search completes at its maximum, complete scry carries both partitions in one answer, and one-card surveil offers top/graveyard. Scry draft labels deliberately preserve local correction access. Multiple choices for one physical card are not guessed. No observed library producer proves which remaining confirmation/priority descriptors are required. |
| GameCard.ResolveActionTargets, GameCard.cs:3306, and TargetSet.AddTarget/RemoveTarget/CurrentTargets_CollectionChanged. Legal membership, maximum count and readiness constrain selection. | LibrarySearchPlanner supplies candidates/bounds; ChoiceSelectionService.select/canSubmit/move manages offered IDs and ordering; ChoiceSystem.Validate independently rejects wrong player/choice, invalid counts, duplicates and unoffered IDs. | **Membership/count responsibilities mapped; failure behavior differs.** MTGO asserts on invalid target insertion; our UI ignores unoffered input and server refuses it. Target relationships are not automatically library search rules. The recorded Primeval Titan search supplies public evidence for automatic completion at capacity. Local deselection, partial completion and ordering have live evidence; arbitrary MTGO target relationships remain outside this correspondence. |
| GameAction.Execute, GameAction.cs:179, dispatches GetMessage; CardAction.GetMessage, CardAction.cs:171, validates fulfilled target sets and serializes object IDs. | GameBoard.pick/submitChoice/skipChoice dispatch through SignalRService.choose:278. The pending request is keyed by game/choice; engineCommand creates the answer from the captured view. ChoiceSystem.Answer:24 validates before emitting ChoiceAnswered and resuming. | **Local identity, duplicate and retry behavior demonstrated; protocols differ.** Physical-instance IDs survive selection. Current-source inspection confirms the snapshot is captured before the awaited send. MTGO stale rejection is not proven by a timestamp alone. |
| Game.ProcessInteractStateElement mulligan branches, Game.cs:2623/2634; special-step recognition at 2742/2751. | OpeningHandPrompts and OpeningHandCoordinator own keep/mulligan/bottoming; shared choice controls and LibraryShortcutBindingPolicy render/dispatch. TurnSystem delegates opening progression. | **Prompt responsibilities mapped; rule algorithm independently implemented.** Two-player paid first mulligan and multiplayer free first mulligan have separate engine/live evidence. Client decoding cannot prove MTGO's private opening algorithm. |
| No recovered server draw/search/shuffle/mulligan algorithm. | LibraryDrawCoordinator, LibrarySearchCoordinator/Policy/Planner, LibraryShuffleCoordinator, LibraryOrder and OpeningHandCoordinator delegate movement/replacements to shared contracts and persist continuation in event-reduced state. | **Unknown MTGO server implementation, explicitly allowed as unknown by the plan.** Published-rule and replay/privacy evidence validates local behavior; no algorithm identity or superiority claim. |

## Library window methods

| Recovered method | Local counterpart | Current verdict |
| --- | --- | --- |
| CardSelectorManager.UpdateDelayedSelectors/GetShouldZoneDisplay/CreateVisual/DestroyVisual | ViewProjector authorized inspection membership; LibraryRevealComponent stable owner groups and active panel creation/removal | Active nonempty inspections now open automatically; expiry removes them. Search, surveil and four-owner public disclosure verified. This is a bounded display-lifetime match. |
| Overlays.AddCardSelectorsAsChildren:156 groups HasActions, sorts by name, constructs a movable/resizable Library panel and binds authorized count. | LibraryInspectionPresentation.groups, disclosure count, FloatingPanelDirective and UiPreferencesService.libraryPanel | Grouping and display sorting preserve library order. Movement, resize, clamp, saved geometry and collapse/reopen verified locally. Library-only file import/export and its application to an actual inspection are verified in library-layout-panel-final-live.log. Own/opponent settings routes and retained window ordering have local live correspondence; MTGO settings-file byte compatibility is not claimed. |
| CardSelectorViewModel.CardSelectClose_Execute:243 and CanExecute:247 | Native details/summary collapse, independent of choice submission | The recovered generic close action is disabled and is not bound by Overlays. Local collapse sends no game event and preserves selections. The retained FloatingPanel no-close template has no collapse control. Local collapse is an intentional convenience; native BringToFront now maps to FloatingPanelStackService.raise:12 and FloatingPanelDirective.activate:46/start:87. library-panel-stack-live.log demonstrates click/focus activation retained after focus leaves, without game events. |
| CardSelectorViewModel.CardSelect_Execute/CardSelectOk_Execute | Shared selection/submission controls, subject to the actual library route | Generic selector confirmation is not evidence that every library search needs Confirm. The recorded normal card-action route submits single fetch selection directly; the local path now follows that observed behavior. |

## Current verification index

- Full rules: library-final-integrated-rules.log, 2,103 passed; API:
  library-lobby-revalidation-api.log, 846 passed. Current engine checkpoint.
- Latest client: ../mtg-client/library-surveil-completion-verify.log,
  1,919 passed, coverage and production build; existing card-modal style warning.
- Opening: library-final-integrated-opening-four.log, table
  25593e00-44cb-4777-b341-c6e6a84a311a, and
  library-final-integrated-opening-two.log, table
  8992885b-9d54-4004-b4ba-33f507459810. Both preserve Commander-size opening,
  paid bottoming after reload, private choices and player-count-specific first draw.
  Later client changes concern top-order/surveil completion; these are recorded
  opening checkpoints, not a claim of rerunning opening after those changes.
- Sequential top placement: library-top-placement-live.log, table
  b3eea15a-6789-481a-8539-59af030d2bcd. Exact four-seat ordering and reload passed.
- All-graveyard surveil: library-surveil-all-graveyard-live.log, table
  ca3b801c-894f-4c90-ab5a-5b6d73e5995c. All-seat private decisions, exact remaining
  order, deferred movement and reload passed. Partial-selection behavior remains
  covered separately in library-surveil-partial-preserved-live.log.
- Direct search, optional zero, no-match and multi-search: indexed at the top of
  ../LIBRARY_REMAINING_WORK.md. Search panel lifecycle latest:
  library-auto-open-search-live.log, 2dcf827b-68bf-4a6c-bee6-98861ad40777.
- Single surveil menu: library-single-surveil-{top,graveyard}-live.log;
  automatic opening rerun: library-auto-open-surveil-live.log,
  153f4e69-1b68-491d-9584-7d7c9ace926b.
- Four-owner public access/expiry: library-all-disclosure-live.log,
  00fa65f7-513c-447e-9770-0db8f7b6bc13. Each owner is reachable by collapsing
  the front panel; this does not establish equal multiwindow usability.
- Public observed reference: BoshNRoll tutorial, September 16, 2021,
  https://www.youtube.com/watch?v=h6posM1Gbv8 (27:06–27:13 search;
  30:48–30:53 surveil). This is recorded behavior, not a current-server payload.

## Remaining comparison work

The table supplies a current counterpart and verdict for every method row named
by the plan. Full one-for-one-or-better acceptance remains unproven for broader
library action descriptors, other unobserved search variants,
and hidden bottom ordering. The retained native library template has no collapse control; local collapse is a documented addition. Unqualified search now has public recorded evidence, and library geometry file export/import has local integrated acceptance. Continue public reference research for concrete
scenarios; do not ask the user again for unavailable recordings or introduce
guessed metadata. Final integrated acceptance and presentation remain separate.
No commit is authorized by this register.

## Concrete library action inventory — 2026-10-24 source review

Inspected retained GameCard.cs:3409–3595 and current client LibraryRevealComponent,
ZoneViewerComponent, library-action.ts and LibraryActionsService. This inventory
replaces an unspecified descriptor backlog with explicit applicability boundaries.

| Local action | Preconditions and owner | Comparison verdict / remaining discriminator |
| --- | --- | --- |
| Select/toggle a search card | Connected, active inspection, matching current choice ID and exactly one offered option for the physical card; pending blocks dispatch. Shared ChoiceSelectionService owns accumulation and completion. | Recorded single and unordered two-card completion correspond. Unqualified-search direct completion is observed in the CalebDMTG Demonic Tutor recording; mandatory server bounds and refusal/replay are verified independently. |
| Single surveil top/graveyard | Same current-card lookup; surveil min 0, max 1, one option, no division. Delegates existing skip/pick. | Recorded menu correspondence. Multi-card arrangement uses shared ordering controls; do not infer an identical MTGO menu from this single-card recording. |
| Cast/play a disclosed card | Active current inspection, connected idle interaction, no current choice, and membership in server castable/playable IDs. Delegates CardCastingService. | Existing integration boundary; full casting is outside this library-only task. Disclosure by itself does not enable play. |
| Manual draw/shuffle/look/stop/search | Own library, connected idle interaction and explicit manualLibraryActionsAllowed; shared LibraryActionsService and persisted request journal. | Intentional honour-system capability, not a claim that MTGO offers arbitrary manual actions. Server still authorizes each command. |
| Manual top/bottom and full arrangement | Above permission plus active manual look; reposition requires owned inspected card; arrangement requires all library cards inspected. Shared reposition/arrange requests. | Local authorized capability; not evidence of a competing MTGO server algorithm. |
| Card details | Active or historical disclosed data; active selection reconciles against latest permitted membership. | Read-only display, not a game action. History does not grant current library inspection. |
| Panel collapse/move/resize | Presentation-only controls; collapsing retains the pending choice and draft. | Separate own/opponent geometry and retained stacking are demonstrated. Native no-close library template is established; local collapse is an intentional addition. |

Recovered descriptors requiring careful interpretation:

- ACTION_LOW_MENU_PRIORITY, ACTION_PARENT_ACTION_ID and ACTION_ALT_MENU support
  generic priority/submenu composition. The inspected consumer is shared by all
  game cards; it does not identify a library search that requires these fields.
- ACTION_ALWAYS_MENU forces a menu for the producer-supplied action. Our inspection
  card opens a menu while the prompt option directly selects. Recorded prompt
  completion matches; the general card-click/menu equivalence is not established.
- CONFIRM_MODE_STRING distinguishes absent from present-empty, with separate
  accept/cancel strings. No observed library producer in the retained evidence
  establishes which remaining search variant supplies it. Do not manufacture a
  confirmation dialog for every library action.
- Modal counts/repeat, local tap, activated/mana/stack and target-dependency flags
  are generic card-action features. Their presence in this consumer does not make
  their entire implementation a library requirement or authorize expanding into
  card/casting implementation.

Completed public observation: Andrea Mengucci VEEDEO, "Otherworldly Gaze is
amazing in Dredge | Modern | MTGO", January 12, 2022:
https://www.youtube.com/watch?v=9JxX6vSPuk8 . Inspected 4:49–5:10 shows three
graveyard selections completing without another ordering prompt. Inspected
57:31–57:58 shows a separate top-order prompt after one graveyard selection,
Prismatic Ending placed before Thrilling Discovery, and the latter drawn first.
ChoiceSelectionService.select:53–59 now completes the full-set surveil contract
at capacity; partial selections retain Confirm. placeOnTop:64–76 prepends each
offered selection and submits when complete. LibraryRevealComponent.topOrderOption
and performExtraAction guard the active choice/inspection and delegate to that
shared owner. Existing explicit top-first ordering remains available; selected
cards stay visible for correction, unlike the recording's disappearing cards.
The fixed unit and live contracts above cover those paths. Treat the recording's
pre-errata instruction separately from every modern surveil producer and trigger
timing. No named card implementation was needed for this comparison.
The unqualified tutor, native collapse template and retained window stacking are
already covered by the newer evidence above; do not restart them as missing.

Fresh local comparison run: library-surveil-order-reference-live.log, table
3ab45625-0fd5-426d-acb8-4a5aea238e5d. Existing four-player --surveil --keep-all
acceptance passed unchanged: separate top-order prompt, exact top-first result,
private decisions, deferred movement and all-seat reload. The seat-0 ordering
capture was inspected and clearly shows numbered selections, correction arrows,
Confirm and Start over. This verifies the local explicit-order contract and makes
its gesture difference from native sequential placements concrete. No production
or test expectation changes; no commit. The owned QA host was stopped.
