# Library UI acceptance matrix

## Multi-owner automatic inspection — 2026-10-24

The auto-open client build (1,887-test verification) now also passes a four-owner
public disclosure fixture: each of four viewers receives eight cards separated
into four owner panels, each initially open with two cards. The live verifier
reaches and collapses each frontmost panel at desktop and landscape, exposing
the next without answering the pending choice. Four shuffles remove all active
inspection, leave historical public reveal data, and survive all-seat reload.
Table 00fa65f7-513c-447e-9770-0db8f7b6bc13; library-all-disclosure-live.log, exit 0.
tools/TableQa/library-all-disclosure-owner-{0,3}-{desktop,landscape}.png supplies
the captured boundary; owner 0 landscape and owner 3 desktop were inspected.
This proves reachability by collapsing panels, not an MTGO-equivalent multiwindow
layout or physical-device usability. No production edit; host stopped; no commit.

## Current inventory reconciliation — 2026-10-24

Post-inventory integrated checkpoint: 2,085 rules and 840 API tests pass in
library-ui-combined-rules.log and library-ui-combined-api.log. Client
npm run verify exits 0 in mtg-client/library-ui-combined-verify.log: 1,870 tests,
changed-file coverage, formatting, lint, UI checks and production build pass.
Existing card-modal style budget warning remains. No commit was made; unresolved
rules and MTGO comparison questions remain outside this UI completion claim.

This table supersedes stale pending claims in the historical inventory below.
It maps that finite inventory to its later acceptance entries; it does not add
new required scenarios. A partial row identifies the remaining evidence precisely.

| Inventory row | Current disposition |
| --- | --- |
| Own/opponent library and public top | Private look/public-top and phasing flows are evidenced; opponent-control absence is now checked and visually inspected in all three viewports below. |
| Empty/single-card library | Covered by the six inspected inspection captures and last-card/empty-draw follow-up; the old missing-capture claim is superseded. |
| Look/stop | Live private access, both uncertain-delivery directions and all-seat reload are recorded. Active desktop/landscape and stop-control captures in all three modal viewports are indexed below. |
| Search form | Invalid empty/negative/fractional counts and all five destinations' conditional-control visibility are covered across three viewports; see the destination-control run below. |
| Search selection/confirmation | Private selection, no-match, intentional zero-pick and maximum-pick confirmation are covered by the runs below; portrait board choices display the rotation notice. |
| Draw/shuffle replacement prompt | Fresh four-seat runs and inspected desktop/landscape prompts plus portrait rotation notices are indexed below. |
| Arrangement editing | Local edit/cancel, exact-order confirmation, touch and empty/single-card boundaries are recorded. |
| Arrangement revocation | Revocation and rejected submission are covered; final rejection run c1b5ecad-5304-482f-b200-bbb2bea82918 includes inspected messages in all three viewports. |
| Reposition/mode guard | Existing top/bottom/touch flows plus the fresh mode-guard run below cover suppression during a cast draft, cancellation without mutation and subsequent reposition. All three guard captures are inspected. |
| Busy request | Visible sending status and one-command exclusion pass the later pending-feedback run; earlier missing-status finding is superseded. |
| Lost response/warning/retry | Fresh shuffle delivery-loss run verifies single warning ownership and retry; all three warning captures are inspected below. Server rejection remains a separate case. |
| Server rejection | Search and arrangement follow-ups record exact messages and inspected viewport captures; generic missing-error-state claims are superseded. |
| Card details | Keyboard focus/return and actual 404 artwork fallback pass separate four-seat runs with all three captures inspected. Phasing follow-up additionally closes open-preview revocation. |

Physical-device use remains unverified and is not represented as browser touch.

Mode-guard viewport acceptance (2026-10-24): table
d2ae0ad9-fc03-4257-9225-f46b454e86ec passes `--reposition --mode-guard --four`
(library-mode-guard-viewports.log). During a Cinder Spark draft, the inspected-card
menu contains no Put on actions. Capturing at all three sizes and cancelling the
draft preserve seat revisions. Subsequent top reposition, inspection expiry and
all-seat reload pass. Inspected tools/TableQa/library-reposition-during-cast-
{desktop,portrait,landscape}.png: desktop/landscape show only Card details and
Close; portrait shows the rotation notice. Verifier exited 0 and owned host stopped.
This closes the last partial viewport row in this finite inventory. It does not
close unresolved rules/MTGO items or replace final integrated acceptance.

Draw/shuffle prompt and uncertainty acceptance (2026-10-24): draw table
fc864542-b33e-456f-8251-ad508caacc23 passes `--draw --four`
(library-draw-prompt-viewports.log), including pending refresh, private drawn-card
projection, replacement answers and all-seat reload. Shuffle table
10806f3b-ca99-47f1-9bc4-ae4d924a7572 passes `--shuffle --lost-response --four`
(direct exec session 62232, exit 0). It verifies modal warning ownership without
duplicate generic toasts, replacement answers, retry identity/event-count checks
and all-seat reload. The first redirected launch exited 1 without diagnostic
output; it is not the successful evidence or an established product defect.
Both owned hosts were stopped after their runs.

Inspected all six `library-{draw,shuffle}-live-four-pending{,-portrait,-landscape}.png`
captures under tools/TableQa. Desktop/landscape show readable replacement labels;
portrait shows the board rotation notice. Also inspected
`library-lost-response-warning{,-portrait,-landscape}.png` from the shuffle run:
the single library-owned uncertainty message is readable in all three modal
layouts. These captures supersede the older warning-file attribution. This is
simulated dropped completion, not a physical-network or device test.

Look/stop viewport index (2026-10-24): table
6827e24d-a9cb-41fc-85f5-6e95849421db passes `--look --four`
(library-look-stop-controls-live.log). Active inspection survives reload and is
private to its actor; Stop looking expires it for every seat and final reload
preserves that state. Inspected captures from this run:
`library-look-live-four-active.png` (desktop inspection),
`library-look-live-four-landscape.png` (landscape inspection), and
`library-look-stop-control-{desktop,portrait,landscape}.png` (enabled stop control).
Files are under tools/TableQa. The modal remains usable in portrait, although the
underlying board's portrait rotation guard is not an inspection display.
Verifier exited 0 and the owned host stopped. This run supplements the earlier
uncertain-delivery runs; it does not simulate delivery loss itself.

Opponent-library controls (2026-10-24): four-seat table
a37a8cef-7b90-4a83-ad22-21391926bdcd passes `--look --opponent-library --four`
(library-opponent-controls-live-final.log). Alex opens Mira's library. Desktop,
portrait and landscape contain no manual-action/search/order controls or card
names. The modal displays the public 57-card count and privacy notice with an
accessible close control and no document horizontal overflow. Opening/resizing
does not change library events or seat revisions. All three
tools/TableQa/library-opponent-controls-{desktop,portrait,landscape}.png captures
were inspected. The normal own-library look, active reload, private observer
views, stop and all-seat final reload then pass. The initial harness assertion
mistook uppercase rendered text for missing text; no product change was needed.
Verifier exited 0; the owned host stopped. This tests one opponent modal in a
four-seat session, not every opponent/viewer combination or physical devices.
Search destination controls (2026-10-24): four-seat table
4a655a17-75ba-4871-acf8-860d236305f9 passes `--search --destination-controls --four`
in library-search-destination-live.log. All five destinations are checked at
1440 × 900, 375 × 667 and 667 × 375: placement exists only for Library and
Enter tapped only for Battlefield. Relevant controls are displayed within the
viewport, without document horizontal overflow. Changing form values leaves
library events and all seat revisions unchanged. The normal private search then
completes with confirmation, hand privacy and all-seat reload checks.
All six Library/Battlefield conditional-control captures under
tools/TableQa/library-search-destination-* were visually inspected; the other
nine states have automated assertions and saved captures, not claimed visual
inspection. Portrait and landscape use the panel's vertical scrolling.
Both owned process handles are now absent. This closes the search-form cell;
the other partial rows remain open.

Maximum-pick search (2026-10-24): table
e2e2b0bb-364b-4998-8561-9ca36ccdb37c passes `--search-battlefield --four`
(library-search-max-pick-live.log). After pending-choice reload the actor selects
two matching Forests, clicks a third and retains exactly the first two selected
options. The third remains unselected. Editing and capturing the selection emits
no library events; explicit confirmation reveals and puts exactly two Forests
onto the battlefield tapped, with no shuffle or hand-count change. All four seats
reload consistently and inspection expires. The three
tools/TableQa/library-search-max-pick-{desktop,portrait,landscape}.png captures
were inspected: desktop and landscape show two highlighted options and Confirm;
portrait shows the rotation notice. The verifier exited 0 and the owned host
stopped. This closes the remaining maximum-pick cell, not the other partial rows.

Intentional zero-pick search (2026-10-24): table
8e6f7d55-a69a-4831-9136-787474d0fe9d passes `--search --zero-pick --four`
(library-search-zero-pick-live.log). Unlike the no-match scenario, the actor has
matching Land options after reload and confirms without selecting any. The run
checks one search, one shuffle, no reveal, unchanged hand and library count,
inspection expiry and all-seat final reload. The three
tools/TableQa/library-search-zero-pick-{desktop,portrait,landscape}.png captures
were inspected. Desktop and landscape show matching options and enabled Confirm;
portrait shows the board's rotation notice, not usable choice controls. DOM bounds
alone do not prove visibility through that overlay. The verifier exited 0 and
the owned host stopped. No production change was needed.

Activation-panel follow-up (2026-10-24): table
b529279d-d282-40fc-9727-44f4c148d3b4 passes the prefix verifier through a
battlefield card click, payment panel, cancellation with unchanged server state,
reopening and Pay and activate. The three replacement choices, recovery, retained
trigger resolution and all-seat reload checks still pass (library-prefix-panel-live.log).
The payment-panel capture was inspected. The first attempt exposed a QA-only
missing payment-descriptor route; enabling the existing QaLibraryPayment mapping
for this fixture fixed that harness issue. No client production change was needed.
This supersedes the activation-panel limitation below. The owned host was stopped.

Ordinary-prefix activation (2026-10-24): four seats pass the normal hub activation
and three browser replacement choices on table 97edfb3b-e9ec-4301-bcf2-0a846c83e8e4.
library-prefix-activation-live.log verifies refund, retained shuffle/library state,
one surviving trigger, resolution and all-seat reload. Landscape first-choice and
desktop actor-final captures under tools/TableQa/library-prefix-activation-* were
inspected. The activation panel itself was bypassed by a normal hub command, so
this run proves transport/choice UI/replay acceptance, not activation-panel UX.
Synthetic artwork is absent. The QA host was stopped; no commit.

Detail-preview revocation (2026-10-24): four-seat table
e425af95-9552-4947-bc31-42ce9651d428 passes --phasing-detail
(library-phasing-detail-live.log). The private top-card detail is open before
the provider phases out; afterward the preview, viewer card and context menu
are absent, top inspection and legal casting are revoked, and all seats reload
at revision 8. The before/revoked landscape captures under
tools/TableQa/library-phasing-detail-shuffle-* were inspected. This separately
closes the global-detail gap in the menu-only run below. Synthetic card art is
absent; physical touch and the final integrated gate remain separate.

Phasing (2026-10-24): four-seat table fdfcc229-93ad-4171-a1cd-5a813d1dc5f3
passes private top-card and legal-cast revocation, cleanup of an open library
viewer context menu, unchanged library count and all-seat reload. Before/after
landscape captures under tools/TableQa/library-phasing-shuffle-* were inspected.
library-phasing-live-final.log is the successful run. An initial projection
bottleneck was fixed with weak snapshot-scoped static-effect mapping reuse:
133 ms view / 31,825 ms legal before, 86 ms view / 612 ms legal after, same
four-player fixture. Full backend gates: 2,073 rules / 840 API; client verify:
1,870 tests plus coverage/build (library-static-snapshot-*.log and sibling
library-phasing-verify.log). This run covers an open context menu, not a global
detail preview or physical touch device. Host stopped; no commit.

Client gate after opening acceptance: npm run verify completed with exit 0,
1,869 successful tests, coverage checks and production build
(mtg-client/library-opening-verify.log). The existing card-modal stylesheet budget
warning remains (18.05 kB against 16 kB). No client production files changed.

Two-player opening (2026-10-24): table
`31272840-6204-46b6-81ea-c9ebbe7aad9d` passes the paid first mulligan,
private bottom choice after both-seat reload, UI card selection, unchanged kept
hand, turn-one main-phase arrival without a starting-player draw, and final
reload (`library-opening-two-counts.log`). Both players' summary and pile counts
are asserted against the server in both browsers. Desktop, portrait and landscape
captures exist under tools/TableQa/library-opening-two-*.png; actor bottom-choice
landscape/final desktop, opponent waiting landscape and portrait orientation
notice were inspected. Synthetic art is absent by design. This closes the
two-player opening flow's browser acceptance, not physical-device testing or
the final combined gate. No client production change was needed.

Live shuffle expiry (2026-10-24): public reveal table
`14718e6e-40ea-4974-92cc-34ebad3291c8` passes the four-seat verifier
(`library-disclosure-shuffle-live-current.log`). Private Look table
`7d946af5-5c09-4911-a626-19df5ca089f3` passes its new `--private` variant
(`library-private-shuffle-live.log`). The production engine/hub revoke active
inspection after shuffle, with all-seat reloads at both pauses and completion.
Public history remains; private Look does not create public reveal events or
opponent inspection. Each run records exactly one shuffle and unchanged event
counts after final reload. Actor landscape and opponent desktop captures were
inspected: active inspection is gone, public reveal history remains only in the
public case, and the private actor retains the explicitly historical last-look
entry. Both owned QA hosts were stopped. This verifies reload reconnection;
forced transport interruption during shuffle itself remains untested.

Face-change follow-up (2026-10-24): all 18 LibraryStaticPermissionTests pass.
The new case spends a front-face permission, serializes/replays a transform,
then spends the distinct back-face permission and verifies its replayed origin.
This establishes separation across faces; it does not claim arbitrary repeated
loss/regain semantics. Test-only addition; the last full production-change suites
remain 2,044 rules / 840 API. Live shuffle/inspection expiry is the next gap.

Permission-origin coverage follow-up (2026-10-24): all 17
LibraryStaticPermissionTests pass. New real-subsystem cases verify that two
external granting permanents produce separate permissions and that removing the
first provider cannot refresh the spent second permission. A copied permission
also remains spent after removing its preceding keyword. Both cases serialize
and replay the play events, checking the recorded origin. These are test-only
additions after the 2,044-rule / 840-API full-suite pass; face-change coverage remains
open. No client change or commit.

Stable permission identity follow-up (2026-10-24): LayerSystem now carries ability
origins through additions and removals. ZonePlayPermissions records these origins
instead of treating the current effective-list position as identity. The reproduced
preceding-keyword-removal regression passes, and an additional test spends two
identical independent grants, replays their events, expires the first grant and
verifies the second remains spent. All 2,044 rules tests pass. This supersedes the
index-identity gap noted below for those scenarios; dedicated copy/face-change and
static-grant-provider coverage remains to be completed. No client change or commit.

Static permission selector regression (2026-10-24): the shared reader ignored
StaticAbility.To, allowing library cards excluded by that selector. Four real
subsystem cases cover matching/excluded lands and spells; the excluded pair
fails before the fix (`library-static-selector-red.log`, 2 passed / 2 failed).
ZonePlayPermissions now resolves that selector through the shared IQuery and
requires the candidate to be included. Full rules pass 2,042 tests
(`library-static-selector-rules.log`); full API passes 840
(`library-static-selector-api.log`). No client change in this follow-up.
The separate once-per-turn identity concern remains: LayerSystem can remove a
preceding keyword or append granted abilities, while StaticPlayPermission still
uses the current ability-list index. This needs a regression and stable origin
tracking; selector coverage does not establish identity stability.

Readiness recovery regression (2026-10-24): the isolated final run passes on
table `a2511841-0f91-42c5-b371-a07e64d476b5`
(`library-ready-recovery-final.log`). The browser loses the completion of an
accepted manualLibraryLook, reconnects, restores private inspection, stops
looking and reloads all four seats. The probe records exactly manualLibraryLook
and manualLibraryStopLooking; exact library order is preserved. This verifies
the existing accepted-response-loss path after clearing stale transport snapshots.
The owned QA hosts were stopped. Nothing committed.

Live-view readiness verified (2026-10-24): REST gameJoined no longer claims a
live connection, join resets readiness, and stateSynced restores readiness and
clears connection errors. A late REST response cannot overwrite an already-live
snapshot. SignalR clears its command-translation snapshot during connect/loss.
The shared readiness selector gates library play/reposition/search controls and
choice controls; inspection details remain accessible. Waiting text is visible
inside inspection and stale available-action headings are hidden.

The previous build reproduces the defect with a held first hub view:
`library-ready-before-build.log` shows Play land and reposition actions still
offered. The land test then passes both held reload and forced reconnect views
on table `b4d20e13-0e7a-47a0-a9e9-feac8b7d88c5` (`library-ready-live-final.log`).
The final visible-status build passes the same barriers plus the full spell flow
on table `68bbbbb0-f15c-46b4-9b69-c96b1f898eb8`
(`library-ready-visible-live.log`). Inspected both waiting-menu captures: the
waiting message is visible and only Card details/Close are offered. Full
`npm run verify` passes 1,869 tests, coverage and build
(`mtg-client/library-ready-visible-verify.log`); existing stylesheet warning remains.
These results supersede the readiness gap below. The separate lost-response
recovery rerun is tracked independently; its earlier no-result run proves nothing.

Library land actions verified (2026-10-24): EngineLegal.playableLands now travels
through engineView, GameViewAdapter and both view/state models independently of
castableCards. LibraryRevealComponent uses the corresponding offer list for each
action; the shared inspection grouping includes playable lands. Regression tests
cover absent/revoked offers, wrong-list IDs and pending-choice precedence. Full
`npm run verify` passes 1,865 tests plus coverage/build
(`mtg-client/library-land-offers-verify-final.log`), with the existing stylesheet
budget warning. The initial run exposed an inspection-only card guard regression;
the final run includes its fix.

Fresh four-seat table `9742cc8f-1bd3-4c58-98b7-8d50c988c9bf` passes
`--top-land --four` (`library-top-land-live.log`): play the first of two top lands,
observe one new land and library 57 → 56 at all seats, verify the next inspected
land has no Play land action after the drop is spent, stop inspection and reload
all seats with REST/hub equality. Inspected desktop/landscape menu and final board
captures (`tools/TableQa/library-top-land-*.png`). The QA host was stopped.
Reload-readiness handling remains open: the client reducer currently marks the
REST gameJoined event connected before the hub view arrives, while stateSynced
does not restore connected after reconnect. The live harness waits for the actual
post-reload hub view; that waiting is not a product fix.

Verified resized-menu run (2026-10-24): table
`1a8abaaa-7dec-49ca-bbed-1359d9c263a8` passes the complete four-seat top-library
spell flow and desktop/667×375 menu-bound assertions (`library-cast-ready-live.log`).
Inspected desktop and landscape menu captures plus `library-top-cast-resolved.png`:
Close is fully visible in landscape, and the resolved board shows library 56
and the additional creature. The QA host was stopped afterward; nothing committed.
The preceding diagnostic run exposed a stale card element during the post-reload
REST/hub transition. The harness now waits for an actual browser ViewUpdated
message before acting. Its click probe confirms the submit button received the
click. This establishes a passing ready-state flow; it does not prove every prior
intermittent failure had that cause or that the UI blocks all pre-ready actions.

First successful live top-library spell run (2026-10-24): fresh table
`18ee58cc-bdc2-4148-b20e-82767ad48b6c` passes menu → shared casting → public
stack → four-seat priority/resolution → private inspection stop → all-seat reload
(`library-top-cast-diagnostic.log`). No submission-code change was made between
the earlier failures and this successful run, so their intermittent cause is
not established. Do not describe them as fixed. The shared menu now marks its
OnPush view for checking on window resize and bounds its height with scrolling.
Full `npm run verify` passes 1,862 tests, coverage, lint and production build
(`mtg-client/library-menu-resize-verify.log`); the existing card-modal stylesheet
budget warning remains. The live harness now asserts menu bounds after resize.

Live static-permission follow-up (2026-10-24): the fresh real-permanent fixture
now displays Cast in the inspected top card's menu. Inspected the landscape
capture `tools/TableQa/library-top-cast-menu-landscape.png`: action is visible,
but the bottom of the menu clips after viewport resize. The four-player cast
submission still times out (`library-top-cast-live-final.log` and
`library-top-cast-live-retry.log`); do not mark live casting complete. A direct
Command invocation on the first failed table successfully changed stack 0 → 1,
isolating further investigation to browser submission/state delivery rather
than the original missing static permission. The retry's outgoing-command probe
was empty and its revisions were [4,3,3,3], with no refusals on the harness hubs.
Failure diagnostics now include browser logs, compact per-seat counts and a
named stack-transition timeout for the next fresh run. The client still needs
independent playable-land IDs and its submission completion check still assumes
the source started in hand; neither is closed by the backend tests.

Static permission implementation follow-up (2026-10-24): ZonePlayPermissions now
owns the shared resolution/static permission lookup used by land plays and cast
plans. It reads current characteristics/controller, source zone, face-down and
phased-out status, conditions and card filters. Static permission use is recorded
as source object plus ability slot, separately from legacy ActiveEffect IDs, on
both land and spell events/history. The nine library regressions pass, including
the two formerly failing live-permanent cases and once-per-turn replay/source
isolation. Full rules: 2,038 passed (`library-static-permission-rules.log`). Full
API: 840 passed (`library-static-permission-api.log`). These supersede the red
backend counts below. Browser acceptance is recorded separately when complete.
Additional coverage is still needed for ability-slot changes through granted or
removed abilities and alternate static selector shapes; the current identity
does not carry a separate grant-origin identity.

Library permission isolation (2026-10-24): four new real-subsystem regression
cases separate static permission discovery from legal-action enumeration.
`library-static-permission-red.log` reproduces both real-permanent failures.
`library-legal-offer-red-retry.log` independently proves that an already-recognized
top-library permission still produced no offered spell or land. LegalActions now
includes the player's library candidates and delegates every candidate to the
existing casting checks. The two enumeration cases and 14 existing legal-action
tests pass; the two real-permanent cases still fail (`library-legal-offer-tests.log`,
16 passed / 2 failed). No end-to-end success is claimed. The owned four-seat QA
host was stopped before the regression runs.

Remaining implementation must read current static permissions, including source
controller, face-down/phased-out/zone status, conditions and card filters. Both
land and spell paths must share that permission lookup. Once-per-turn static
permissions also need a stable source/ability identity distinct from existing
ActiveEffect integer IDs; do not synthesize colliding IDs or inject a fixture-only
effect to hide the missing production path. Client land actions additionally need
the independent legal.playableLands list carried through its transport/model.

Inspection cast action follow-up (2026-10-24, incomplete): the client now delegates
an inspected card's server-offered Cast action to CardCastingService. The full
`npm run verify` passes 1,862 tests (`mtg-client/library-inspection-cast-verify.log`).
The four-seat `--top-play` live run fails before casting: table
`7a5142ed-86f1-4caa-bc62-0e6ce4da81ee` does not offer the inspected top card in
the authoritative `legal.castable` list (`library-top-cast-live.log`). The failure
capture confirms that its menu contains details and reposition actions only.
This is not a verified end-to-end casting implementation.

The fixture puts a permanent with a printed MayPlayFromZone static ability onto
the battlefield. CastPlan.Allowing currently reads state.ActiveEffects; the
existing top-library permission test creates a ContinuousEffectCreated record
directly. Next investigate this static-permission integration seam, retaining
the real permanent fixture rather than injecting a permission merely to make
the browser test pass. Also verify playable land IDs separately: the server
publishes legal.playableLands independently from legal.castable.

Artwork-load failure follow-up (2026-10-24): `QaUnavailableArtwork` supplies a
loopback HTTP-404 image through the production GameArtworkService. The initial
four-seat run (`library-broken-art-live.log`) preserved readable text and keyboard
controls, but inspected captures showed a broken-image box wasting vertical room.
CardModalComponent now remembers failed image URLs for its lifetime and uses its
existing placeholder; the magnifier closes and another face's distinct image
remains eligible. The new face-switch regression passes. `npm run verify` passes
all 1,861 tests, coverage and production build (`mtg-client/library-broken-art-verify.log`),
with the existing 18.05/16 kB stylesheet warning.

Final table `0970d9df-d2b5-443c-90a9-f0d933de3d54` passes the four-player
`--broken-artwork` run (`library-broken-art-live-final.log`). Browser resource timing
confirms an actual image HTTP 404; the failed detail image is removed and the
placeholder appears. Focus, Tab, Escape, return focus, no detail-command mutation,
private inspection/stop and all-seat reload pass. Inspected all three
`tools/TableQa/library-detail-broken-art-{desktop,portrait,landscape}.png` captures:
name, type, rules text and power/toughness are readable with no document overflow.
This closes the detail-window image-failure case; it does not claim new error
handling for every image elsewhere on the board. The isolated host was stopped.

Library card-detail keyboard follow-up (2026-10-24): the browser regression first
failed because opening details through Shift+F10 left focus outside the detail
window (`library-detail-focus-red.log`). LibraryRevealComponent now applies the
existing DialogFocusDirective and dialog name/role to its card-detail instance;
no parallel focus implementation was added. `npm run verify` passes all 1,860
client tests, coverage and build (`mtg-client/library-detail-focus-verify.log`).
A redundant optional chain was then removed and the production rebuild passes
(`library-detail-focus-build.log`); the existing 18.05/16 kB stylesheet warning
remains.

Four-seat table `4fc182fb-3ce4-403a-95a5-4b1a0cd4a244` passes Shift+F10 → Enter,
focus entry, forward/reverse Tab containment, Escape and return to the inspected
card, with unchanged game revisions during details (`library-detail-focus-live.log`).
The normal private-look/stop/broadcast/all-seat reload checks also pass. Inspected
`tools/TableQa/library-detail-keyboard-{desktop,portrait,landscape}.png`: name,
type, rules text and power/toughness remain readable without artwork, with no
document overflow. This verifies absent artwork in the synthetic fixture, not a
network image-load failure. No-art fallback and keyboard return are now covered;
broken-image behavior remains distinct. The isolated host was stopped.

Pending-arrangement rejection (2026-10-24): final table
`c1b5ecad-5304-482f-b200-bbb2bea82918` passes `--reject-arrange --four`
(`library-arrange-rejection-live-final.log`). The initial look completes and the
edited confirmation is held before sending. Another connection stops inspection;
the draft, Arrange control and private inspection disappear before release.
The rejected confirmation sends once, changes no library order or event counts,
and all seats agree after reload. The first successful functional run exposed
an incorrect, truncated generic permutation warning. ManualLibraryCoordinator now
uses the existing IsActive check to report “Library inspection ended. Look again.”
before validating the permutation. All six ManualLibraryMovementTests pass
(`library-arrange-rejection-tests.log`). The final live run asserts this exact
message and no text overflow at desktop, portrait and landscape; all three
`tools/TableQa/library-arrange-rejected-*.png` captures were inspected. No client
source changed. The isolated host was stopped. This closes the draft-loss-during-
submission scenario previously left open in the pending-search follow-up below.

Pending-search rejection follow-up (2026-10-24): table
`68909be2-cdf0-415c-ab01-7b9dfdc1f7dc` passes four-player `--search --reject-busy --four`
acceptance in `library-search-rejection-live.log`. The browser holds one search
invocation; a second authenticated connection concedes the same player, then the
held request is released. The server displays “Library actions are unavailable
right now.” No game events are added after concession by the rejected request,
no private inspection or choice appears, and all-seat reload agrees with broadcasts.
Inspected `tools/TableQa/library-search-rejected-{desktop,portrait,landscape}.png`:
the form has disappeared and the warning is readable, including over the portrait
rotation notice. One outgoing manual request is asserted. This closes the
pending-search permission-loss/rejection scenario only; an existing arrangement
draft losing permission during submission remains a separate acceptance item.
Only the QA harness changed. The host was stopped; no production rebuild or
client verification was needed for this test-only follow-up.

Explicit reveal → shuffle follow-up (2026-10-24): four-seat table
`c5c09390-5038-42f1-a171-31de7315f3df` passes public two-card inspection, revocation
after shuffle, historical reveal retention, both pending-choice reloads and final
all-seat reloads. Exact one reveal/one shuffle and no refusals are asserted in
`library-disclosure-shuffle-live-final.log`. Inspected actor landscape/opponent
desktop before and after shuffle, plus the actor's portrait rotation notice in
`tools/TableQa/library-disclosure-shuffle-*.png`. These synthetic fixture cards
have no production art. The expanded inspection must be collapsed to reach the
underlying choice; the harness uses the normal summary control.

Same-step token movement follow-up (2026-10-24): table
eb8fc455-decb-43ec-80f8-b5a9cc434b57 passes the browser replacement choice,
private views, exact token creation/death-history recovery, retained-trigger
resolution and four-seat reloads. Inspected tools/TableQa/library-token-movement-choice-1-landscape.png,
its portrait rotation notice and library-token-movement-resolved-seat-0.png.
Evidence: library-token-movement-live.log. QA definitions have no production art;
this verifies the spell-resolution payment path, not earlier activation admission.

Token recovery follow-up (2026-10-24): four-seat tables
fb643e42-1dce-4916-b8ed-f44bf2d8b5dc (independent token removed) and
1bcf86e5-48bc-4af0-802a-892f776f3d55 (token consumed by retained library operation)
pass choice reload/rotation, private views, exact history/trigger disposition and
all-seat final reloads. Inspected both matching landscape choice and resolved-seat-0
captures under tools/TableQa, plus library-token-retained-choice-1-portrait.png.
Logs: library-token-recovery-live.log and library-token-retained-live.log. These
use QA definitions without production art and the spell-resolution payment path.

Mixed-chain recovery follow-up (2026-10-24): table
c91903a8-1c4b-475d-8231-273bbfd13e33 passes the browser replacement choice,
private views, four-seat reloads and one retained trigger resolution. Inspected
tools/TableQa/library-mixed-chain-choice-1-landscape.png, its portrait rotation
notice and library-mixed-chain-final-seat-0.png. The independent artifact visibly
returns to the battlefield; exact identity and tap restoration are asserted in
the harness. Evidence: library-mixed-chain-live.log. QA fixture definitions have
no production card art. Earlier activation admission is not covered by this run.

Commander hand-entry recovery follow-up (2026-10-24): table
795f1c3f-1bdb-42c8-9d26-57fa9da53f1a passes two prompts, private views, four-seat
reloads, restored commander identity/tap state and zero fired triggers. Inspected
tools/TableQa/library-commander-hand-choice-1-landscape.png, its portrait rotation
notice and library-commander-hand-final-seat-0.png. The landscape buttons pass
reachability/44px checks. Evidence: library-commander-hand-live.log. This is the
spell-resolution payment path with QA definitions, not earlier activation admission.

Commander library-entry recovery follow-up (2026-10-24): table
4d711ac3-990a-4ad4-8618-dd3a2575a67a passes two browser prompts, private views,
four-seat reloads and retained-trigger resolution. The Return to command zone
and Move to Library buttons pass landscape reachability/44px checks. Inspected
library-commander-chain-choice-2-landscape.png, its portrait rotation notice,
and library-commander-chain-resolved-seat-0.png under tools/TableQa. These are
QA fixture cards without production art. See library-commander-chain-live.log
and LIBRARY_RESUMED_FAILURE_CONTRACT.md for the exact behavior and scope.

Source audit: 2026-10-24. This is the finite UI inventory for the existing library
scope, not a replacement for the library implementation plan. Source branches
were read in `zone-viewer.component.html/.ts`, `library-arrangement-draft.service.ts`,
`library-actions.service.ts`, and `tools/TableQa/verify-library-payment.js`.

Required viewports: desktop, 375 × 667 portrait, 667 × 375 landscape. The board's
portrait rotation prompt is an expected state where applicable; a screenshot of
that prompt does not verify hidden landscape controls. Browser touch emulation
does not establish physical-device acceptance.

| State or transition | Existing evidence inspected | Remaining acceptance |
| --- | --- | --- |
| Own hidden library, opponent library, public top | Viewer explicitly separates count from visible cards; public-top and manual-look harness paths exist. | Reconcile existing captures per viewport; verify opponent has no manual controls. |
| Empty library and single-card library | Arrangement disabled below two cards; hidden empty text uses count. | Explicit browser captures and assertions for counts, disabled arrangement, draw outcome and privacy. No current capture proof established by this audit. |
| Look / stop looking | Harness supports both lost-response paths and reload. | Link each viewport capture to its successful run; retain private/opponent projection assertions. |
| Search form: normal, invalid count, destination-dependent controls | Template requires nonnegative integer count; placement only for Library, tapped only for Battlefield. Successful filtered-search log `library-search-controls-live.log`, table `701a460e-aff5-4e5f-bab3-66eddf8519f5`. | Invalid-input screenshots and no-command assertion; all conditional form controls across required viewports. Existing successful form captures alone do not prove invalid state. |
| Search selection and explicit confirmation | Same successful log establishes private inspection, pending reload and confirmation. | Reconcile zero matches, zero picks and maximum picks with capture evidence. |
| Draw/shuffle pending replacement | Harness captures pending desktop/landscape and refreshes while pending. | Match existing images and logs for each viewport; confirm portrait behavior separately. |
| Arrangement draft, clear, reorder, cancel, confirm | Harness asserts local edit/cancel sends no command, visible order, 44px confirmation, no horizontal overflow, and portrait/landscape/desktop captures. | Confirm empty/single-card boundary above; keep exact-order server assertion separate from visual order. |
| Arrangement permission revoked | Successful `library-order-revocation-live.log`, table `50102311-2367-4ea9-bf54-a549091c5048`, four seats and touch: editor disappears, arrange control disappears, reinspection succeeds. | Inspect `library-arrange-revoked.png` and establish remaining viewport evidence. Do not call this functionally untested. |
| Reposition top/bottom and mode guard | Successful `library-reposition-mode-guard-live.log`, table `0e60b43b-830e-4c5f-9627-6d740f62c8f6`; harness asserts inspection expiry and reload, has desktop/landscape menu captures. | Reconcile top/bottom and touch images; explicitly verify guard visibility at each required viewport. |
| Busy request | Template disables action/confirm/cancel controls; service blocks overlapping execution. | Hold an actual request pending and capture disabled controls; attempt repeated input and assert one command. Service tests are not visual proof. |
| Lost response, warning and retry | Harness captures warning desktop/portrait/landscape, reloads, verifies request identity and event counts. Successful warning-owner run `library-warning-owner-live.log`, table `d653cbcb-5958-4407-8666-5aadd3e987b1`. | Reconcile image ownership and readability with recorded run. Distinguish delivery uncertainty from server validation errors. |
| Server rejection / permission loss during submission | UI catches failures into an unconfirmed-action status; arrangement reconciles permission and identity changes. | Dedicated rejected-submission capture, no duplicate command, stale draft removal and private-card disappearance. Permission revocation while idle is a distinct already-tested transition. |
| Card detail from visible/inspected library card | Viewer has click, context-menu and keyboard detail paths; unavailable images hide the image element. | Record keyboard/detail focus return and missing-image readability in required viewports. Never use a hidden card identity to open detail. |

Completion evidence for each row is a successful harness result plus inspected
screenshots at the required viewports and the row's state/command/privacy
assertions. A file existing or a component unit test passing is insufficient.
No new capture run or UI completion is claimed by this source audit.

## Invalid-count acceptance — 2026-10-24

The subsequent `--search --invalid-search --four` run reproduced an enabled
submit button for fractional counts (`library-invalid-search-red.log` and
`tools/TableQa/library-search-invalid-fractional-desktop-before.png`). Empty and
negative counts correctly disabled submission when edited through keyboard input.
Selenium `clear()` alone did not notify Angular reliably, so the harness now uses
Ctrl+A/Backspace before entering each value.

The client now shares `validSearchCount` between button enablement and command
validation. `library-invalid-search-live.log`, table
`effc075c-4710-4f69-afe9-c8c3191f3e02`, passes all nine invalid-input/viewport
combinations, unchanged event counts and view revisions, then a valid filtered
search, pending reload, hand privacy and all-seat broadcast/reload.
Captures are `tools/TableQa/library-search-invalid-{empty,negative,fractional}-{desktop,portrait,landscape}.png`.
The three fractional captures were visually inspected; portrait and landscape
show the form and submit control within the dialog. This closes invalid count
functional acceptance across those viewports, not every row above.

Full client `npm run verify` passes 1,860 tests, coverage, lint, formatting, UI
checks and production build (`mtg-client/library-search-count-verify.log`). The
existing 18.05 kB card-modal stylesheet warning remains.

## Pending search acceptance — 2026-10-24

`library-search-busy-live.log`, table `5a5ab0ca-e9b6-4726-8e6a-a945e7abe91f`,
passes `--search --busy-request --four`. The QA transport holds the real first
manual command before sending it, keeping its invocation pending. At desktop,
portrait and landscape, every manual-action button and search submit is disabled;
repeated click attempts produce exactly one outgoing command, no server event
change and no view revision. Releasing the held frame completes the search,
private selection, reload and all-seat broadcast normally.

Inspected all three `tools/TableQa/library-search-busy-{desktop,portrait,landscape}.png`
captures. Functional exclusion passes, but the visual inspection exposes a
remaining usability gap: there is no visible pending-status message, and disabled
buttons look very similar to idle buttons. `libraryBusy` currently controls
disablement while `libraryStatus` is cleared at request start. Pending feedback
therefore remains outstanding; this row is not wholly complete.

The probe is isolated to QA and is enabled only for search without response-loss
injection. No production behavior changed in this checkpoint.

## Visible pending feedback — 2026-10-24

The viewer now renders `Sending library action…` in its status region while busy,
and search submit reads `Sending…`. This uses the existing request state and
does not add another pending flag. The status region remains available for the
existing uncertain-delivery message after failure.

`npm run verify` passes all 1,860 tests and all other gates, including production
build (`mtg-client/library-pending-status-verify.log`). The existing card-modal
stylesheet budget warning remains.

The updated busy harness asserts both pending messages in every viewport,
disabled controls, one outgoing command, unchanged events before release,
then successful search/reload/privacy/broadcast. Direct-output session 25447
completed with exit code 0 and a success report for table
`07d4b34d-827a-4165-bee8-dfd109f27cb3`. The preceding run produced captures but
no completion report, so it is not used as full acceptance. The three updated
`library-search-busy-*.png` captures were inspected: desktop shows the status
and button label; portrait and landscape show the sending label at submit.
The pending-search feedback gap is closed. Other action-specific pending
surfaces in the inventory remain to reconcile.

## Empty and single-card inspection — 2026-10-24

Added a QA-only `libraryCount=0|1` fixture using ordinary recorded zone moves,
then replay through the production session service. The existing browser harness
now supports `--small-library --look --four`.

Both direct-output runs completed successfully:

- Empty: table `c4f19d8e-acdb-4dbe-872e-5cf9696cb483`, session 65836.
- Single card: table `108b3552-920d-4395-bec4-f329b585f37a`, session 16279.

Each verifies the displayed count, owner inspection size, no opponent inspection
cards, disabled arrangement, stop removing inspected cards, and unchanged
authenticated snapshots after all four browser reloads. All six captures
`tools/TableQa/library-{0,1}-cards-{desktop,portrait,landscape}.png` were inspected;
the count and controls fit within each viewport and the document does not overflow.
These runs establish the inspection/ordering boundary. Empty-library draw/loss
behavior and zero-match search are distinct checks still outstanding in this UI
inventory. No production code changed in this checkpoint.

## Search with no matching cards — 2026-10-24

`--search --zero-match --four` requests one card with an unmatched subtype filter;
it does not request zero cards. The harness expects zero selected arrivals.
Direct-output session 21659 passed on table
`c72f6e68-3289-43bb-ba64-d9237cb8b119`: owner inspection remains available,
opponents receive neither identities nor options, reload preserves the empty
search choice, confirmation is enabled, and confirming emits exactly one search
and one shuffle without reveal, hand change, or library-count change. All four
authenticated views reload consistently and no command is refused.

Inspected `tools/TableQa/library-search-zero-match-{desktop,portrait,landscape}.png`.
Desktop and landscape show the empty choice with Choose none/Confirm controls;
portrait shows the existing rotation prompt. No production code changed.

## Empty draw: loss passes, priority defect reproduced — 2026-10-24

Direct-output session 33078, table `71cfdea9-62ab-4858-815c-b824510bdf07`,
passes `--small-library --empty-draw --look --four`. The actor's browser draw
produces exactly one `DrawFromEmptyLibraryAttempted`, no `ObjectDrawn`, and
`hasLost=true` in every seat's view. Other players remain in the game; actor
manual library actions become unavailable. All-seat reload preserves the result.

However, inspected `library-empty-draw-{desktop,portrait,landscape}.png` captures
show the eliminated actor still holding priority and seeing Pass priority.
This flow is **not complete**. Source inspection confirms `PlayerLost.Apply`
removes owned objects but leaves `state.Priority` unchanged. Local CR 800.4a
explicitly passes an outgoing priority holder's priority to the next surviving
player; CR 800.4j also governs subsequent grants during their unfinished turn.
Next fix must address authoritative priority succession and replay, rather than
merely hiding the stale action button. No production change made in this run.

## Empty-draw priority repair — 2026-10-24

Four replay regressions first failed with the departed priority holder unchanged
(`library-empty-draw-priority-red.log`). `PlayerLost.Apply` now transfers held
priority to the next surviving seat and removes that player's prior pass while
preserving other passes. `PlayerTurnOrder.NextLiving` owns the lookup and is also
used by the existing turn-system succession method, avoiding duplicate loops.
The unfinished turn remains the departed player's turn; existing turn grant
logic gives priority to survivors at subsequent steps.

All 13 priority tests, 1,953 full rules tests and 840 API tests pass
(`library-empty-draw-priority-tests.log`, `library-loss-priority-rules.log`,
`library-loss-priority-api.log`). Replay cases cover wraparound, a previously
eliminated next seat, refusal of the departed player's pass, and progression to
the next step after all survivors pass.

Live session 20999, table `a87cc85f-8f23-435e-b87d-49920aea89dc`, passed the
empty-draw check with all views asserting priority holder 1 and all four reloads
unchanged. Updated desktop and landscape captures were inspected: Mira has
priority and Alex's Pass priority control is disabled. This verifies the
reproduced immediate-transfer defect; turn progression is covered by the replay
tests rather than this live run. The local seat still lacks an explicit
elimination label in focus view, a separate presentation gap.

## Elimination presentation and live continuation — 2026-10-24

The local player panel now renders an explicit Eliminated status. The focus
action panel says You are eliminated and explains that remaining players can
finish the game. Both read the existing `hasLost` value.

`mtg-client/library-elimination-label-verify.log` passes full verification:
1,860 tests, coverage, formatting, lint, UI checks and production build, with
the existing card-modal style-size warning. Live session 79614 passes on table
`6bc9e016-9606-4d6b-a0c5-1bdb134621ac`. It asserts the rendered status and
prompt, then has seats 1, 2 and 3 pass through the production hub. Every view
advances to BeginningOfCombat with seat 1 holding priority, followed by four
unchanged reloads. Updated desktop/landscape captures were inspected and show
the eliminated status without clipping. The reproduced empty-draw loss,
immediate priority transfer, local presentation and next-phase continuation
are now verified together. Broader library requirements remain open.

## Last-card boundary — 2026-10-24

Live session 22233, table `a0ccc604-846d-4d12-b4e1-c7a568fdd33b`, passes
`--small-library --empty-draw --look --four` starting with one card. The first
draw emits one ObjectDrawn and no empty-draw attempt; every view keeps the player
alive, the owner gains one hand card, and opponent projections contain no identity
for that arrival. Reload preserves this intermediate state. Inspected
`tools/TableQa/library-last-card-drawn.png`: zero library, increased hand, no
elimination label and normal priority.

The next draw then emits exactly one empty attempt, eliminates that player,
transfers priority, renders the elimination message, and allows all three
survivors to pass into the next phase. All four final reloads preserve the result.
This distinguishes exhausting the library from attempting to draw after it is
empty. No production change was required for this boundary check.

## Retained tap trigger through library recovery — 2026-10-24

Four-player table `e240698f-ad33-499f-8324-5c749db94708` passed the public
spell-resolution recovery flow with `verify-library-nested-payment.js --tap-recovery`.
Three choice prompts were exercised after reload, with private-context checks,
landscape button reachability, portrait rotation notice, and all-seat final reloads.
One recovery, one shuffle and one retained trigger occurred; that trigger resolved
once. Inspected `library-tap-recovery-choice-2-landscape.png`,
`library-tap-recovery-choice-2-portrait.png` and `library-tap-recovery-resolved-seat-0.png`
in tools/TableQa. Details and the narrower scope of this effect-path proof are in
LIBRARY_BATCH_PROPERTY_RECOVERY.md. The earlier activation admission gate remains.

Follow-up table `51390545-ad5d-42b6-a611-d5d023606dd5` passes `--tap-batch`,
with no pause between tapping and moving the artifact. Its one mill-replacement
prompt survives reload/rotation; recovery leaves life 40, library 3 and exactly
one tap trigger, which resolves once. Four-seat reloads preserve both states.
Inspected `library-tap-batch-choice-1-landscape.png`,
`library-tap-batch-choice-1-portrait.png` and `library-tap-batch-final-seat-0.png`.
