# Library method comparison

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


2026-10-24: a newly inspected public two-land search recording demonstrates
completion on the final permitted pick, with a remaining-pick counter and Done
available before capacity. The local unconditional multi-search Confirm is a
concrete divergence. Source, exact frames, limitations and implementation target
are at the top of LIBRARY_CURRENT_METHOD_REGISTER.md.

Current consolidated required-method table:
[LIBRARY_CURRENT_METHOD_REGISTER.md](LIBRARY_CURRENT_METHOD_REGISTER.md).
It maps all seven plan rows and window methods with current counterparts,
behavioral evidence, differences and unknowns. Chronological entries below
retain their original dates and may be superseded by that register.

## Active inspection now opens on creation — 2026-10-24

Current-source audit found a concrete presentation divergence: the local details
element had no open binding, so every newly authorized inspection began collapsed.
The recovered CardSelectorManager delayed branch creates a visible selector when
authorized ZoneVM.Count becomes nonzero; Overlays creates its floating library
panel. The public search/surveil recording also shows the library already open
when the player chooses. This is a supported automatic-presentation counterpart,
not an inference about the disabled generic close command discussed below.

LibraryRevealComponent now initializes active panels open and historical panels
collapsed. Stable owner keys and unchanged Angular binding values preserve a
user's subsequent native collapse through routine state updates. Revoking access
removes the element; a later new inspection opens again. One focused regression
checks creation, collapse, changed-card update, history, expiry and recreation.
No new UI state store or engine permission was introduced.

Full verify passes 1,887 tests, coverage and production build, exit 0 in
../mtg-client/library-inspection-auto-open-verify.log; existing style warning.
Four-seat search table 2dcf827b-68bf-4a6c-bee6-98861ad40777 passes automatic opening,
collapse/reopen with selected instances, event neutrality, completion and reload
(library-auto-open-search-live.log). Four-seat surveil table
153f4e69-1b68-491d-9584-7d7c9ace926b passes automatic opening for each chooser,
card-menu movement, exact order/privacy and reload (library-auto-open-surveil-live.log).
Search desktop and surveil landscape captures inspected. Synthetic fixture cards
do not establish artwork. Hosts stopped; no commit. QA scripts now express the
desired expanded/collapsed state explicitly through a shared helper where needed.

## Search panel lifetime discriminator — 2026-10-24

Retained CardSelectorViewModel.cs:243–249 implements an empty
CardSelectClose_Execute and an always-false CardSelectClose_CanExecute.
This is not evidence that closing a search answers it with zero cards.
The actual library overlay construction, Overlays.AddCardSelectorsAsChildren
(156–205), constructs FloatingPanel/HorizontalPanelView in Library display mode;
it does not bind that close command. CardSelectorManager.UpdateDelayedSelectors
(104–124) and GetShouldZoneDisplay (127–137) manage creation/removal from authorized
zone contents. Therefore the disabled generic close command must not be promoted
into a claim about every floating-panel minimize/close gesture.

Local counterpart: LibraryRevealComponent's native details/summary toggles
presentation; ChoiceSelectionService owns selected option IDs independently.
The disclosure subscription removes active contents when projection permission
expires. Collapse/reopen does not call GameBoard.pick/skipChoice or end a search.
Four-seat table 0ffc40cd-5a52-4b45-8277-a3198e55ac9a proves this local boundary
with two selected physical Forest instances at desktop and landscape. Choice and
event snapshots remain unchanged; selections survive reopening; the normal
two-card search then completes, reveals and enters tapped, preserves the intended
no-shuffle behavior, and passes observer/final reload checks. Evidence:
library-search-panel-choice-live.log, exit 0, and tools/TableQa/
library-search-{collapsed,reopened}-{desktop,landscape}.png. Reopened landscape
capture inspected. No production changes or new full-suite result; host stopped.

Verdict: local lifecycle and non-submission behavior demonstrated; exact MTGO
floating-panel collapse/reopen interaction remains unknown. This narrows the
unverified boundary and rules out inferring cancellation from the generic close
method; it does not assert superiority or require a user-supplied recording.

## Single-surveil implementation verified — 2026-10-24

The recorded one-card menu below now has a local counterpart. LibraryRevealComponent
offers top/graveyard only for an authorized active single-card optional surveil
choice. Both destinations delegate to GameBoard's existing pick/skip handlers;
the shared submission owner and LibraryArrangementCoordinator remain unchanged.
Search and surveil reuse the same unambiguous current-card option lookup. Pending,
disconnected, stale, withdrawn and historical-inspection actions are rejected.
The existing transport marks surveil as ordered; a one-card destination does not
need an additional order choice. Multi-card arrangement is unchanged.

Rules source checked directly: CR 701.25a in the September 25, 2026 rules at
https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt.
Full client verify: 1,886 passed, coverage and production build exit 0,
../mtg-client/library-surveil-menu-verify.log. Existing card-modal style warning.
Four-seat live menu runs: fc17830c-90cc-4c0d-8921-ab596fcae3f5 (graveyard) and
1f990514-2ec9-4ead-b38a-1ec88dbfcd39 (top), logs library-single-surveil-*-live.log.
Both verify chooser-only options, private inspections, exact hidden order,
deferred movement, destination counts and per-seat/final reload. Desktop and
landscape menu captures inspected. Synthetic QA cards do not establish artwork.
Hosts stopped; no commit. This supersedes the implementation target below.

The user confirmed no private reference is available and requested continued
public-source research. The public recording resolves this concrete menu and
single-search interaction; no user recording is a dependency. Broader current
MTGO action descriptors remain unverified and are not inferred from these flows.

## Recorded single-card surveil menu — 2026-10-24

Observed in the same BoshNRoll tutorial, actual browser video frames at
https://www.youtube.com/watch?v=h6posM1Gbv8&t=1848s (30:48) and 30:53.
The Dragon's Rage Channeler surveil trigger displays Unholy Heat in a library
window. Clicking the card exposes two destination actions: retain it on top or
move it into the graveyard. At 30:53 the graveyard action has completed, the
library window and surveil trigger are gone, and the underlying Lightning Bolt
remains on the stack. This is a recorded 2021 flow, not a current-server payload.

Current local mapping: LibraryArrangementPlanner.Plan offers the inspected
objects as optional selections for graveyard movement; an empty answer keeps
the top card. LibraryArrangementCoordinator owns movement/continuation, and
ViewProjector.ChoiceCards supplies authorized option cards. Those semantics
already exist. The UI gap is concrete: LibraryRevealComponent.searchOption only
exposes choice actions for kind search; a surveil inspection card therefore has
no matching top/graveyard destination menu. ChoiceControls instead offers the
card selection and generic Choose none controls separately.

Implementation target: project both authorized single-surveil outcomes into
the shared card menu and delegate selected/empty answers to the existing choice
submission owner. Guard chooser authorization, current choice identity,
pending/connection state and inspection expiry. Do not build a second surveil
coordinator or infer multi-card ordering behavior from this one-card example.
This supplies a concrete next F5 action-menu gap; no production change or new
test result is claimed by this observation.

2026-10-24 implementation follow-up: the demonstrated extra confirmation step
is removed for offered single-card unordered/nondivision choices through the
shared ChoiceSelectionService.requiresConfirmation policy. The existing command
owner submits immediately; pending UI retains the submitted disabled option.
Multi-card choices and ordered searches still require Confirm and optional searches retain
Choose none. Client verify passes 1,883 tests and production build. Four-seat
direct, multi-card and zero-pick runs/captures are indexed at the top of
../LIBRARY_REMAINING_WORK.md. This is bounded correspondence to the recorded
flow, not proof of every optional confirmation/menu descriptor or current MTGO
runtime equivalence. Older unconditional-confirmation statements below are
superseded for this single-card path. No commit.

## Recorded qualified single-card search — 2026-10-24

Primary observation of recorded application behavior (not an official specification):
BoshNRoll, "ANCIENT TECHNOLOGY EXPLAINED! ... Basic MTGO tutorial!", published
September 16, 2021, https://www.youtube.com/watch?v=h6posM1Gbv8&t=1626s .
Inspected the actual video in the browser, including paused frames around
27:06–27:13, rather than relying on search snippets or an unavailable transcript.

- At 27:06 the resolving Misty Rainforest ability shows a library window with
  eligible lands highlighted separately from darkened nonmatches. The prompt
  instructs the player to click a card to put it onto the battlefield and exposes
  a Done button.
- At 27:12–27:13 the pointer is on the offered Volcanic Island. The library and
  stack window then close and Volcanic Island appears on the battlefield. No
  separate selection-confirmation dialog is visible in the inspected transition.
- This establishes direct selection for this recorded qualified single-card
  search. It does not establish every search, current-client behavior, a zero-pick
  Done result (not activated here), or absent/present private confirmation fields.

Local comparison: ChoiceSelectionService.select explicitly retains maxPicks=1
search picks and returns null; ChoiceControls renders Confirm for every search.
Therefore this flow has a demonstrated extra confirmation step locally. The
generic CardSelectorViewModel selection/OK path must not be treated as proof
that all MTGO library searches use that path. The recovered normal card-action
dispatch and this observed search are distinct evidence.

The implementation follow-up above now supplies direct single-card search
dispatch through the existing choice submission owner, retaining offered
membership, pending exclusion, retry identity and explicit zero-pick handling.
Do not generalize this recording into immediate submission for ordered or
multi-card searches. This finding supersedes the blanket "no reference sequence"
blocker; no production change or new test result is claimed by this entry.

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

## Official web evidence — 2026-10-24

The user's request to use web sources produced additional primary evidence:

- [MTGO gameplay guide](https://www.mtgo.com/getting-started/getting-started-gameplay):
  the Prompt Box communicates required actions; the Revealed Cards area displays
  hidden-zone search disclosures; zone panels support expansion/pop-out and layout
  resizing. Playable cards are highlighted before all costs are payable, and
  non-hand zones containing castable cards open automatically. Zero-mana spells
  receive a Cast menu to prevent accidental activation.
- [MTGO tips](https://www.mtgo.com/getting-started/getting-started-tips-tricks):
  Ctrl+Z can reverse eligible mana activations, but drawing or milling through a
  mana ability prevents undo. Escape cancels the current selection/process;
  the 1 key accepts the current prompt.

These sources establish documented behavior, not internal method or payload
equivalence. The local inspection panel supports desktop movement/resizing and
browser-local saved geometry. MTGO export/import and multiple-window layout
equivalence remain unverified. No superiority claim follows. Generic Escape behavior
does not prove that an already resolving mandatory search can be abandoned.
The guides do not specify the scenario-specific ACTION_LIST fields or qualified
search confirmation sequence. Retain those as unknown comparisons, rather than
requiring user account access before other library acceptance work can proceed.
No production behavior was changed by this documentation update.

## Current reconciliation — 2026-10-24

Desktop geometry update: FloatingPanelDirective now supplies pointer/keyboard
movement and viewport clamping for the existing authorized inspection panels;
native resize is enabled at the desktop breakpoint. Phone landscape uses the
fixed overlay. Four-seat real-browser geometry/privacy/expiry/reload checks and
inspected captures pass library-floating-live.log; client verify passes 1,878
tests. This supplies the movable/resizable responsibility documented by MTGO's
official gameplay guide, superseding the blanket geometry divergence below.
It does not prove MTGO-equivalent saved positions, multiple-window stacking or
scenario-specific reopening timing. No private server method is inferred.

Pending-prompt implementation supersedes the disabled-versus-hidden divergence
below for search and opening-hand actions. ChoiceSubmission metadata records the
clicked control and submitted IDs in the existing SignalR pending request state.
ChoiceControls retains that disabled control and hides alternatives until
completion/failure. Tests and four-seat Confirm/Choose none/mulligan captures
are recorded in LIBRARY_ACCEPTANCE_AUDIT.md (1,875-test client gate passed).
This is a bounded counterpart to PromptBoxViewModel.DeactivatePrompt; it does
not implement MTGO's complete action descriptor or conditional confirmation
protocol. Unknown/external submissions without metadata retain the safe disabled
fallback. Basic persisted inspection-window geometry is now implemented; full
MTGO layout settings equivalence remains unverified.

Current-source recheck: LibraryInspectionPresentation.groups already separates
offered instances from inspection-only cards and sorts displayed groups by name,
without mutating authorized input or choice order. Its assertion cases cover
duplicate names, hidden IDs, pending-choice precedence and playable lands.
LibraryShortcutBindingPolicy supports configurable modifier keys, unbound
actions and collision rejection; its tests exercise exact modifier matching and
binding changes. Older rows calling either feature absent are superseded by
these inspected implementations, not new feature requests.

For the requested one-for-one comparison, the remaining differences are concrete:

| Boundary | Current classification | Completion implication |
| --- | --- | --- |
| Counts, authorized owner sections, display grouping, opening shortcuts and pointer selection | Bounded responsibility counterparts implemented; runtime MTGO equivalence not established | Retain method mappings and local acceptance. Do not rebuild these because an older checkpoint says missing. |
| Prompt alternatives during submission | Equivalent demonstrated for the bounded standard-button behavior: submitted action disabled, alternatives hidden for search/opening choices | ChoiceControls pending metadata and four-seat Confirm/Choose none/mulligan captures; client library-submission-presentation-verify-resumed.log (1,875 tests). Unknown external submissions use disabled fallback; complete action protocol not claimed. |
| Inspection window geometry | Desktop movement/resizing and browser-local saved geometry implemented; full equivalence unknown | FloatingPanelDirective and UiPreferencesService.libraryPanel; library-layout-preferences-live.log verifies four-seat movement/resizing, collapse/reopen, responsive fallback, full-page restoration and privacy/expiry. Export/import and multiple-window equivalence remain unverified; no superiority claim. |
| Single-card search submission | Bounded correspondence demonstrated to the recorded Misty Rainforest flow: choosing an offered card submits directly | ChoiceSelectionService.requiresConfirmation/select and ChoiceControls share the decision; direct four-seat pending/privacy/reload verification passes. The 2021 recording does not certify all current MTGO searches. |
| Conditional confirmation and per-card action metadata | Broader descriptor capability remains unmapped locally; library scenarios requiring it remain unknown | The direct-search finding removes unconditional local confirmation for that flow. Do not introduce guessed confirmation/menu/priority metadata or claim every optional descriptor equivalent. |
| Qualified-search fail-to-find action and reopen timing | Unknown MTGO scenario behavior | Local rules/privacy tests prove local behavior only. |
| Draw/search/shuffle/mulligan server algorithms | Unknown private implementation; published-rule implementation exists locally | The plan separates rule validation from unavailable server reconstruction. |

This closes stale-feature classification, not F5 as a whole or the user's
one-for-one-or-better criterion. No new tests were run for this source audit.

Selector count mapping checked independently of public pile totals:
CardSelectorViewModel initializes CardCount from ZoneVM.Count, updates it from
CardCountChangedHandler on collection changes, and unsubscribes in Dispose.
LibraryRevealComponent's inline summary displays reveal.cards.length for each
authorized disclosure. This maps the displayed authorized-content count, not
SelectedCards.Count and not the total hidden library. The retained view-model
hash still matches b3531b632b001bc4edea3c834e896aa522836c0fed917ed4e5e1be89574fbbe6.
No additional count implementation is needed on this evidence. Server disclosure
timing remains outside this static comparison.

Live-reference dependency: the current process inventory found no process named
MTGO/MagicOnline, and a bounded search of retained JSON/log/text evidence found
no actual ACTION_LIST/CONFIRM_MODE_STRING scenario payload. This is not proof
that no installation or account exists. The user has been asked whether a live
session or recording is available. A visual recording can establish click/menu,
confirmation, fail-to-find and reopen behavior; it cannot establish hidden
priority/group/parent-action fields. Those require an authorized runtime action
inspection or captured scenario payload, not assumptions from generic consumers.

The minimum reference scenarios are: a mandatory unqualified single-card search;
a qualified search with matches where zero picks is legal; a multi-card search;
and inspection closing/reopening before completion. For each, record offered
card actions, left/right click behavior, confirmation/cancellation, selection
count, window lifetime and the result. Compare the same local scenario before
changing dispatch. Account access, installation and live reference results are
not inferred from the existence of downloaded assemblies.

Implementation follow-up (verified): ChoiceControls now applies the
main route's squared displacement <= 36 and elapsed time <= 500 ms to pointer
search selection. Keyboard activation bypasses the pointer gate. The component
regression now includes seven-pixel and diagonal rejection, 501-ms rejection,
and an accepted six-pixel endpoint. The live probe adds seven-pixel, diagonal and
600-ms gestures. npm run verify passed (1,871 tests, exit 0) in
library-search-click-gate-verify.log. Four-seat table
f283984f-c68d-43ae-a230-37fb7710518d passed all five rejection gestures,
keyboard and emulated landscape touch selection, search confirmation, privacy and
all-seat reload (library-search-click-gate-live.log, exit 0). Threshold, diagonal,
hold and touch captures were inspected; the owned host was stopped. This is a
bounded match to the recovered click gate, not full action-protocol parity.
This supersedes the ten-pixel
local implementation described in the preceding checkpoint below.

Main library card-route discriminator: retained DuelSceneViewModel.OnMouseUp
(around lines 2908–3042) supplies the caller gate for Card_View.ProcessMouseUp.
DoesMouseDownTimeCountAsSingleClick accepts elapsed time <= 500 ms; OnMouseUp
then rejects a normal click when dx*dx + dy*dy > 36. It also returns for a
two-button zoom release or an animated card. SelectTargets routes left clicks
through LeftClickDuringSelectTargets; OrderTargets and Distributing have separate
paths. Otherwise an eligible card with actions outside a pile zone receives
ProcessMouseUp when the click gate or explicit isDragCast flag permits it.
This is retained-source evidence, not a runtime observation.

Because Overlays' library panel uses Card_View, this six-pixel radial/500-ms gate
is a stronger comparison for ordinary library card interaction than the separate
CardSelectorView's signed ten-pixel predicate. Our search-button gate is currently
ten pixels per axis with no duration limit. The tested +/-20-pixel suppression
agrees with both routes, but does not establish threshold parity: a seven-pixel
horizontal move, a five-by-five diagonal move, and a stationary 600-ms press
are decisive differences. Do not claim the local gate improves on MTGO's main
card route merely because it improves on the separate selector predicate.
Library-specific server action values and interaction modes remain unknown.

Current pointer behavior supersedes the pre-change observation below. ChoiceControls
now rejects search clicks whose endpoint displacement is at least 10 px on either
axis, using absolute values. Keyboard activation remains supported. This intentionally
differs from the signed predicate in retained MTGO source: leftward movement receives
the same accidental-selection protection as rightward movement. The tradeoff is that
a mouse click with that much endpoint drift is rejected in both directions. It does
not track maximum excursion or establish physical-device scrolling behavior.

Four-seat table 35ae99ef-3bd5-4b77-98ca-4c17a05fb097 passed the updated
`--search --pointer-probe --four` verifier (library-search-pointer-fixed.log, exit 0).
Real mouse +20/-20 px gestures both remained unselected; Enter and an emulated
667x375 touch tap selected successfully. No probe emitted library events or completed
the search. Normal confirmation, private inspection, hand privacy and all-seat reload
passed. Right/left and touch screenshots were inspected; the owned host was stopped.
Client npm run verify passed with 1,871 tests (library-search-pointer-verify.log).
The unit regression also covers vertical displacement, the exact threshold, pointer
cancellation, a small movement and pending-state protection. These are bounded local
benefits, not live MTGO evidence or a claim of overall superiority.

Pre-change local pointer observation: table e2ddb43d-e845-4331-b456-fcc10d413a3e passes
`--search --pointer-probe --four` (library-search-pointer-probe.log).
Real WebDriver mouse press/move/release within one option selects it after both
+20 px and -20 px movement. Each probe starts with cleared picks; neither emits
library events or completes the pending search. Both saved right/left captures
were inspected. Normal confirmation, privacy and all-seat reload subsequently
pass; verifier exits 0 and owned host stops. Compared with the retained predicate
below, +20 px is a demonstrated local/source-predicted difference, while -20 px
agrees for this bounded input. This is not live MTGO observation, a touch-scroll
test, or evidence that either UX is superior. This describes the pre-change behavior.

Pointer discriminator (retained source, 2026-10-24): CardSelectorView's down handler
stores the pointer position. Its up handler computes signed dx/dy and calls
SelectCard only when `dx < 10 && dy < 10`, then clears the stored position.
There is no absolute-value operation in the retained method. Consequently +20 px
horizontal movement fails this predicate, whereas -20 px passes (assuming the
release source has the required card data context). This is a static prediction,
not observed MTGO runtime behavior or proof of a product defect. The local
ChoiceControls template previously dispatched the browser's click without a movement
predicate. The completed comparison and subsequent local change are recorded above.
Do not call arbitrary drag suppression a recovered symmetric MTGO rule, or claim
the local behavior better without testing and explaining the difference.

Selector lifecycle recheck: retained CardSelectorManager.cs still hashes to
`c9f37aff81ec00eae5fae4ed156d5ba948e05dff9b3f6ca42c29a2843cb9f762`.
AddSelector stores one entry with stable ID, title, zone and window settings;
UpdateDelayedSelectors compares authorized nonempty state against Displayed,
CreateVisual adds a view model, and DestroyVisual removes/disposes it. This is
static retained-source evidence, not a new REA or MTGO runtime observation.

Current LibraryRevealComponent.disclosures$ groups authorized cards by ownerId,
uses stable `inspection:<ownerId>` keys through disclosureKey, and removes an
owner section when its authorized cards disappear. The existing test
`separates owners and preserves an open library through rename and another owner expiring`
asserts two separate owner sections, stable DOM/open state through rename, removal
of the expired owner's section and closure of its detail. Earlier text describing
one combined list is superseded. Local representation remains collapsible inline
sections rather than independent selector windows; no comparative usability
benefit, exact MTGO update timing or runtime reopening equivalence is claimed.

This table supersedes older claims below that mulligan keys, submission disabling,
manual search/order or top-library land dispatch are simply absent. It does not
turn a local passing test into MTGO equivalence. Inspection used ordinary file
tools on retained decompilation; no new REA execution or live MTGO session occurred.
REA tools and an installed rea command are unavailable in this session.

| Recovered responsibility | Current local method / evidence | Verdict |
| --- | --- | --- |
| DuelSceneViewModel.YesNoPressed routes offered semantic actions | LibraryChoiceShortcuts.resolve/key and LibraryShortcutBindingPolicy normalize configured keys; ChoiceControls.onShortcut emits an offered option. Defaults are 3 for mulligan and 4 for keep; other prompt kinds are excluded. Component tests cover pending/readiness suppression. Earlier live shortcut runs and the current opening flow cover local dispatch. | Demonstrated match for the bounded opening-hand selection responsibility. MTGO's general Yes/No modifier and fallback action flags remain a different, broader protocol. |
| PromptBoxViewModel.DeactivatePrompt disables the selected standard button and hides alternatives | ChoiceControls template disables choices, ordering, clear and confirmation while pending or disconnected, and displays Sending. Existing component tests assert those disabled states. | Duplicate-submission prevention has a counterpart. Presentation diverges: local alternatives remain visible and disabled. No superiority claim; matching MTGO's hide behavior would require an explicit presentation decision. |
| PromptBoxViewModel.Execute/FinalExecuteAction dispatch the offered action | Shared choice selection/board dispatch/SignalR submission; normal answer commands remain server-authoritative. | Responsibility mapped, exact action protocol is not equivalent. Metadata-driven conditional confirmation remains a gap. |
| CardSelectorViewModel.CardSelect_Execute retains a single selection before dispatch | ChoiceSelectionService.select special-cases single-card search to retain/replace the offered option without submission. | Demonstrated selection/dispatch split for this case; not blanket multi-target parity. |
| CardSelectOk_CanExecute and CardSelectOk_Execute enable and submit selection | ChoiceSelectionService.canSubmit validates server minimum/maximum; confirmation submits current option IDs. | Explicit difference for zero-pick searches and instance identity. Actual MTGO fail-to-find action values remain unknown. |
| Public total versus authorized selector contents | PlayerView.LibraryCount feeds table summary/pile; inspectedLibraryCards separately feeds authorized content. Two-player opening DOM assertions compare both visible totals to server values in both browsers (library-opening-two-counts.log). | Bounded responsibility match. MTGO server disclosure timing remains unknown. |
| Per-card offered actions | Library inspection derives castable/playable IDs from server legal actions and delegates to shared casting/land submission. Top-land four-seat acceptance is recorded in library-top-land-live.log and LIBRARY_UI_ACCEPTANCE_MATRIX.md. | Older playable-land-transport gap is closed locally. Normal/low priority, parent/group, override and confirmation metadata are not mirrored one-for-one. |
| Manual library search/order | ManualLibraryCoordinator delegates to shared search/order owners; live evidence is in LIBRARY_ACCEPTANCE_AUDIT.md. | Implemented local extension. No recovered MTGO server method establishes equivalence or superiority. |

Rechecked retained source identities:
PromptBoxViewModel.cs SHA-256
`e8cc28a4fa67ec56567a20645db5acc66511ccf69d8196a70a8508157a00b50f`;
DuelSceneViewModel.cs SHA-256
`c49b06f38525aa9d0d9687941d59d5c6bbef1649bbd792169667243b11c208cb`.

F5 is still incomplete: actual library-scenario action metadata, full selector
opening/reopening behavior and unknown server algorithms cannot be certified by
these retained generic client methods. This reconciliation removes obsolete gap
claims; it does not authorize expanding into unrelated casting or deck-building.

Local offered-action update (2026-10-24): inspected top-library cards now use
server legal IDs to offer Cast and delegate to the existing CardCastingService.
ZonePlayPermissions is the shared backend permission reader for lands and spells;
the legal-action builder now checks library candidates. A four-seat real-permanent
fixture passes the spell flow (`library-top-cast-diagnostic.log`). This supplies a
local action-dispatch counterpart, but does not establish parity with MTGO's
priority/group/parent-action/confirmation metadata. Playable-land transport and
earlier intermittent browser submission failures remain separate open checks.

## Offered-action producer trace — 2026-10-24

Retained `GameCard.cs` closes one missing link between action data and the
previously traced card-menu/confirmation consumers. This is static inspection
of retained decompilation using ordinary file tools, not new REA Evidence or a
live MTGO capture. No REA tools or installed `rea` command were available in this
session. GameCard.cs SHA-256:
`a38135cbc0fb0dcfe1e3388a411479d7ad3776560324793b2ee68632b2fd9641`.

| Recovered method | Established behavior | Local counterpart and remaining difference |
| --- | --- | --- |
| `GameCard.ResolveCardActions` (line 3606) | Clears existing actions and resets AlwaysShowActionMenu/HasGameActions, then resolves normal-priority and low-priority actions separately. | Current server choices replace offered options; LibraryRevealComponent invalidates stale context and derives menu actions. There is no general normal/low-priority per-card action-list counterpart. |
| `GameCard.ResolveCardActionsHelper` (line 3409) | Reads consecutive ACTION_LIST sublists, selects the requested priority pass, and reads parent-action IDs, action names, groups, targets and flags before CardAction.Create. | ViewProjector.ChoiceCards provides authorized instances; LibraryInspectionPresentation.groups derives actionable membership from options or castable IDs. This is a partial responsibility mapping, not equivalent action metadata. |
| Confirmation branch (line 3487) | An absent CONFIRM_MODE_STRING produces null; a present null string becomes empty. Accept/cancel labels are read only when that property is present. CardAction.Create receives those distinct values. | Local library search retains picks and uses unconditional explicit confirmation. It has no matching optional per-card confirmation descriptor. Empty and absent metadata cannot be collapsed if such a descriptor is added. |
| Menu branch (line 3525 onward) | ACTION_ALT_MENU is carried into the action; ACTION_ALWAYS_MENU sets the card-level menu flag. | The library inspection menu is always offered for an active card. No equivalent server-supplied override is established. |

`PromptBoxViewModel.Execute` confirms the consumer distinction: non-null
ConfirmModeString enters the confirmation branch, with whitespace/empty text
selecting localized defaults. Other earlier interaction-mode branches can bypass
that branch; this is not a universal rule for every click.

This evidence explains where the metadata comes from, but does not reveal the
values sent for an actual library search or fail-to-find action. Consequently it
does not justify changing local search to immediate submission, nor calling its
extra confirmation superior. The concrete remaining comparison is a library
offered-action inventory with action count, priority, parent/group, menu override,
confirmation-property presence and target requirements for each search scenario.
Unavailable scenario values remain unknown rather than inferred from this generic
producer. No gameplay implementation or runtime verification changed in this pass.

## Manual inspection boundary (2026-10-23)

Own-library honour-system Look and Stop looking now use ManualLibraryCoordinator,
ManualLibraryInspection and the existing private inspection projection. They do not
emit LibrarySearched or constitute the rules-based search procedure. The client uses
the existing inspection panel and shared manual-action submission/retry service.
These controls remain an honour-system extension: no recovered MTGO method mapping
or superiority claim is established by implementing them. Rules-based search action
selection is separately compared below. Manual search/order controls remain open.

## Library search menu selection (2026-10-23)

LibraryRevealComponent offers “Select for search” only for an actively inspected
card with exactly one matching option in the current server search prompt.
Historical disclosures, unavailable cards, ambiguous alternatives and pending
submissions cannot use this route. Changing the prompt closes its stale menu.
The output delegates to GameBoardComponent.pick and ChoiceSelectionService;
there is no second selection store or network submission implementation.

This is a partial counterpart to the recovered Card_View action menu and
CardViewModel.ExecuteAction route. Local selection still requires explicit
confirmation; this does not establish MTGO's conditional dispatch parity or
claim an improvement over it.

Evidence: two-player table 0c636013-bef5-4cbd-b160-1f758e46bd00 completed
the search after the menu selected a Mountain in the existing prompt. The
pending prompt and library count were unchanged before confirmation. The test
then verified tapped destination, count agreement and private inspection expiry.
Client log: library-search-menu-live.log. Captures inspected:
library-search-inspection-search-selected.png and
library-search-inspection-menu-landscape.png. Component regression coverage
checks exact option delegation, pending rejection, stale prompts, historical
cards, absent options and ambiguous alternatives.
### Multi-card search menu coverage

A component integration test drives the real shared menu with three offered
cards and a two-pick maximum, delegating its events to ChoiceSelectionService.
It verifies ordered picks, rejection of a third pick, deselection, disabled
pending action, replacement selection and no immediate answer submission.
This is local interaction evidence, not a live multi-card server-search test
or proof of MTGO action-dispatch parity. No production behavior changed in this
coverage follow-up.
### Live multi-card search follow-up

The previous live-evidence limitation is now closed for a two-player, two-card
search using the existing Blighted Woodland and Forest definitions.
verify-library-multi-search-live.js pays the normal four-mana activation and
sacrifice cost, selects from the inspection menu, rejects a third pick, deselects
and replaces a pick, then confirms in the existing prompt. Both viewers observe
two new tapped Forests, a library count reduced by two and expired inspection.
The other viewer receives no private inspected cards or choice cards while the
search is pending. Direct choice privacy assertions also pass.

Table: 40c8faa3-a362-45c1-bacc-b5853a9f627b.
Log: library-multi-search-live.log.
Reviewed desktop captures: library-multi-search-selected.png and
library-multi-search-resolved.png. No card definitions or production behavior
were changed for this acceptance test. This does not prove every search variant,
four-player behavior, or exact MTGO menu-dispatch equivalence.
## Acceptance standard (2026-10-23)

Four-seat follow-up: `verify-library-multi-search-live.js --four-seats` reuses
the same paid Blighted Woodland search and menu interactions with four browser
seats. Table 50c845c8-4036-4063-a1a9-eefb5c85f58c passes the two-pick limit,
deselection/replacement, one confirmation, tapped arrivals and count reduction.
All three observers receive no inspected cards, choice cards, options or last-look
contents during the private search. Every final view has expired active inspection.
Direct-choice privacy checks also pass. Client log:
library-multi-search-four-live-final.log. Reviewed captures:
library-multi-search-four-selected.png, private-observer-1.png and resolved.png
under e2e/screenshots (each filename has the library-multi-search-four prefix).

This extends local interaction evidence to four seats. It uses the casual land
test deck, not Commander deck validation, and establishes neither full responsive
layout acceptance nor exact MTGO dispatch parity. No production code changed.
The first attempt lost all connections when local services stopped; a retry
before the client was available failed to connect. Only the final completed run
is acceptance evidence.

Every recovered library method requires an explicit local class/method counterpart
or an explicit missing verdict. A difference requires a concrete benefit,
tradeoffs and verification before it can be accepted as an improvement. Cleaner
class names, lower method counts and passing unrelated suites do not establish
superiority. Record responsibility, counterpart, behavioral evidence, differences
and verdict together. Unknown MTGO server algorithms remain unknown.

Recent owner-selected top/bottom movement has serialized engine tests and a
two-browser RevealUntil acceptance run (table
da5277e7-2c2e-4ecb-9839-0134c2672c6e). Its local responsibility is
EffectInterpreter.OrderLibraryMovement, shared Ask/Relocate and
ViewProjector.ChoiceCards. No recovered MTGO ordering method has yet been mapped
to that implementation: its MTGO verdict is **unverified**, even though the tested
local behavior passes. See LIBRARY_ACCEPTANCE_AUDIT.md for scoped evidence.

## Authoritative current method register (2026-10-23)

### Library inspection details menu

LibraryRevealComponent now reuses CardOptionsMenuComponent for right-click,
ContextMenu and Shift+F10 entry. Card details uses the existing gameplay card
modal; plain click remains available. Active-inspection menu data is refreshed
from the authorized projection and discarded if the selected instance disappears,
along with an open detail. Historical entries remain inspection-only.

The live search on table 715475e2-c686-41f6-a940-895225441c48 verified mouse and
Shift+F10 entry, menu dismissal, card details without answering the outstanding
search, and menu bounds in phone landscape. Desktop menu/details and landscape
menu captures were inspected. Unit coverage includes permission expiry for an
open menu. `npm run verify` passed with 1,818 tests, coverage and production build
(`mtg-client/library-inspection-menu-verify.log`); the existing stylesheet budget
warning remains. Live log: `mtg-client/library-inspection-menu-live.log`.

This supplies the details-menu portion of a counterpart to Card_View's right-click
route. It does not implement MTGO's complete offered-action menu: library search
selection still belongs to the current prompt, and broader action dispatch remains
unverified. Initial capture selectors were corrected after inspecting the actual
shared modal (a body-mounted `.card-modal`, not the board's separate native-dialog
preview).

### Search recovery after accepted-response loss

`verify-library-search-live.js --accepted-response-loss` sends one real search
answer, withholds ViewUpdated from the acting browser, waits for server acceptance,
stops that browser's hub and throws a controlled invocation error. Independent
observer connections verify that the search actually finished. The acting browser
still displays its retained selection until reload; reload rejoins and receives
the committed state, removes the obsolete inspection/choice, and shows exactly
one library card moved to the tapped battlefield destination. Table
22e998f1-9814-48b8-b56a-bb7282f004cd passed, including the existing privacy and
library-count checks. Lost/recovered captures were visually inspected.

This exposed incorrect certainty in the existing error text. Choose invocation
errors now say `Choice status unknown. Reload to sync.` rather than claiming the
action was never sent. Evidence: `mtg-client/library-search-accepted-loss-live.log`
and `e2e/screenshots/library-search-accepted-response-{lost,recovered}.png`.
The fault is injected at the browser delivery boundary; this does not establish
automatic reconnect behavior under actual packet loss or MTGO recovery parity.
Full client verification passed: 1,817 tests, coverage gate and production build
in `mtg-client/library-search-recovery-verify.log`; the existing card-modal
stylesheet size warning remains.

### Library ordering and delayed-window boundary verified from retained code

Read-only inspection of the retained decompilation establishes the following
library-specific boundaries. These are static observations, not a live MTGO run
or REA Evidence output; no REA inspection tools were available in this session.

| Method/configuration | Observed library behavior | Local consequence |
| --- | --- | --- |
| PlayerViewModel.ConfigureZones; DuelSceneViewModel.ZoneSorting and InitializePlayerInfo | LibraryZone is CardZone 2. ZoneSorting contains only CardZone 3 (graveyard). Library ZoneViewModel is therefore created with sortByTimestamp=false. | Timestamp-sorted insertion is not a missing library requirement. Do not add it to the library counterpart based on the generic ZoneViewModel.Add method. |
| ZoneViewModel.Add / Remove | With library sorting disabled, Add appends and Remove removes the object. | LibraryRevealComponent projects the supplied authorized array. Append/remove responsibility is represented; upstream ordering and update timing are still unverified. |
| Overlays.AddCardSelectorsAsChildren | Collection view groups on HasActions, sorts HasActions descending and Name ascending. | This is display sorting above the library model. LibraryRevealComponent currently preserves supplied order and has no action grouping. A presentation policy must not reorder the server's library or a top/bottom choice answer. |
| DuelSceneViewModel.InitializePlayerInfo; CardSelectorManager.UpdateSelectors / UpdateDelayedSelectors | Both own and other-player library selectors are registered with isDelayUpdateVisual=true. UpdateSelectors skips them; its Allies/HasActions condition does not govern this library path. UpdateDelayedSelectors uses GetShouldZoneDisplay and creates/destroys on a change. | Do not suppress an authorized teammate library inspection merely because its cards have no available actions. Reconcile library visibility with the delayed branch, not the ordinary selector branch. |
| CardSelectorManager.GetShouldZoneDisplay | Normally requires Count>0. When RevealPilesToPilerOnly is true, also requires PilePanelChoosePile. | Nonempty authorized local sections cover the normal count gate. Pile-specific authorization/presentation is still unmapped. |

Retained source anchors: DuelSceneViewModel.cs lines 140, 5696, 5793–5799;
PlayerViewModel.cs ConfigureZones; ZoneViewModel.cs lines 404–442;
Overlays.cs lines 169–171; CardSelectorManager.cs UpdateSelectors,
UpdateDelayedSelectors and GetShouldZoneDisplay. SHA-256:
ZoneViewModel.cs `5ca30fec182ff525b7c080e7be0a810e26f1ec4a95becce1e0af36d70135da8b`;
CardSelectorManager.cs `c9f37aff81ec00eae5fae4ed156d5ba948e05dff9b3f6ca42c29a2843cb9f762`.
The current local component was reread at
`mtg-client/src/app/components/library-reveal/library-reveal.component.ts`.
No comparative usability benefit or full method parity is claimed.

Local implementation follow-up: `LibraryInspectionPresentation.groups` now
groups authorized inspection cards using offered choice-card instance IDs, or
server-provided castable IDs when no choice is pending, and sorts names within
each group. LibraryRevealComponent renders the groups; historical reveal/look
arrays retain their original order. Input arrays and choice answers are never
sorted in place. Tests include duplicate names, non-card options, redacted choices,
unavailable castable IDs, stable ties and ordered-choice input preservation.

`npm run verify` passed with 1,817 tests, the changed-file coverage gate and a
production build. The existing 18.05 kB card-modal stylesheet budget warning
remains. Live table c77324f0-b919-40fc-a6ab-736aa8c57e20 verified Mountains offered
by Evolving Wilds appear before inspection-only Evolving Wilds cards, private
inspection on one seat only, search completion and permission expiry. Desktop
and landscape captures were visually inspected; portrait displays the existing
rotate-device gate, so it does not prove a usable portrait inspection panel.
Evidence: `mtg-client/library-inspection-groups-verify.log`,
`mtg-client/library-inspection-groups-live.log`, and
`mtg-client/e2e/screenshots/library-search-inspection*.png`.

This is a partial counterpart to Overlays' collection-view grouping/sorting.
MTGO HasActions encompasses more action types than this local protocol exposes;
exact culture-sensitive collation and all action transitions remain unverified.
No superiority claim is made. The capture's initial refresh/input races were
investigated using actual screenshots and hit-target geometry. A later pointer
run still timed out after opening a card detail; this pointer interaction remains
unresolved at that stage. The then-current harness waited for the refreshed board and stable summary,
then activates it with Space. That full two-player run passed on table
c14b62bc-6eaa-4a7f-aa78-f538a7484c97, retained in the live log above. This verifies
keyboard access and grouped presentation, not reliable pointer access.

Pointer follow-up: event tracing reproduced the header moving from y=292.53 to
y=348.58 between pointer-over and pointer-down, delivering the click to the stack
card. A second trace showed stack art already loaded at a constant 76 px while
the header moved; the changing hand-hover preview caused flex redistribution.
The board now prevents stack flex shrinking while an active library inspection
exists and caps that scrollable stack region at 40vh. The regression explicitly
hovers a hand card, moves onto the inspection summary, asserts less than 1 px of
vertical movement, then opens the inspection by mouse. The full two-player search
passed on table 98aa0edb-8e39-4a6c-afd2-c9a27e865324. Desktop and landscape
captures were inspected. Evidence: `library-inspection-pointer-trace.log`,
`library-inspection-pointer-image-trace.log`, and
`library-inspection-pointer-fixed.log` in mtg-client. This supersedes the earlier
unresolved pointer verdict for this reproduced hand-hover scenario; it does not
establish all input-method parity with MTGO. `npm run verify` also passed after
the fix: 1,817 tests, changed-file coverage and production build; retained in
`mtg-client/library-inspection-pointer-verify.log`. The existing card-modal
stylesheet budget warning remains.

### Presentation-path correction: programmatic library panels

#### Input and submission guards traced (2026-10-23)

The normal route is DuelScene.DuelScene_MouseUp →
DuelSceneViewModel.OnMouseUp → Card_View.ProcessMouseUp →
DuelSceneCardViewModel.ExecuteAction → DuelSceneViewModel.ExecuteAction →
PromptBoxViewModel.Execute → FinalExecuteAction → Game.ExecuteAction.
This is a conditional route, not a guarantee that every card click sends.

| Recovered method | Guard/behavior established by retained code | Local comparison and required scenario |
| --- | --- | --- |
| DuelScene.DuelScene_MouseUp | 200 ms timer; second release still routes when no pile zone is active. | Do not describe this as blanket double-click suppression. Verify local duplicate delivery independently. |
| DuelSceneViewModel.DoesMouseDownTimeCountAsSingleClick / OnMouseUp | Normal click requires elapsed time <=500 ms and squared movement <=36; ignores animated card release and consumes completed two-button zoom. | ChoiceControls uses browser click events without an explicit corresponding policy. Test long press, drag/scroll release and touch before claiming equivalent input behavior. WPF coordinates are not physical pixels. |
| DuelSceneViewModel.OnMouseUp | SelectTargets routes to LeftClickDuringSelectTargets and returns; Distributing returns; ordering has a separate route. Ordinary card dispatch requires an Actions collection and excludes pile-zone cards. | Local choice kinds and offered IDs are counterparts only in responsibility. Do not reuse a generic single-choice rule for every targeting/ordering operation. |
| PromptBoxViewModel.Execute | Rejects absent game/action/viewmodel or disposed viewmodel. Card actions requiring targets can enter SelectTargets instead of sending. Non-null ConfirmModeString opens a confirmation dialog (including default text for an empty string). | Immediate dispatch from Card_View is not necessarily immediate network submission. Search-specific action metadata is still needed to decide whether local unconditional search Confirm should remain. |
| PromptBoxViewModel.ConfirmBeforeExecutingAction | Calls FinalExecuteAction only when the dialog result is true. | Conditional confirmation needs an explicit counterpart if such metadata is introduced. Cancellation must not submit. |
| PromptBoxViewModel.FinalExecuteAction / Game.ExecuteAction | Applies HoldPriority modifier, then delegates to GameAction.Execute and raises the game-action notification. | Shared local transport remains appropriate; this evidence does not establish server authorization or replay guarantees. |

WaitingForServer is set by Game_GameActionPerformed and cleared after updates.
The inspected OnMouseUp/Execute methods do not use it as a blanket input gate;
the extracted resource references found are status indicators. Do not claim
absence of all other guards elsewhere in the client. Local pending-send
coalescing remains a tested local property, not proven MTGO method equivalence.

Current local ChoiceControls template and ChoiceSelectionService were reread:
all search choices retain selection and expose Confirm; option buttons use
click handlers; the controls have explicit Details buttons but no contextmenu
handler. These are concrete differences, not evidence that either UI is better.
Next implementation decisions require matching offered-action semantics; absent
that evidence, preserve legal search bounds and record the difference explicitly.

Source identities: DuelSceneViewModel.cs SHA-256
`c49b06f38525aa9d0d9687941d59d5c6bbef1649bbd792169667243b11c208cb`;
PromptBoxViewModel.cs
`e8cc28a4fa67ec56567a20645db5acc66511ccf69d8196a70a8508157a00b50f`;
Game.cs `17c0bb8cefc655603587f6b6c2e871c6b6ddacef5783a6456d75808cd642a034`.
No new runtime observation or test execution in this trace.

New static trace resolves the presentation link previously left open. In the
same retained DuelScene.dll, Overlays constructor binds CardSelectorManager to
`CardSelectors`. UpdateCardSelectorManager subscribes to CollectionChanged;
CardSelectorCollectionChanged calls AddCardSelectorsAsChildren or
RemoveCardSelectorsFromChildren. AddCardSelectorsAsChildren creates a
FloatingPanel containing HorizontalPanelView in Library mode. It does not
instantiate CardSelectorView or bind its select/OK commands on this path.

Consequently, the CardSelectorViewModel select/OK and CardSelectorView pointer
rows below are **recovered-method comparisons only, not established counterparts
for the displayed library interaction**. The previous select-then-confirm local
implementation must not be described as MTGO library parity on that evidence.
This does not prove CardSelectorView is unused elsewhere.

| Newly traced method/resource | Established shipped-code behavior | Local consequence |
| --- | --- | --- |
| Overlays constructor / UpdateCardSelectorManager / CardSelectorCollectionChanged | Binds manager and subscribes/unsubscribes collection changes; adds/removes existing/new panels. | LibraryRevealComponent.disclosures$ remains a partial lifecycle counterpart. |
| Overlays.AddCardSelectorsAsChildren | Binds ZoneVM.Cards, groups by HasActions, sorts HasActions descending then Name ascending; creates movable/resizable panel, owner title, count and saved window settings. | Explicit sorting/grouping and window-behavior mapping now required. Do not reorder the authoritative library to reproduce presentation sorting. |
| Overlays.RemoveCardSelectorsFromChildren | Removes FloatingPanel by matching selector DataContext identity. | Owner-keyed section expiry is a responsibility counterpart; exact lifecycle timing still unverified. |
| HorizontalPanelView Library template | Uses SmoothScalingLibraryPanel and Card_View with CardWeight VerticalZone. No CardSelectorView or selector OK control on this path. | Trace shared card interactions rather than legacy selector commands. |
| Card_View.ProcessMouseUp | For one non-alt offered action, left click dispatches directly unless AlwaysShowActionMenu. Other branches construct offered-action menus; right click also merges card tooltips. | Local retained-search-selection plus Confirm is a difference requiring alignment or demonstrated benefit. Do not change it until caller gates and prompt-specific action semantics are traced. |

Recovered ProcessMouseUp calls DuelSceneCardViewModel.ExecuteAction for its
chosen action; Item_Click also submits its offered action. Cast-specific branches
in this shared control do not authorize expanding this task beyond library work.
The full upstream mouse-routing/interaction-state gates and actual search
server actions remain unverified. This is static shipped-code evidence, not a
live MTGO observation or a recovered server algorithm.

Inspection used existing ILSpy 11.1.0.9782 with --disable-updatecheck and retained
DuelScene.dll (SHA-256 recorded below); MTGO was not executed. All 68 BAML/XAML
resources were extracted into ignored `decompiled/selector-resource-audit` for
the resource search. The decisive link was recovered from Overlays code, not a
text binding in the parent XAML. No REA MCP tools were available; these are
ordinary local CLI results, not REA Evidence records.

Retained output SHA-256:
- Overlays.cs: `3dcf3bc15091fd19af82ed3afab1d48303fdbec04169b714a101aaa6ee14ce39`.
- HorizontalPanelView.cs: `a3457870485e4203fe8057ff9b369e7701bdab55e1e70cde90858ad561737635`.
- Card_View.cs: `5c089333a47fba20073a79bd1612039d62eea6ca450542b513b4e6b28d34b08a`.
- view_horizontalpanelview.baml.xaml: `795ee2ae6e1987b421d46242df27707906cc08efce0858fcb259e946696904e4`.

This table supersedes implementation verdicts in the historical evidence below.
It covers the recovered methods inventoried in this document, not every method
in the MTGO application. A responsibility counterpart is not full behavioral
parity. No row currently establishes that our implementation is superior.

| Recovered MTGO method | Current local counterpart | Verdict and outstanding acceptance evidence |
| --- | --- | --- |
| CardSelectorViewModel.CardSelect_CanExecute / CardSelect_Execute | ChoiceSelectionService.select | Partial: offered single-search selection is retained and repeated selection does not toggle it off. Definition lookup versus instance IDs differs; multi-selection is outside the recovered method's evidence. |
| CardSelectorViewModel.CardSelectOk_Execute | ChoiceControls confirmation → SignalRService.choose | Partial: explicit selection submission mapped. Resolve the shipped selector's presentation/binding route before claiming runtime equivalence. |
| CardSelectorViewModel.CardSelectOk_CanExecute | ChoiceSelectionService.canSubmit | Different/unverified: local minimum/maximum bounds allow legal zero-selection searches; MTGO selector OK requires a selection. Its separate fail-to-find route remains unknown. |
| CardSelectorViewModel.CardSelectClose_CanExecute / CardSelectClose_Execute | LibraryRevealComponent inspection collapse and detail close | Different/unverified: recovered command is disabled/no-op; local presentation may collapse without cancelling the decision. Benefit and input-binding equivalence are not verified. |
| CardSelectorViewModel.CardCountChangedHandler; ZoneViewModel.Count | LibraryRevealComponent.disclosures$ authorized card arrays | Partial: authorized content count is separate from public LibraryCount. Exact update timing and selector count contract remain unverified. |
| CardSelectorManager.GetShouldZoneDisplay | LibraryRevealComponent.disclosures$ nonempty authorized sections | Partial: available objects drive display. Pile-only branch remains unmapped. |
| CardSelectorManager.CreateVisual / DestroyVisual / UpdateDelayedSelectors | LibraryRevealComponent.disclosures$ owner sections and expired-detail cleanup | Partial: owner grouping, expiry and historical separation implemented. Independent window placement and MTGO update boundaries remain unverified. |
| DuelSceneViewModel.InitializePlayerInfo | LibraryRevealComponent.disclosures$ owner grouping | Partial: separate owner sections implemented. Simultaneous multiple owners have component coverage, not a live multi-owner capture. |
| DuelSceneViewModel.Game_GameChanged | LibraryRevealComponent.disclosures$ state subscription | Partial: projection updates presentation; exact UpdateViews → UpdateDelayedSelectors → WaitingForServer boundary equivalence is unverified. |
| CardSelectorView.LayoutView_MouseLeftButtonDown / LayoutView_MouseLeftButtonUp / SelectCard | ChoiceControls selection event → ChoiceSelectionService.select | Different/unverified: local button click versus pointer movement filtering. Drag-versus-click behavior requires a focused comparison. |
| ZoneViewModel.Add / Remove | LibraryRevealComponent.disclosures$ array projection; LibraryInspectionPresentation.groups | Partial: displayed contents update; offered-action grouping/name sorting implemented separately from model order. Library does not use timestamp sorting. Broader HasActions and exact update timing remain unverified. |
| ZoneViewModel.Move | ChoiceSelectionService.move is only a candidate | Unverified: recovered method changes display order. No recovered library caller establishes it as a counterpart for library top/bottom ordering. |
| ZoneViewModel.ViewUpdate | ChoiceSelectionService.select covers option membership only | Gained/no-action signals remain a comparison gap. The retained attribute-counter producer targets graveyard only; see the producer trace below. |
| DuelSceneCardViewModel.UpdateSelectableState | ChoiceSelectionService.select covers option membership only | Partial: active/targetable/interaction mode and AlwaysShowActionMenu equivalence unverified. |
| DuelSceneCardViewModel.ExecuteAction; Game.ExecuteAction / CardAction.GetMessage | SignalRService.choose → server ChoiceSystem validation | Partial responsibility match; library-specific action availability inventory and protocol equivalence not established. |
| Game.DetermineSpecialStepTypeFromTurnStep / DetermineSpecialStepTypeFromInteractElement | OpeningHandPrompts.Declaration → typed choice rendering | Different/unverified: typed protocol replaces recognition of flags/text. No verified superiority claim. |
| Game.ProcessTurnStepElement (mulligan action construction) | OpeningHandPrompts.Declaration; LibraryChoiceShortcuts.resolve | Partial: offered Keep/Mulligan plus recovered 3/4 defaults implemented. Full default-button behavior remains unverified. |
| Game.ProcessTurnStepElement (generic prompt construction); PrimitiveAction.SetMessage; DuelSceneViewModel.OkPressed | ChoiceControls → shared answer transport | Partial generic route only. Library fail-to-find and selector-confirmation keyboard dispatch remain unverified. |
| PromptBoxViewModel.Execute / FinalExecuteAction | ChoiceControls → board → SignalRService.choose | Partial: submission/coalescing and reload recovery after simulated accepted-response loss verified. Automatic reconnect under actual packet loss and MTGO comparative recovery behavior remain unverified. |
| PromptBoxViewModel.DeactivatePrompt | SignalRService.choiceSubmission and ChoiceControls.sendingLibraryAction | Superseded by the current reconciliation: known search/opening submissions now hide alternatives and disable the submitted control. Missing metadata retains disabled fallback. |
| KeyboardInput.ConvertToHotKey | LibraryShortcutBindingPolicy.capture; LibraryChoiceShortcuts.resolve | Partial: configurable Keep/Mulligan keys and modifiers implemented and live-tested. Entire global dispatcher is outside this counterpart. |
| KeyboardInput.KeyDown | LibraryChoiceShortcuts.resolve | Different/unverified: offered-action dispatch is guarded against text entry, dialogs, composition, repeats and pending sends. Broader suppression is not established as superior. |
| KeyboardInput.DoesKeyComboExist | LibraryShortcutBindingPolicy.conflicts; UiPreferencesService.setLibraryShortcut | Partial: collision rejection verified between the two opening-hand actions; global shortcut collision coverage missing. |
| KeyboardInput.RestoreDefaults | UiPreferencesService.resetLibraryShortcuts | Partial: reset and persistence verified for these two device-local bindings; global settings/account synchronization not mapped. |
| KeyboardInput.AddKeyDownEventHandler / RemoveKeyDownEventHandler | ChoiceControls listener → LibraryChoiceShortcuts | Partial: choice-scoped listener instead of action-specific global delegates. Full lifecycle/dispatcher equivalence unverified. |

Evidence for the updated shortcut rows: current policy, selection service and
shortcut resolver were reread; retained live log
`mtg-client/e2e/screenshots/library-configurable-shortcuts-live.log` reports the
remap/collision/unbind/reset/reload/old-key/two-player completion pass. The retained
`mtg-client/library-configurable-shortcuts-verify.log` reports build completion.
These are prior run artifacts, not new test executions in this reconciliation.
Other rows retain the scoped source/test evidence below; no runtime MTGO
observation was added here.

Acceptance sequence: resolve selector presentation/input dispatch; inventory
library-specific offered actions; map each missing responsibility; then verify
intentional differences against the same behavioral scenarios. Engine draw,
shuffle, search, ordering and mulligan algorithms remain independently tested
implementations: the corresponding MTGO server code has not been recovered.

## Historical evidence and superseded verdicts

Entries below preserve discovery provenance. Statements such as "remapping
missing", "combined list" and "BAML not recovered" describe earlier states;
use the authoritative register above for current status.

### Configurable opening-hand shortcuts implemented (2026-10-23)

LibraryShortcutBindingPolicy now owns key/modifier normalization, capture and
collision checks. UiPreferencesService persists bindings locally, rejects an
assignment used by the other opening-hand action and resets only these bindings.
Settings exposes Keep/Mulligan capture fields, unbind, reset and collision feedback.
ChoiceControls reads current bindings for both labels and authorized-option dispatch.
Default keys remain 3/4; modifier combinations work only when explicitly assigned.

Method counterparts: ConvertToHotKey -> LibraryChoiceShortcuts.resolve plus
LibraryShortcutBindingPolicy.capture; DoesKeyComboExist -> conflicts;
RestoreDefaults -> UiPreferencesService.resetLibraryShortcuts. Scope is the two
opening-hand actions, not MTGO's entire configurable keyboard dispatcher. Our
repeat/composition/dialog/pending-send guards remain intentional differences.
No new library-selector OK/Cancel shortcut is inferred from the generic MTGO route.

Live table 8d6a013f-d0f5-4a61-86fd-1f3c1a01b5f0 passes UI remapping to Shift+m/k,
collision rejection, unbind/reset, reload persistence, old-key suppression and
two-player opening-hand completion. Captured desktop and 375x667 editor states,
including the lower controls. Evidence: client
e2e/screenshots/library-configurable-shortcuts-live.log and
verify-library-configurable-shortcuts-live.js. Bindings are device-local, not
account-synced. Global shortcut collision coverage remains outside this mapping.

### Keyboard dependency recovered and dispatcher mapped (2026-10-23)

Retrieved SharedResources.dll from the same official version directory as the
retained application manifest (3.4.159.4708.20261014120111). Verified 8,498,688 bytes
and manifest SHA-1 digest `CMP6+KtmfXgrum0z5sEVONo3OV8=` before inspection.
SHA-256: `948832b84b67d8945b91cfa2c763fc9bd28ea19d7681c9256b4d5c172cdadc47`.
No installation or target execution; local ILSpy CLI static inspection only.
This supersedes the missing-dependency status below.

Recovered Shiny.Utilities.KeyboardInput and Shiny.Enums.GameHotKey:

| Method | Recovered responsibility | Local mapping / verdict |
| --- | --- | --- |
| ConvertToHotKey | Resolves configured, non-unbound key combinations with shift/control/alt state. | LibraryChoiceShortcuts maps fixed offered mulligan defaults and suppresses modifiers. **Partial; configurable mapping missing.** |
| KeyDown | Skips non-function keys originating in TextBoxBase, normalizes SystemKey, dispatches registered handlers, suppresses repeated Undo. | Local shortcut policy ignores text-entry, repeated/composing/modified input and pending sends. **Partial; broader repeat suppression is a deliberate difference, not established superiority.** |
| DoesKeyComboExist | Detects another action assigned the same key/modifier combination. | **Missing:** no library shortcut remapping/collision policy. |
| RestoreDefaults | Restores stored duel/general key settings and reloads binding containers. | **Missing:** fixed defaults are not a resettable settings implementation. |
| AddKeyDownEventHandler / RemoveKeyDownEventHandler | Registers/removes action-specific delegates. | ChoiceControls listener delegates to LibraryChoiceShortcuts. **Partial:** local routing is choice-scoped, not a configurable global dispatcher. |

Recovered enum values resolve the existing parent trace: eOK is 13, eCancel is
10 and eRevealMorphs is 16. DuelSceneViewModel registers eOK with OkPressed;
OkPressed invokes the offered PromptBox OK/Done action. This still does not route
to CardSelectOk_Execute or establish Enter as a library-selector confirmation key.
Do not conflate the generic prompt action with selection confirmation.

KeyboardInput.cs SHA-256:
`73fff8c6dae0636aae1802222812cd6bbcc82c26a8ad4b38cbc464bf52359936`.
Next keyboard implementation gap is explicit: configurable bindings and collision/
reset behavior, with local choice authorization preserved. Selector presentation
wiring and library-specific runtime dispatch remain separately unverified.

### Temporary-window exclusion and keyboard dependency (2026-10-23)

Recovered TemporaryZoneViewModel and TemporaryZoneWindowViewModel. The latter's
ShowWindow creates a floating window, assigns its owner/data context and forwards
KeyDown/KeyUp to the owner. PlayerViewModel's temporary-zone construction at
1554–1573 registers graveyard, exile, revealed, shields, commander and companion;
it does not register LibraryZone there. This public-zone path must not be used
to fill the missing library-selector presentation link.

IL inspection of DuelScene establishes the exact missing keyboard dependency:
`[SharedResources]Shiny.Utilities.KeyboardInput` (field and constructor calls in
retained DuelScene.il, 1976 and 2064). SharedResources.dll is absent from the
currently retained artifact directory. Further keyboard-dispatch analysis needs
that exact-version assembly, rather than more inference from button names or
numeric hotkey values. No dependency was downloaded or target executed here.

Decompiled source SHA-256:
- TemporaryZoneViewModel.cs: `ee368f8b6b5fea1549b2ecfcdc9267b637bafeb63c722b63a7c87e8f8fa16d1d`.
- TemporaryZoneWindowViewModel.cs: `a2ed7e3a0e0131ee0d0d0c04bdd5c6fd54235e63257bc1d4b9d6ae920c5af1fe`.

### Parent-view wiring audit (2026-10-23)

Recovered DuelScene.xaml, duelview.xaml, multiplayerview.xaml, overlays.xaml,
TemporaryZoneWindow.xaml, TemporaryZoneView.xaml and DuelResources.xaml with the
same local ILSpy resource command. Decompiled Shiny.Play.Duel.DuelScene separately.
The inspected parent resources do not contain a CardSelectors.Collection binding
or a CardSelectorView template. CardSelectorManager still demonstrably registers
library zones and creates selector view models, but these artifacts do not finish
the route from those objects to a displayed CardSelectorView. Do not claim that
the legacy-looking selector resource is unused either: other resources, styles
or programmatic view creation may supply the missing link.

DuelScene.cs subscribes its host window's KeyDown to HandleKeyDown (lines 125–139).
HandleKeyDown (219 onward) forwards to KeyboardInput.KeyDown except for a repeated
hotkey branch. This establishes a parent keyboard route separate from selector
XAML. KeyboardInput's implementation is not present in the retained type inventory;
the numeric hotkey comparison is not promoted into an Enter/Escape library rule.

The earlier select/confirm mappings are comparisons with recovered command
methods, **not verified current MTGO library UX**. Until the presentation link or
a runtime capture is established, a method match alone cannot certify current
client behavior. This distinction applies to zoom/sort/layout controls as well.

SHA-256:
- DuelScene.cs: `7c68a45f8364f50b84dd5c6e62ac17a01beaa93bc9d5679392959ecebf305938`.
- DuelScene.xaml: `453e62511a1b2b11c77b78587c307d294333f191d71ca0b6881025fb22543ad3`.
- duelview.xaml: `c6286e47ac8c4a0c911f4866d122d67d7fd79fc472b01275da3059110d68dcf2`.
- multiplayerview.xaml: `b375e9f56a0c6c7737b5dbb9273d201b5c7169e69fa5a325afa12684e8075600`.
- overlays.xaml: `387fd32d464ae5bff3738d65b8a8a846a41bf401c9a547c5e773bf6f3a536492`.

No target execution, runtime observation or production-code change in this pass.

### Selector BAML resource recovery (2026-10-23)

The installed ILSpy CLI 11.1.0.9782 supports `--list-resources` and
`--resource`; recovered DuelScene.g.resources/view/cardselectorview.baml as
decompiled/CardSelectorView.xaml and cardselectordialogview.baml as
decompiled/CardSelectorDialogView.xaml. Used --disable-updatecheck, existing local
artifacts and ordinary CLI tooling. No target execution or REA Evidence claim.

The CardSelectorView resource declares CardZoomSelectionView,
CardSortSelectionView and CardLayoutSelectionView, feeding LayoutView's
ZoomPercentage, SortingPreset and Layout. It declares an OKbutton command
binding, but no InputBindings, KeyBinding, IsDefault or IsCancel attributes.
This closes resource extraction, not global keyboard dispatch: inherited styles
and parent-window handlers still need tracing before assigning Enter/Escape keys.

There is a wiring discrepancy: the resource spells its binding
CardSelectOKCommand, while the retained CardSelectorViewModel property is
CardSelectOkCommand. The resource also binds LayoutVM, absent from that recovered
class. CardSelectorView's code-behind loads this resource and uses the view model
for pointer selection, but does not explain those binding differences. Do not
silently normalize the names or claim the resource's controls work at runtime.

The local library inspection/choice components do not expose equivalent user
zoom, sorting or layout selectors. These are **unverified resource-level parity
items**, not yet verified missing runtime features. CardSelectorDialogView is a
separate selectable-card list resource; no library-specific caller has been
established for it, so it must not be substituted as library evidence.

SHA-256 provenance:
- DuelScene.dll: `3799b8fe160304405416153b8c80e96aa8196ef961cc510abe45aa4b0fd208d1`.
- CardSelectorView.xaml: `3eb0318074f2d3e560fd3bbc9d63b46c559bf75b642482832890cd73c2c252d9`.
- CardSelectorDialogView.xaml: `89b312c87e41d678fb70ca2d6de7765392dab947cd7f0ba6d7d003b5e64b9097`.

This supersedes earlier statements that selector XAML/BAML had not been recovered.
No client shortcuts or controls were changed on ambiguous static evidence.

### Zone presentation and offered-action trace (2026-10-23)

Inspected retained ILSpy output with ordinary filesystem tools; no REA MCP
tools are present in this session, no new decompilation was performed, and MTGO
was not executed. These findings expand the inventory rather than certify parity.

| Recovered method | Observed responsibility | Local counterpart / verdict |
| --- | --- | --- |
| ZoneViewModel.Move, lines 432–441 | Rejects timestamp-sorted zones; moves an item within the observable display collection. No game-action dispatch occurs in this method. | ChoiceSelectionService.move rearranges selected option IDs for an ordering prompt. Similar presentation operation, but no recovered caller connects ZoneViewModel.Move to library ordering. **Unverified**, not an engine LibraryOrder counterpart. |
| ZoneViewModel.Add / Remove, lines 404–430 | Inserts a displayed card, optionally by timestamp, or removes it. | LibraryRevealComponent projects authorized inspection arrays into owner sections. **Partial** presentation responsibility; timestamp ordering and equal-timestamp behavior are not mapped. |
| ZoneViewModel.ViewUpdate, lines 241–344 | Updates selectable states and emits gained/no-action signals based on offered card actions; also formats server-supplied attribute-match values/descriptions for configured zones. | ChoiceSelectionService.select validates offered option IDs. No equivalent server-supplied attribute-match descriptions/counter contract was established in the inspected local selection/inspection code. **Not established as a library requirement**: the producer trace below configures graveyard only. Generic option counts must not be called equivalent. |
| DuelSceneCardViewModel.UpdateSelectableState, lines 2808–2813 | Uses HasGameActions, interaction target mode, IsActive and IsTargetable; copies AlwaysShowActionMenu from the game card. | ChoiceSelectionService.select only establishes option membership and selection behavior. Full active/target-mode/menu equivalence is **unverified**. |
| DuelSceneCardViewModel.ExecuteAction, lines 2794–2801 | Marks a Cast action as being cast, then delegates to MainViewModel.ExecuteAction. | Existing shared choice transport is a responsibility counterpart for submitting a choice. This generic method does not enumerate library-specific offered actions. **Partial**; no inferred draw/search/shuffle menu. |
| CardSelectorViewModel.CardCountChangedHandler, lines 189–196 | Refreshes selector CardCount from authorized ZoneVM.Count. | LibraryRevealComponent derives inspection content from inspectedLibraryCards. **Partial**; selector count is distinct from public LibraryCount. |

The retained decompiled tree contains no caller of ZoneViewModel.Move found by
the focused source search. That is a limit of the retained tree, not proof that
the shipped application never calls it. The contextual library action inventory
still requires actual offered-action construction or a library-specific runtime
capture. Do not invent fixed MTGO Draw/Search/Shuffle menu entries from the generic
ExecuteAction method.

Evidence SHA-256:
- ZoneViewModel.cs: `5ca30fec182ff525b7c080e7be0a810e26f1ec4a95becce1e0af36d70135da8b`.
- DuelSceneCardViewModel.cs: `4896f80ef1ddb11cca70c8c6032ff23f13bc99fe72c86578da3dab8a0edf5925`.

Local sources inspected: src/app/game/choices/choice-selection.service.ts and
src/app/components/library-reveal/library-reveal.component.ts in mtg-client.
This documentation-only trace adds no test or live-UI evidence.

### Search completion dispatch trace (2026-10-23)

Recovered Game.ProcessTurnStepElement (retained Game.cs, 2584 onward) creates
separate primitive actions for offered OK, Cancel, Yes/No and PromptButton0–3
flags. The numbered prompt buttons use server-provided PromptButtonText. Button 0
can be marked default by the incoming DefaultAction and receive the OK hotkey.
PrimitiveAction.SetMessage constructs PromptActionMessage with ActionFlags,
MessageParameter and Timestamp. DuelSceneViewModel.OkPressed (2626 onward)
dispatches the offered OkPromptButton or DoneButton; it does not invoke
CardSelectorViewModel.CardSelectOk_Execute directly.

This establishes a generic prompt-completion route separate from selector OK.
It does **not** establish which flag/text is used for fail-to-find in a library
search: no retained search-specific server prompt or runtime capture identifies
that action. Do not label Cancel, Done, PromptButton0 or an empty card selection
as MTGO fail-to-find on this evidence alone. No MTGO target was executed.

Local counterpart chain: LibrarySearchPolicy.Bounds supplies minimum/maximum;
ChoiceControls renders Choose none only for minPicks == 0 and emits skip through
the existing answer route. Its Confirm control uses ChoiceSelectionService.canSubmit.
InterpreterTests.LibrarySearch has distinct qualified-search zero-choice and
unqualified-search mandatory-choice regressions. These establish local rules
behavior, not the missing MTGO dispatch mapping. This generic prompt trace is a
partial match; search-specific fail-to-find remains **unverified**.

### Per-owner implementation follow-up (2026-10-23)

The combined-list gap below has now been addressed in LibraryRevealComponent:
authorized inspectedLibraryCards are grouped by ownerId, named from players, and
tracked by owner ID rather than display title. No new access is requested or
granted. Historical disclosures retain separate stable keys. A component test
covers two owners, disjoint cards, name changes preserving an open panel, one
owner expiring and that owner's active detail closing. Live table
0e04841b-59bf-4de1-8f61-4a518c44eb6d verifies the owner-labelled private inspection,
refresh, ordering completion and no opponent disclosure; library-look-active.png
was inspected. Evidence: library-owner-inspection-live.log. Simultaneous multiple
owner panels are component-tested but not yet captured in a live game. Independent
floating window placement and exact MTGO game-update timing remain differences.

### Selector lifecycle trace supplement (2026-10-23)

Inspected retained decompilation, without running MTGO. This narrows the earlier
lifecycle mapping; it does not establish equivalent server timing.

| Recovered method / call site | Local counterpart | Verdict |
| --- | --- | --- |
| DuelSceneViewModel.InitializePlayerInfo, library registration at lines 5793/5798 | LibraryRevealComponent.disclosures$ owner grouping and disclosureKey | MTGO registers separate local/opponent library selectors, both delayed. Local authorized cards are also separated by owner with stable section keys. Inline collapsible sections versus independent selector windows remains a presentation difference without verified comparative benefit. |
| DuelSceneViewModel.Game_GameChanged, lines 3912–3924 | LibraryRevealComponent state subscription | MTGO calls UpdateViews before UpdateDelayedSelectors, then clears WaitingForServer. Our projection-driven subscription updates inspection and invalidates unauthorized details. Equivalent lifecycle responsibility; exact update-boundary parity remains unverified. |
| CardSelectorManager.UpdateDelayedSelectors | LibraryRevealComponent active section construction/removal | Creates/destroys the registered selector when authorized zone contents change between empty/nonempty. Library registration uses this path, not the immediate UpdateSelectors ally/HasActions filter. Do not apply that immediate filter to the library based on this method. |
| CardSelectorView.LayoutView_MouseLeftButtonDown / LayoutView_MouseLeftButtonUp / SelectCard | ChoiceControls selection event → ChoiceSelectionService.select | MTGO distinguishes pointer down/up movement before dispatching selection. Our button click routing is a platform difference; drag-versus-click equivalence has not been tested. |
| CardSelectorViewModel.CardSelect_CanExecute and CardSelect_Execute | ChoiceSelectionService.select | CanExecute returns true, but Execute rejects invalid inputs and cards lacking offered actions. Our method rejects IDs absent from supplied options. Responsibility mapped; definition-based MTGO lookup and instance-based local IDs remain different. |

The retained CardSelectorView code-behind loads cardselectorview.xaml and does not
expose its input bindings. Its generated OKbutton field is not evidence that Enter
or key 1 submits a library choice. XAML/BAML binding recovery is still required.

Two similarly named types are excluded from library parity evidence:
CardSelectorAction is constructed with TurnStepFlags.ChooseCardName, so its
SelectedCard/TranslateSelectedValue methods do not establish library search
selection behavior. Game's CreatureOrdering state element creates OrderingAction;
DuelSceneViewModel uses that action for ordering attackers/blockers. Neither trace
establishes a library top/bottom ordering counterpart. Keep that mapping open.

Evidence identities (SHA-256 of retained decompiled files, associated binary
version/provenance recorded below): CardSelectorManager.cs
`c9f37aff81ec00eae5fae4ed156d5ba948e05dff9b3f6ca42c29a2843cb9f762`;
CardSelectorView.cs
`c3be415b9dc0b495db91d325a2cd14b973b222a6ebda7ee8ad8585e87431bd25`.
Decompilation includes unresolved enum-reference warnings; numeric enum values
alone have not been promoted into library-specific behavioral claims.

This register supersedes the implementation-status statements in the chronological
notes below. Those notes retain the evidence and failures that led to the changes.
This is a partial method inventory, not a completed MTGO parity certification.
Counterparts match responsibilities; class names and method counts need not match.
An intentional difference is not accepted as better merely because it is documented.

| Recovered MTGO method | Current counterpart | Current verdict / remaining proof |
| --- | --- | --- |
| CardSelectorViewModel.CardSelect_Execute | ChoiceSelectionService.select, single-card search branch | Selection is retained without sending. Selecting another option replaces it. Repeated selection now retains the card, matching the recovered method; explicit Start over clears ours. Multi-card selection is outside this single-selection method's evidence. |
| CardSelectorViewModel.CardSelectOk_Execute | ChoiceControls confirmation → SignalRService.choose | Explicit confirmation sends selected object IDs. MTGO resolves a selected definition to a zone object and executes its first offered action. Responsibility mapped; object-identity/protocol differences are not an exact method-body match. |
| CardSelectorViewModel.CardSelectOk_CanExecute | ChoiceSelectionService.canSubmit | Our search bounds permit zero selections when fail-to-find is allowed. MTGO's recovered OK method requires a selection; its separate fail-to-find action path remains unmapped. Do not change our legality from this isolated UI method. |
| CardSelectorViewModel.CardSelectClose_CanExecute / CardSelectClose_Execute | LibraryRevealComponent details collapse and card-modal close | MTGO's selector close command is disabled and its execute method is empty. Ours permits collapsing the inspection and closing a detail without cancelling the pending operation. Presentation difference; exact close/input-binding parity remains unverified. |
| CardSelectorManager.GetShouldZoneDisplay | LibraryRevealComponent checks inspectedLibraryCards.length | Both use available objects, not public library total. Our server filters inspection objects by viewer and current zone. MTGO's pile-only branch has no established counterpart in this audit. |
| CardSelectorManager.CreateVisual / DestroyVisual / UpdateDelayedSelectors | LibraryRevealComponent derives active sections and clears unauthorized active detail on state updates | Lifecycle counterpart implemented for search/scry/surveil; historical LastLook is separate. Temporary generic look/reveal and pile windows are not covered by this verdict. |
| ZoneViewModel.Count; PlayerViewModel DeckTotal assignment | InspectedLibraryCards count; PlayerView.LibraryCount | Authorized content count and public total remain distinct. Partial responsibility match; not proof of identical MTGO server disclosure timing. |
| Game.DetermineSpecialStepTypeFromTurnStep / DetermineSpecialStepTypeFromInteractElement | OpeningHandPrompts.Declaration → explicit choice kind → client choice rendering | Typed protocol replaces text/flag recognition. Architectural difference with an identifiable purpose, not a proven superiority claim. |
| Game.ProcessTurnStepElement mulligan action creation | OpeningHandPrompts.Declaration | Keep/Mulligan choices supplied, no assigned automatic default. Keyboard/default-button parity still unverified. |
| PromptBoxViewModel.Execute / FinalExecuteAction | ChoiceControls → board → SignalRService.choose | Authoritative submission chain mapped. Client coalesces concurrent sends by game/choice. Ambiguous acceptance followed by transport failure remains an open acceptance case. |
| PromptBoxViewModel.DeactivatePrompt | SignalRService.choiceSubmission + ChoiceControls.sendingLibraryAction | Bounded standard-button behavior demonstrated: clicked control disabled, alternatives hidden for search/opening actions. Four-seat confirm, skip and mulligan captures verify it. Missing metadata retains disabled fallback; complete descriptor parity remains unverified. |
| Game.ExecuteAction / CardAction.GetMessage | Shared command transport and server ChoiceSystem validation | Responsibility mapped, not wire-protocol parity. Full contextual library action inventory remains open. |

### Evidence checked for this register

Re-read retained CardSelectorViewModel methods at lines 198–252,
CardSelectorManager methods at lines 105–160 and PromptBoxViewModel.DeactivatePrompt
at line 1607; artifact/version provenance is recorded below. Re-read current
ChoiceSelectionService, LibraryRevealComponent, SignalRService pending state and
OpeningHandPrompts. This update adds no runtime observation or MTGO execution.
Existing live and test evidence is scoped in the chronological notes below.
Latest backend logs independently confirm 1,518 rules and 804 API tests passing
(`library-inspection-replacement-rules.log`, `library-inspection-replacement-api.log`).
Those suite totals do not prove the unmapped MTGO contracts above.

### Required next audit work

1. Trace the library selector's input bindings and separate cancel/fail-to-find
   actions before changing selection-toggle, close or zero-selection behavior.
2. Inventory contextual library actions, their availability and dispatch methods;
   map each to a responsible class and a behavioral test or an explicit gap.
3. Trace ordering and opening-hand keyboard/default actions. Do not infer server
   shuffle, draw, search or mulligan algorithms from client presentation methods.
4. For every intentional difference, either align behavior or supply a concrete
   benefit and verification. Until then its verdict stays different/unverified.

Our LibraryDrawCoordinator, LibrarySearchPlanner, LibraryOrder,
LibraryRevealCoordinator and OpeningHandCoordinator are independent engine
implementations. No corresponding MTGO server implementations have been recovered;
their correctness must be assessed against rules and scenario tests separately.

## Opening-hand prompt method audit

### Keyboard dispatch trace (2026-10-23)

Implementation follow-up: LibraryChoiceShortcuts now maps offered mulligan choices
to the recovered defaults (3 Mulligan, 4 Keep). ChoiceControls listens for the key
and emits its existing pick event; selection validation, command transport and
pending-send coalescing remain shared with button clicks. The buttons expose
aria-keyshortcuts and a shortcut title. Text-entry/dialog targets, composing,
repeated, modified, handled and pending-send events are ignored. Enter, Escape,
unoffered options and non-mulligan choices do not dispatch through this policy.
This matches the recovered default-choice responsibility; configurable remapping
and the generic OK/Cancel/search-selector routes remain incomplete.

Two live browser seats passed at table 492bd3a1-77e0-4c40-93a0-b83b792a77a4:
Enter did not decide, 3 requested a mulligan, 4 kept, one card was bottomed and
both seats completed opening hands with six/seven cards. Inspected before/kept
captures. Harness: client e2e/verify-library-mulligan-shortcuts-live.js; evidence:
library-mulligan-shortcuts-live.log. This two-seat casual fixture does not establish
Commander multiplayer free-mulligan acceptance. npm run verify passed: 1,808
tests, changed-file coverage (28 files at or above 60%) and production build,
recorded in library-mulligan-shortcuts-verify.log. Existing card-modal stylesheet
budget warning remains. No commit made.

Follow-up recovered the default bindings from the matching public
`WotC.MtGO.Client.Model.Settings.dll` artifact at the versioned patch base already
recorded in this document. Size 67,072 bytes; SHA-256
`7034a0f6ca5918fd9dc62bfa4cbe71a6ceb9fbb48b39e104be2346abc702407c`.
Its SHA-1 matches the retained manifest digest `nEu+7IUG4fijBEHfZMpaC9NyRck=`.
ILSpy reconstructed KeyBindingSetting and the retained DuelScene overlay:

| Semantic action | Overlay setting ID | KeyBindingSetting default | Library consequence |
| --- | --- | --- | --- |
| OK / Pass | 10 | Key.D1 | Candidate confirmation shortcut, only when an offered confirmation is available. |
| Yes | 15 | Key.D3 | Mulligan on the recovered TurnStep mulligan route. |
| No | 16 | Key.D4 | Keep on that same route. |

`HotKeyOverlayViewModel.LoadHotKeys` supplies the semantic labels and setting IDs;
`KeyBindingSetting.DefaultKeyGestures` supplies the physical defaults. Settings are
mutable, so these are defaults, not every user's binding. No Enter default was
established. Search-selector keyboard handling still requires its own input-route
trace; the generic OK/Pass shortcut does not prove it confirms that selector.
The client implementation gap remains open. A library-scoped dispatcher should
map offered Mulligan/Keep options to 3/4 through the existing pick path, ignore
text entry/modifier/repeat/pending states, and avoid creating a second submission
implementation. Configurable remapping is a separate parity requirement.

New reconstruction SHA-256 identities:
- KeyBindingSetting.cs: `0af4f2b20f31efdb8775c96f2d7f5977e9f54640692603124d6235ea0d2866d8`
- HotKeyOverlayViewModel.cs: `faa797ad8ff38737ef55ae3ef9b574219830769cc8135b1c3e2fd785c9ce7ee4`
- PrimitiveAction.cs: `b956fc6c24b048dbdf5b10f830b53f54d58a542ebfc3c531bb52fbfbbd2496db`
- PrimitiveWithAlternatesAction.cs: `5bcad8a9a5818324415fe617229a3a4f9997656d3894a677ee7de013bdd73a22`

PrimitiveWithAlternatesAction.Create forwards the default flag and action flags
through PrimitiveAction. It defaults isDefault to false and passes actionHotkey
zero. This supports the earlier mulligan non-default finding without guessing
from button position. No target execution or application behavior change occurred.

Static inspection of the retained reconstruction extends the earlier default-button
finding. No MTGO process was executed. PromptBoxViewModel.cs SHA-256 remains
`e8cc28a4fa67ec56567a20645db5acc66511ccf69d8196a70a8508157a00b50f`;
DuelSceneViewModel.cs SHA-256 is
`c49b06f38525aa9d0d9687941d59d5c6bbef1649bbd792169667243b11c208cb`.
These files retain unresolved enum/reference warnings; numeric hotkey identifiers
below are not claims about physical keyboard bindings.

| Recovered method | Observed contract | Our counterpart / verdict |
| --- | --- | --- |
| PromptBoxViewModel.ProcessUpdate (1054; button routing 1130) | Resets prompt button references, builds offered actions, then resolves OK by action Hotkey 1 or localized OK name; Cancel by Hotkey 2 or Cancel name; Done by name. | ChoiceControls renders offered ChoiceDto options. No corresponding semantic hotkey routing exists in the inspected choice component/service. Gap. |
| DuelSceneViewModel.OkPressed (2627) | Routes to offered OkPromptButton, otherwise DoneButton; no button means no submission. | Explicit confirmation exists, but no equivalent board-level hotkey dispatch has been established. Do not default to the first choice. |
| DuelSceneViewModel.ExecuteActionWithYield (2638) | Applies yield modifiers only to an action marked IsDefault; otherwise caller invokes the button. | No equivalent library prompt modifier contract. Outside basic mulligan selection; do not infer it from our immediate-choice branch. |
| DuelSceneViewModel.CancelPressed (2704) | Dispatches an offered Cancel action, or its button callback. No Cancel button means no action. | Closing card details is not a corresponding operation-cancellation method. Remains unmapped. |
| DuelSceneViewModel.YesNoPressed (2719) | Hotkey identifiers 5/6 locate offered Yes/No action flags, with action-hotkey fallback; identifier 20 adds a modifier to Yes. | Keep/Mulligan options exist and are selected explicitly. Semantic Yes/No keyboard routing is missing from the inspected choice path. |

Game.ProcessTurnStepElement maps mulligan Yes to Mulligan and No to Keep, while
leaving Mulligan non-default. Combined with YesNoPressed this establishes a
separate semantic choice route; it does not establish the physical keys or every
interaction-message route. Next evidence needed: GameHotKey definitions and
configured/default keyboard bindings, plus PrimitiveWithAlternatesAction hotkey
construction. Implement only offered-action dispatch once those are mapped,
including pending-send and editable-field guards. Browser native activation of a
focused button is not proof of parity with these global commands.

This is an additional concrete gap, not an accepted improvement over MTGO.
No application behavior changed in this audit and no verification suite was rerun.

### Contextual-action ownership audit

New static reconstruction of DuelSceneCardViewModel from the retained DuelScene.dll:
SHA-256 `4896f80ef1ddb11cca70c8c6032ff23f13bc99fe72c86578da3dab8a0edf5925`.
Local ILSpy, no target execution; file identity added to the reconstruction index.

| Recovered method | Observed contract | Library counterpart / verdict |
| --- | --- | --- |
| DuelSceneCardViewModel.UpdateSelectableState | Derives selectability from received actions, active state, targeting state and interaction mode; copies AlwaysShowActionMenu. | Search candidates come from the server and ChoiceSelectionService rejects unoffered options. Full contextual menu availability is not established by this narrower counterpart. |
| DuelSceneCardViewModel.ExecuteAction (2794) | Marks a Cast action as being cast and delegates to MainViewModel.ExecuteAction. | Library search submits through the shared choice transport. Do not add casting behavior to a library service solely to mirror this shared card class. |
| DuelSceneViewModel.ExecuteAction(PopUpMenuItem) (3348) | Unwraps the offered action and delegates to ExecuteAction(IGameAction), which calls PromptBox.Execute. | Shared dispatch responsibility mapped; a complete library-specific menu item inventory remains unverified. |
| DuelSceneCardViewModel.HaveAnyActionNamesNotOpenedZone (2475) | Records action names already used to open each zone and reports newly encountered names. | No corresponding action-name reopening policy identified in our active inspection component. This is a concrete unmapped presentation responsibility, not a search legality rule. |
| DuelSceneViewModel.HideAllPlayerTemporaryZones | Saves state and sets temporary zone visibility false. | Confirms presentation hiding exists elsewhere in MTGO, but does not prove the library selector Close command invokes it. Our collapse behavior remains a different, unverified route. |

No unconditional Draw/Shuffle library menu contract was established from these
methods. Our manual controls are an honour-system extension, not a recovered MTGO
equivalent. Their paths are ZoneViewer → LibraryActionsService → command transport
→ GameSession → ManualLibraryCoordinator → the existing zone draw/shuffle methods.
GameSession retains ownership of table-level honour-system authorization.

This audit found the game-state eligibility predicate duplicated in GameSession's
projection and ManualLibraryCoordinator's execution. Both now call
ManualLibraryPolicy.IsAvailable; request identity/replay validation stays in the
coordinator. This removes an actual source of display/command drift without
creating a second draw or shuffle implementation. It is an improvement over our
previous structure, not evidence of superiority to MTGO. Full rules (1,518) and
API (804) suites pass in library-manual-policy-*.log. Existing API scenarios cover
allowed draw/shuffle, enforced-table rejection, replay rejection and empty-library
loss/control withdrawal. No client code changed in this refactor; npm verification
and live captures were not rerun for it. No commit made.

### Follow-up: recovered mouse route and repeated selection

Static ILSpy reconstruction of `Shiny.Play.Duel.CardSelectorView` from the same
retained DuelScene.dll establishes the mouse route: generated Connect case 4
attaches PreviewMouseLeftButtonDown/Up; MouseLeftButtonUp resolves a card from
DetailsViewModel or BaseCardViewModel and calls SelectCard; SelectCard executes
CardSelectCommand. It does not confirm. Combined with CardSelect_Execute, this
establishes retention on repeated selection on this route. XAML command bindings,
keyboard and alternative routes remain unverified. Do not reproduce incidental
mouse-delta implementation details as a domain requirement.

Reconstruction hash: CardSelectorView.cs
`c3be415b9dc0b495db91d325a2cd14b973b222a6ebda7ee8ad8585e87431bd25`.
Also reconstructed CardSelectorDialogViewModel.cs
`3f54ee39ad356ffe9b8a40b183c53a91a8f6a55507a29b05e6d451a252fb45db`.
The latter takes ICardSelectorAction and returns a CTN through the prompt; it is
not evidence for library-object fail-to-find. Its hidden Cancel/Close buttons must
not be imported into library behavior based on the similar class name.
Both file identities are retained in reconstruction-file-hashes.json. No target
execution occurred; unresolved-reference limitations still apply.

Our single-card search now retains repeated selection; explicit reset still clears
it. Existing multi-card and unrelated immediate-choice branches are unchanged.
Live regression failed before the change at table
`1ccbc868-a977-4902-83bb-62aa0a2efb62`, with aria-pressed=false after reselecting
(`library-reselect-red.log`). Afterward, two-player table
`34b15516-bb6f-4d1a-96cc-5ef62e2ddfbb` passed repeated selection, no premature
submission, confirmation, private inspection and destination/count checks
(`library-reselect-live.log`). Inspected `library-search-reselected.png` confirms
the retained highlight. This casual-deck scenario is not Commander deck-legality
acceptance. The selection-service regression also checks explicit reset.
`npm run verify` passed: 1,805 tests, changed-file coverage and production build
(`library-reselect-verify.log`). Existing card-modal stylesheet budget warning
remains. No commit was made.

Inspected the saved Game reconstruction and newly reconstructed
`Shiny.Play.Duel.ViewModel.PromptBoxViewModel` from the retained DuelScene.dll
(version 3.4.159.4708; assembly hash recorded below). Local ILSpy 11.1.0.9782,
static inspection only; no target execution. Reconstruction SHA-256:
`e8cc28a4fa67ec56567a20645db5acc66511ccf69d8196a70a8508157a00b50f`.
Unresolved-reference comments in the reconstruction remain a limitation.

| Recovered method | Observed contract | Our counterpart / verdict |
| --- | --- | --- |
| Game.DetermineSpecialStepTypeFromTurnStep (2740) | Recognizes mulligan special step from Yes/No flags and prompt text. | OpeningHandPrompts.Declaration supplies explicit `mulligan` kind. No string heuristic needed for our protocol; this is a different representation, not proof of superior behavior. |
| Game.DetermineSpecialStepTypeFromInteractElement (2749) | Recognizes received Mulligan/Keep buttons and mulligan prompt text. | engineView consumes server kind and options; ChoiceControls renders supplied labels. Same separation of server options and client presentation, different protocol. |
| Game.ProcessTurnStepElement (2589; Yes/No branches 2617–2636) | Creates Mulligan/Keep Hand actions, neither marked default in this path. | Declaration offers Keep/Mulligan; ChoiceSelectionService.select returns the clicked single option. No automatic default is assigned. Keyboard binding parity has not been established. |
| PromptBoxViewModel.ProcessUpdateSnapshot (55) | Captures prompt text/options/player; supports all-player prompts. | engineView projects one pending player and authorized options. Our opening round is sequential; this reconstruction does not prove MTGO uses all-player prompts for mulligans. |
| PromptBoxViewModel.Execute (561), FinalExecuteAction (818) | Standard action deactivates prompt, then delegates execution through Game.ExecuteAction. | ChoiceControls → board choice action → SignalRService.choose → authoritative command. Submission is coalesced while pending, but UI deactivation is missing. |
| PromptBoxViewModel.DeactivatePrompt (1607) | If action belongs to standard buttons, disables clicked button, hides alternatives, marks prompt inactive. | ChoiceControls buttons stay enabled during delivery. Confirm checks only selection validity. Concrete UI gap; no evidence justifies treating this as better. |

Next implementation: expose choice submission state to library/opening prompts,
prevent changing/submitting alternatives while the request is in flight, and
restore interaction after failure without losing retained search selection.
Verify delayed send, duplicate clicks, refusal/failure and new authoritative choice.
Do not infer MTGO server redraw, free-mulligan, bottom-order or shuffle algorithms
from these client prompt methods. Those remain independently implemented rules.

Pending-control implementation: SignalRService now exposes reactive submission
state keyed to game and choice. ChoiceControls receives that state from the board
and disables option, ordering, skip, reset and confirm controls during delivery;
Confirm reads "Sending...". Details remain available. Completion/failure clears
only the matching request's pending state, allowing retry with retained search
picks. Tests cover disabled interaction, selection preservation, duplicate-send
coalescing, failure reset and choice-ID scoping. Our alternatives remain visible
but disabled, whereas the recovered MTGO method hides them; this is a documented
presentation difference, not an exact visual-parity or superiority claim.
Verification: `library-pending-prompt-verify.log`, npm run verify passed with
1,805 tests, coverage and production build. Live offline/retry table
`1d0fba07-ee7e-4a9f-b436-cd65290d73c0` passed; inspected
`library-search-offline-retained.png` confirms the selected card survives failure.
An edit-time encoding regression was corrected and recaptured before this pass.
Pending-state interaction itself is covered by controlled component/service tests;
the live capture shows the restored retry state, not a delayed-send interval.

## Active inspection implementation checkpoint

Library search now records scoped `LibraryInspectionChanged` events independently
of historical `LastLook` snapshots. `ViewProjector.InspectedLibraryCards` includes
only the authorized viewer's inspected objects that still occupy the inspected
library. Search completion removes its scope, including after serialized choice
resumption. `PlayerLost.Apply` removes scopes belonging to the departing viewer
or library owner; `GameEnded.Apply` clears all active scopes. Unrelated scopes
survive a departure.

This provides engine state for the recovered delayed-selector lifecycle described
below. Client consumption and live window verification remain unfinished; this is
not yet a full counterpart to `UpdateDelayedSelectors` / `DestroyVisual`, nor proof
of MTGO server timing. Temporary non-search look/reveal scopes remain open work.

Verification: `library-active-inspection-rules.log` (1,509) and
`library-active-inspection-api.log` (802) passed before departure cleanup.
`library-inspection-lifecycle-rules.log` passes all 1,512 rules tests after cleanup,
including viewer departure, library-owner departure, unrelated-scope preservation,
and game end. The attempted initial regression run stalled after restore and was
cancelled; it is not failing-test evidence.
API compatibility after cleanup: `library-inspection-lifecycle-api.log`, 802 passed.

Transport follow-up: `GameArtworkService.ForAsync` now includes authorized active
inspection objects in its existing visible-card lookup. Its regression verifies
printing selection, deduplication, face-down exclusion, and no retained lookup
after the projected inspection clears. All 803 API tests pass in
`library-inspection-art-api.log`. Client `engineView` and `GameViewAdapter` carry
`inspectedLibraryCards` separately from historical disclosure and default absent
inspection data to an empty list. Rendering the active window remains unfinished.
Client gate: `e2e/screenshots/library-inspection-transport-verify.log` passes
`npm run verify` (1,803 tests, changed-file coverage, production build). Existing
card-modal stylesheet budget warning remains. No visual parity claim is made for
this transport-only change.

Active-window follow-up: `LibraryRevealComponent` now derives an "Inspecting
library" section from the authorized active objects. It suppresses duplicate
historical-look content while active and closes an active card-detail selection
when that object is no longer authorized. Historical snapshots remain explicitly
labeled after access ends. This implements creation/removal from received state,
corresponding to the observed delayed-selector responsibility; it does not claim
identical MTGO layout or server timing.

Two-player live table `f90cfd9d-b295-4ced-ba44-e423acfca8aa` passed search privacy,
reconnect restoration, selection/confirmation, destination, counts, and active
inspection removal. Evidence: client `library-inspection-ui-live.log` and
`library-search-inspection*.png` in e2e/screenshots. Desktop and phone images were
inspected. Portrait shows the existing rotate-phone screen; the landscape rail
is cramped and needs further layout work. Document overflow checks passed, but
those checks do not establish good phone usability.
`library-inspection-ui-verify.log`: npm run verify passed, 1,804 tests plus
coverage and production build; existing card-modal stylesheet warning remains.

Landscape follow-up: active inspections expand into a scrollable panel on short
viewports using the shared `short-viewport` mixin. Desktop retains the sidebar
layout. Live table `b9cf2fc6-c98f-4d22-9209-56c4b7e95f64` passed the preceding
search checks plus panel width greater than 90% of the viewport and collapse /
reopen at 667 x 375. Inspected desktop and landscape captures confirm the formerly
cramped list is readable. Evidence: `library-inspection-layout-live.log` and
`library-search-inspection-landscape.png`. Portrait still uses the existing
rotation screen. This is our responsive adaptation, not an observed MTGO layout.
`library-inspection-layout-verify.log`: npm run verify passed, including 1,804
tests, changed-file coverage and production build (existing stylesheet warning).

Multiplayer inspection audit: expanded `MultiplayerLibrarySearchTests` verifies
every participant's live inspection contents independently of whose choice is
pending, serialized APNAP decisions, moved-card exclusion during replacement
prompts, and complete scope removal while historical looks remain. Full rules
pass: 1,512 (`library-multiplayer-inspection-rules.log`). The first four-browser
inspection run timed out at table `2d69b7d6-1a2a-4f10-b047-ac44e2af2f57` after
the first seat's capture; `library-four-inspection-live.log` is failure evidence,
not a pass. A diagnostic run is required before live multiplayer acceptance.

Diagnostic table `5eaa8b22-4107-42e6-8a2a-f914a9fcccdb` reproduced partial
delivery: after seat 1 confirmed choice 6, viewers 0 and 1 had choice 7 for seat
2, while viewers 2 and 3 still had choice 6 when the 15-second wait expired.
No refusals were observed. See `library-four-inspection-diagnostic.log`.
`GameHub.Command` pushes projected views sequentially, and active inspections
add many objects to projection. Projection/artwork/delivery latency needs
measurement; the bottleneck is not yet established. Do not increase the timeout
and call this accepted without investigating the update path.

Measured cause: temporary per-stage instrumentation caught a 14,567 ms
`ViewUpdated` send while projection was 4 ms and artwork was 0 ms
(`library-push-timing-runtime.log`, table
`62135a1c-b54b-40d8-9c75-22a348b51cd6`). Sequential recipient delivery made that
send delay later seats. Instrumentation has been removed. The hub now prepares
private payloads sequentially (preserving scoped artwork lookup safety), then
delivers independently per account, retaining view-before-choice ordering for
each recipient and awaiting all deliveries. It does not broadcast private data.
All 804 API tests pass (`library-independent-delivery-api.log`), including a
controlled blocked-recipient regression. Live four-player revalidation remains
required; no timeout was raised to mask the failure.

Live revalidation passed at `d0d49690-7619-447b-956e-7be3a0860639`:
`library-independent-delivery-live.log` records all four APNAP choices (5–8),
refresh restoration, private inspection ownership/counts, correct destinations,
and active-window expiry in every browser. The original 15-second wait remains.
This is a measured improvement over our sequential delivery, not evidence of
superiority over MTGO's unrecovered server.

Scope: library selection only in this first audited slice. This is a comparison
against saved static reconstruction of MTGO client 3.4.159.4708, not a live MTGO
comparison or a reconstruction of its server. Baseline evidence:
`ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265`,
with provenance and limitations in `ACTION_STATE_AND_CARD_FINDINGS.md`.

## Selection contracts inspected

| MTGO reconstruction | Our method | Finding |
| --- | --- | --- |
| TargetSet.AddTarget, TargetSet.cs:206, legal-membership assertion | ChoiceSelectionService.select, choice-selection.service.ts:26, offered-ID check | Same membership invariant for independent picks. MTGO asserts on invalid input; ours ignores it in the UI. Our server ChoiceSystem.Validate independently refuses it. Failure presentation differs. |
| TargetSet.AddTarget, current count below maximum | ChoiceSelectionService.select, maximum guard; ChoiceSystem.Validate | Same maximum-count invariant for ordinary multiple selections. This does not establish equivalence for distributed values or related targets. |
| TargetSet.RemoveTarget, TargetSet.cs:272 | ChoiceSelectionService.select, existing-pick branch | Both remove an existing selection; ours toggles from the same method, MTGO uses a separate method. No benefit is claimed from the different method count. |
| TargetSet.CurrentTargets_CollectionChanged, TargetSet.cs:305, IsSet assignment at 327 | ChoiceSelectionService.canSubmit | Both use the minimum to determine readiness; ours also checks maximum. MTGO already enforces maximum during insertion. |
| TargetSet constructor, TargetSet.cs:190 | ChoiceSelectionService.canSubmit and server-produced bounds | MTGO clamps minimum to maximum. Our selection service consumes supplied bounds without clamping. Equivalence requires valid upstream bounds; malformed-bound behavior is different and has not been justified as better. |
| TargetSet.AddTarget records a selection | ChoiceSelectionService.select returns an immediate answer for maxPicks=1 | Potential UX difference. MTGO's surrounding action controller must be traced before concluding whether it confirms or auto-submits single-card library choices. Open. |
| TargetSet.MatchesRequirements, TargetSet.cs:422 | No general relational filtering in ChoiceSelectionService | Not equivalent for all target relationships. Ordinary library search candidates are computed by LibrarySearchPlanner; targeted spells and all relational-selection features are outside this library audit. Do not import targeting rules into search merely to match a class. |

Source paths for our client methods are under
`../mtg-client/src/app/game/choices/`. Server validation is
`MtgEngine.Rules/Choices/ChoiceSystem.cs:53`.

## Verification and limits

The existing ChoiceSelectionService spec uses a search choice and exercises
unknown IDs, maximum count, pick ordering, removal, readiness, changed choice
identity and immediate single-card answers. It is suitable evidence for those
contracts, not for MTGO's entire search interaction.

Fresh ChromeHeadless run: all five selection-service tests passed. Evidence:
`../mtg-client/e2e/screenshots/library-method-selection-audit.log`. Full npm
verification was not rerun for this audit; no client production code changed.

Current server run `library-method-audit-tests.log`: 69 ChoiceResumptionTests
passed. These include stale/wrong-player/duplicate and count rejection, but many
use other choice kinds. They verify the shared validator, not all library paths.

The same run reproduced one library search failure after correcting the unfinished
test's invalid selector type from All to the existing Each. Selecting two cards
with a conditional destination resulted in one card in hand instead of two:
the first move affected the second destination calculation. This is a failing
independent-engine regression, not evidence of an MTGO defect or superiority.
The subsequent fix plans all participants' selected destinations before movement
using LibrarySearchPlanner.Moves, persists those plans across replacement prompts,
and removes them when the instruction completes. The regression now passes.
Four-player replacement replay also verifies the saved plans are withheld from all
client views. Full rules: 1,499 passed; API: 802 passed (library-search-destinations-
rules.log and library-search-destinations-api.log). This establishes an improvement
over our previous implementation, not MTGO's unavailable server implementation.

No full parity verdict or superiority claim is warranted. Next comparison work:
trace the MTGO action controller around single selection; audit bounds production;
then map library contextual actions and mulligan prompts. Server draw/search/
shuffle algorithms remain unknown in the recovered client evidence.

## Follow-up: prompt selection and bound production

Inspected Game.HandleCardSelectorElement (Game.cs:2305) and reconstructed
WotC.MtGO.Client.Model.Play.Actions.CardSelectorAction using the existing local
ILSpy 11.1.0.9782. Assembly SHA-256 remains
7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081.
New reconstruction `decompiled/CardSelectorAction.cs` SHA-256:
736b012b89d51c2d0d14535a41c759b0ec9d8c36831d8ee477cfd9e33b5dc66c.
This is additional ILSpy static evidence, not a new REA evidence record or runtime
observation. The reconstructed class was not executed.

Observed method chain:

- Game.HandleCardSelectorElement passes received Choices and timestamp to
  CardSelectorAction.Create and adds the action to the prompt.
- CardSelectorAction.SelectedCard only updates a notified integer property.
- CardSelectorAction.GetMessage rejects zero, calls TranslateSelectedValue and
  returns the message. TranslateSelectedValue places the selected integer in
  the message parameter; SetMessage constructs PromptActionMessage.
- The constructor uses TurnStepFlags.ChooseCardName. This path therefore must
  not be labeled a demonstrated library-object selection path merely because
  its class is named CardSelectorAction.
- Separately, CardAction.GetMessage validates fulfilled target sets and serializes
  selected object IDs. CardAction.Execute sends first and clears target selections
  afterward. These methods do not establish which UI event calls Execute.

Conclusion: neither the SelectedCard setter nor TargetSet.AddTarget proves
auto-submission or explicit confirmation for library search. The click/controller
caller and an identified library-search interaction are still required. Do not
change our confirmation UX based on this incomplete association.

Our bound-production gap is narrower than initially recorded. Inspection of
LibrarySearchPolicy.Bounds shows maximum=max(0, requested); an UpTo search has
minimum=0; otherwise minimum=min(mandatory match count, maximum). Plan supplies
nonnegative actual candidate counts. Therefore 0 <= minimum <= maximum for
searches through LibrarySearchPlanner.Plan. The client need not clamp valid
search bounds to satisfy this invariant. This is static proof for that producer,
not proof about every choice kind or equivalence to MTGO's server.

## Duel controller and library selector: confirmed method-level gap

Retrieved PlayScene.dll and DuelScene.dll from the same version directory declared
by the saved application manifest (3.4.159.4708.20261014120111), using the official
mtgo.patch.daybreakgames.com artifact host. PlayScene's class inventory is mainly
lobby code; DuelScene contains the game-screen controller and library selectors.
SHA-256:

- PlayScene.dll: 562e1cc3880babab06c9ba2a798dee4a42375bd31f13f9680d3ff629aa4e84ba
- DuelScene.dll: 3799b8fe160304405416153b8c80e96aa8196ef961cc510abe45aa4b0fd208d1

Local ILSpy 11.1.0.9782 reconstructed the focused classes; inventories are saved
in playscene-types.txt and duelscene-types.txt. New reconstruction hashes are in
reconstruction-file-hashes.json. No application execution or live MTGO observation
occurred. Unresolved dependency warnings remain a limitation of recovered source.

Observed chains in the reconstructed DuelSceneViewModel:

1. LeftClickDuringSelectTargets (line 2851) calls SelectOrDeselectTarget, checks
   own-card confirmation flags, then evaluates every target set. If fulfilled
   under its variable-length/legal-target rules and not distributed, it calls
   ExecuteAction. Thus some target selections do auto-submit; that fact does
   not describe every library-selector interaction.
2. Library-zone registration (lines 5793 and 5798) calls CardSelectors.AddSelector
   for the local and other players' LibraryZone. CardSelectorManager.CreateVisual
   (line 140) constructs CardSelectorViewModel for that zone.
3. CardSelectorViewModel.CardSelect_Execute (line 198) checks that the underlying
   card has actions, clears the previous selection, and records the new selection.
   It does not send an action.
4. CardSelectOk_CanExecute (line 238) requires a selection. CardSelectOk_Execute
   (line 224) resolves the selected definition to a zone card, executes its first
   offered action, and clears the selection.

Comparison verdict: our ChoiceSelectionService.select immediately returns an
answer for maxPicks=1, and our choice controls omit confirmation in that case.
This differs from the recovered library selector's select-then-OK command path.
There is no demonstrated benefit justifying our difference. Implement explicit
confirmation for library Search choices, retaining existing behavior for unrelated
choice kinds. Verify selecting alone sends nothing, changing selection is possible,
confirm sends exactly once, fail-to-find still works, and reconnect clears stale
local selections. Update live library harnesses that currently rely on auto-submit.

This establishes the library selector's command contract. Actual WPF input bindings
and any alternative/double-click route have not yet been inspected; do not claim
every possible MTGO mouse gesture requires two clicks.

Implementation follow-up: ChoiceSelectionService now retains single-card search
selections instead of returning an immediate answer. A different card replaces the
selection; selecting it again retains it, as in the recovered selector. ChoiceControls displays Confirm and Start
over for search choices even when maximum=1. Other single-choice kinds retain
their prior immediate submission. A focused regression covers selection,
replacement, deselection and readiness. This matches the recovered select/OK
responsibility split; it is not a claim of complete interaction parity.

Live two-player selection/confirmation passed at
d8a10ed4-96e4-411c-ab96-fe05055ee467; inspected library-search-selected.png.
Fail-to-find with matching options passed at
894308ff-0d00-4593-a2a9-02309c63de5c. Logs are client
e2e/screenshots/library-search-confirm-live.log and library-search-confirm-none.log.
Duplicate-click/transport-failure behavior still needs a focused acceptance check;
the shared server already rejects stale choice IDs but this does not prove UI
single-submission behavior.

## Active library windows versus historical disclosure

Further static reconstruction of DuelScene.dll (same retained SHA-256 above),
using local ILSpy 11.1.0.9782, recovered ZoneViewModel and PlayerViewModel.
Their reconstruction hashes are recorded in reconstruction-file-hashes.json.
No application was executed for this inspection.

| MTGO method/property | Observed responsibility | Our comparison |
| --- | --- | --- |
| CardSelectorManager.UpdateDelayedSelectors | Creates/destroys delayed selectors when GetShouldZoneDisplay changes. Library registrations use this delayed path. | Current search choice controls have a pending-choice lifetime; LastLook is a different historical display. |
| CardSelectorManager.GetShouldZoneDisplay | Normally tests ZoneVM.Count > 0; the pile-only branch also checks PilePanelChoosePile. | Do not use total LibraryCount as permission to open contents. |
| ZoneViewModel.Count | Count of m_cards, initialized from ModelZone and updated by collection events. | Authorized objects and total zone count must remain separate. |
| PlayerViewModel update at line 1833 | DeckTotal = GamePlayer.LibraryCount. | PlayerView.LibraryCount is the corresponding public total. |
| DuelSceneViewModel.Game_GameChanged | UpdateViews, then UpdateDelayedSelectors, then clears WaitingForServer. | Update/close active selectors from received authoritative state rather than retaining live access from a previous look. |
| CardSelectorManager.DestroyVisual | Removes the selector and disposes its view model. | ChoiceControls destruction clears local selection; this is not expiry of engine-side disclosure permission. |
| PlayerViewModel temporary-zone construction at line 1560 | A separate Revealed zone with canDismissCards=true. | LastReveal is a snapshot display; complete parity with MTGO's revealed zone is not established. |

The recovered client collection logic does not establish precisely when the MTGO
server removes a revealed/search object. That rule timing remains unknown from
this evidence. Library selector count is not the deck total; treating them as one
would create an incorrect visibility model.

Our RevealedCard explicitly contains no ObjectId or current-zone link. Keeping a
LastLook snapshot therefore does not by itself grant continued access to current
library order. This is a verifiable privacy distinction, not proof that our history
feature is better than MTGO or that active disclosure lifetime is implemented.
Next required engine work is scoped active disclosure state and expiry, separate
from LastLook/LastReveal snapshots, with replay and authorized-viewer tests.

Validation follow-up: library-search-menu-verify.log passed 1,819 tests, coverage gates and production build (existing card-modal style-budget warning). Removing searchPick.emit deliberately failed the new delegation assertion; restoring it passed all seven focused tests (library-search-menu-mutation.log and library-search-menu-restored.log). No commit made.

Multi-card coverage verification: eight focused component tests passed in library-search-multi-menu-restored.log. Removing the shared maximum-pick guard caused the new menu integration test to fail with three picks instead of two (library-search-multi-menu-mutation.log); the original guard was restored. The full verification result above predates this test-only addition.

## Current search and arrangement method comparison (2026-10-23)

Rechecked the retained source and current implementation directly. This is a static
comparison, not a fresh MTGO runtime capture or a reconstruction of its server.
Retained SHA-256: CardSelectorViewModel.cs
`b3531b632b001bc4edea3c834e896aa522836c0fed917ed4e5e1be89574fbbe6`;
ZoneViewModel.cs
`5ca30fec182ff525b7c080e7be0a810e26f1ec4a95becce1e0af36d70135da8b`.

| Recovered method | Current implementation | Verdict and remaining evidence |
| --- | --- | --- |
| CardSelectorViewModel.CardSelect_Execute (198) replaces selection without dispatch; clicking the same card leaves it selected. | ChoiceSelectionService.select retains a single search option, replaces it on another selection, and returns null. | Same selection/dispatch split and repeated-click behavior for single-card search. Multiple-card search is an additional contract, not proven equivalent by this single-selection method. |
| CardSelectOk_Execute (224) resolves a selected definition to a current zone card, checks offered actions, dispatches the first action, and clears selection. | Search confirmation submits option IDs for the pending server choice. | Comparable confirmation responsibility; different identity and action protocols. Definition lookup is not a reason to replace our physical object IDs. Complete dispatch equivalence remains unproven. |
| CardSelectOk_CanExecute (238) checks that selection is nonempty. | ChoiceSelectionService.canSubmit checks the choice's minimum and maximum. | Explicit difference: zero selections may be valid for an up-to search. The retained method does not establish MTGO's fail-to-find route; no superiority claim. |
| ZoneViewModel.Move (432) calls m_cards.Move and rejects timestamp-sorted zones. | ChoiceSelectionService.move edits a local draft; ManualLibraryCoordinator.Arrange validates the full inspected library and emits ZoneReordered only on submission. | Display movement and authoritative library mutation are separate responsibilities. This recovered method proves neither an MTGO manual arrangement command nor equivalent server validation. |
| ZoneViewModel.ViewUpdate (241) refreshes selectable state and formats server-provided attribute-match values/descriptions. | Search options and draft authorization derive from projected state; draft reconcile cancels when permission, owner, or inspected membership/order changes. | Related state-refresh responsibility only. The producer trace below scopes attribute-match values/descriptions to graveyard; they are not a demonstrated library gap. Ordinary card/selection counts are still not equivalent. |

The manual search entry point delegates to the shared interpreter Search instruction;
manual arrangement delegates to LibraryOrder.Arrange after inspection and exact-set
validation. These are verified local ownership boundaries, not recovered MTGO server
methods. The next parity work must trace the attribute-match producer and selector
bindings before proposing another UI feature or claiming one-for-one completeness.
No production behavior changed for this comparison; existing test results are not
relabelled as new validation.

### Attribute-counter producer trace: library applicability corrected

The retained `Game.ProcessPlayerStatusElement` (Game.cs:2064 onward) sets
`ShowAttributeMatchCounterInTheseZones` to the single value `(CardZone)3` when
both CLIENT_DISPLAY_COUNT_VALUE0 and CLIENT_DISPLAY_COUNT_STRING0 exist, and
otherwise sets an empty zone list. It reads successive paired values/descriptions
from player properties. `PlayerViewModel.ConfigureZones` identifies zone 2 as
library (1543) and zone 3 as graveyard (1544). `ZoneViewModel.ViewUpdate` gates the
counter on membership in that zone list (316).

Thus this recovered producer does not enable attribute counters for the library.
The earlier generic ViewUpdate comparison was too broad when treated as a library
implementation gap. No library counter UI should be added on that evidence.
Repository-wide retained-source search found this assignment path only; absence
of additional retained paths does not prove no other implementation can exist.
Game.cs SHA-256:
`17c0bb8cefc655603587f6b6c2e871c6b6ddacef5783a6456d75808cd642a034`.

The retained CardSelectorView.xaml still binds `CardSelectOKCommand` and `LayoutVM`,
while the reconstructed view model exposes `CardSelectOkCommand` and lacks LayoutVM.
Its zoom/sort/layout controls and mouse handlers are resource evidence, not proof
of working bindings against this recovered class. That discrepancy must be resolved
with matching code-behind/artifact or runtime evidence before claiming full selector
parity. This does not invalidate the directly observed select/confirm method bodies.

This correction changes implementation prioritization: keep attribute counters out
of the library backlog on current evidence; continue library ownership and multiplayer
acceptance work while retaining the selector binding uncertainty explicitly.

### Selector code-behind follow-up (2026-10-24)

Inspected the retained `CardSelectorView.cs` in full alongside the retained XAML
and view model. This is a retained-source audit, not new REA output or live MTGO
evidence. Its `InitializeComponent` loads `/DuelScene;component/view/cardselectorview.xaml`.
`IComponentConnector.Connect` assigns the zoom, sort, layout and OK controls and
attaches the layout mouse-down/up handlers. It does not assign an OK command,
replace the DataContext, or provide a LayoutVM adapter. Consequently this
code-behind does **not** resolve the binding discrepancy recorded above.

The executable selection route visible here is narrower: `SelectCard` casts the
DataContext to CardSelectorViewModel and executes CardSelectCommand. Mouse-up
accepts DetailsViewModel or BaseCardViewModel data contexts after testing signed
X and Y deltas against 10. It does not use absolute displacement, so this method
alone does not demonstrate symmetric drag suppression. Do not copy that check
into our click handling or describe it as superior pointer behavior.

The earlier Overlays trace remains the stronger library presentation evidence:
it creates a FloatingPanel with the library template, rather than instantiating
this selector view. The remaining action is to compare that template's shared
card interaction route with our library controls. Matching artifact/runtime
evidence would still be required to establish where this separate selector view
is used and whether its retained bindings work. No production change follows
from this discrepancy, and no new tests or runtime validation are claimed.

Retained SHA-256:

- CardSelectorView.cs: `c3be415b9dc0b495db91d325a2cd14b973b222a6ebda7ee8ad8585e87431bd25`
- CardSelectorView.xaml: `3eb0318074f2d3e560fd3bbc9d63b46c559bf75b642482832890cd73c2c252d9`
- CardSelectorViewModel.cs: `b3531b632b001bc4edea3c834e896aa522836c0fed917ed4e5e1be89574fbbe6`
