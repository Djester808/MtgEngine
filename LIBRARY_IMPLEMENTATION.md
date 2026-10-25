# Commander library implementation

## Basic card contract — 2026-10-25

Library creation now delegates to `GameObject.CreateCard`, using the existing
definition, selected printing/finish, owner and allocated identity. The factory
rejects malformed selections, mismatched definitions, missing face data and invalid
initial ownership/zone combinations. `CardDefinition.GetFace` bounds face access;
rules text remains descriptive data. No additional catalog or mutable card store
was introduced. Existing movement, order, search, shuffle, disclosure and event-log
owners retain their responsibilities and wire format. Tokens, spell copies and old
logs keep their existing construction/replay paths.

Evidence and method comparison: `research/CARD_CONTRACT_EVIDENCE.md`. Focused
acceptance: `tests/MtgEngine.Rules.Tests/CardContractTests.cs`; existing
`DeckSelectionTests` continue to cover privacy, movement, copies and old logs.

## Superseding scope correction

The user clarified that this task is to define a deck and its basic interactions,
not implement keywords. The keyword, replacement/payment expansion and exact MTGO
keyword-comparison backlog below are historical overreach, not remaining gates.
See `research/DECK_SCOPE_CORRECTION.md` for the inspected keep/extract/remove
boundary and cleanup sequence. Reuse the existing collection/card model and keep
one authoritative library state. Existing tests remain fixed contracts.

Current status and verification are in `research/DECK_SCOPE_CORRECTION.md`, under
the test-scope cleanup dated 2026-10-25. Active keyword/payment expansions and their
dedicated tests have been removed. Basic deck/library operations remain. No commit.

Everything below this point is historical and does not describe currently enabled
payment/recovery capabilities or additional work to implement.

## Current readiness — 2026-10-24

Latest checkpoint: ordinary-prefix replacement pauses followed only by library
costs are publicly admitted through the shared activation journal/recovery path.
Client npm run verify exits 0 with 1,870 tests, coverage and production build
(sibling library-prefix-panel-verify.log); existing stylesheet budget warning only.
Full backend gates pass 2,080 rules / 840 API tests in
library-prefix-admission-rules.log and library-prefix-admission-api.log.
Four-player UI acceptance passes on table b529279d-d282-40fc-9727-44f4c148d3b4:
card click, payment panel, cancel/reopen/confirm, three replacement choices,
recovery, retained trigger resolution and all-seat reload
(library-prefix-panel-live.log). This supersedes older blanket non-library-prefix
pause restrictions below; non-library remainders remain restricted. Ownership
and bounded saved-choice compatibility are recorded in
research/LIBRARY_OWNERSHIP_AUDIT.md. The remaining-work register remains the
authoritative finish checklist; this checkpoint does not establish completion.

Explicit public reveals now use resolution-scoped inspection provenance for
bounded mill departure observation. Private Look-to-all and expired Reveal do
not qualify. Cost/effect, serialized pause and replacement-local Reveal cases
pass the full 2,023-test rules suite (`library-explicit-reveal-rules.log`).
This supersedes the explicit-reveal gap below; broader global-state look-back,
legacy-event and reorder/expiry combinations remain open. See
`research/LIBRARY_HIDDEN_MILL_RULES.md` for the exact evidence boundaries.

Revealed-top mill look-back now preserves the public departing card's evaluated
characteristics when it moves to hand. Twelve cost/effect and paused replay cases
pass, including unrevealed controls, bounded observation privacy and compound
filters after the characteristic provider leaves. General
hidden-destination trigger semantics remain incomplete; see
`research/LIBRARY_HIDDEN_MILL_RULES.md` for the exact remaining boundaries.

Public activation now admits an earlier library-cost pause when only library
costs remain and shared recovery assessment supports the completed prefix.
Successful repeated choices and later failure/refund pass public-command replay
tests. Four-player live success now passes (`library-public-earlier-live.log`),
including an explicit check that the first prompt has another unpaid library cost,
private choices and all-seat reload. Four-player later-cost failure/recovery also
passes (`library-public-earlier-failure-live.log`): one retained mill, one recovery,
life refunded, no stacked activation and unchanged all-seat reloads. Non-library pauses,
unsupported prefixes and non-library remaining costs retain their restrictions.
This checkpoint supersedes blanket earlier-pause statements below.

The library is not complete. The entries below this section are chronological
checkpoints; their statements about unfinished work and suite totals describe the
state at that checkpoint, not necessarily the current implementation.

Current verified backend baseline: 2,006 rules tests
(`library-public-mill-compound-rules.log`; final cleanup also passes all 18 hidden-mill
tests in `library-public-mill-compound-final-tests.log`) and 840 API tests
(`library-mill-observation-privacy-api.log`, including persisted observation
redaction from both player views and direct choice pushes). Token creation recovery now validates and removes
independent creations/history while preserving later retained dependencies and
mapping exact trigger origins. Both token dispositions pass four-player live
acceptance (library-token-recovery-live.log and library-token-retained-live.log).
Same-step token movement to graveyard now passes replay/history/trigger checks
and four-player live acceptance (library-token-movement-live.log); broader layouts
remain outstanding. Multiple-token replay tests now verify independent reversal
and selective retention, including the exact retained token history and trigger
subject. Four-player selective multiple-token acceptance now passes
(`library-token-selective-live.log`): two creations before the choice, one retained
creation/trigger afterward, original artifact restored, and unchanged results
after all-seat reload. These tests required no production changes. Mixed movement chains now recover independently
when only one card is consumed by the library; four-player live acceptance passes,
including independent tap cancellation (library-mixed-chain-live.log).
Earlier Commander hand-entry return now has
validated terminal movement recovery with replay and four-player live acceptance,
including cancellation of the undone tap trigger (library-commander-hand-live.log).
Commander library-entry redirection now
preserves dependencies of an already-retained shuffle step during recovery.
Four-player live acceptance for that chain passes on the spell-resolution payment
path (library-commander-chain-live.log); earlier activation admission is unchanged.
Choice redaction now constructs only the
permitted display context, replacing duplicate private-field clearing lists;
server replay retains the full continuation. Operation resumption is now separated from
payment handling; the remaining failure contract is documented in
`research/LIBRARY_RESUMED_FAILURE_CONTRACT.md` and is not yet implemented.
Activation recovery now refuses records with
missing nested-origin coverage rather than inferring no earlier nesting; the new
regression reproduced acceptance before this guard and passes after it.
Shared `NestedPaymentEvidence` now preserves
nested ownership for effect and activation recovery without duplicating steps.
The internal activation partial-recovery regression passes serialized choices and
normal answer commands; initial public admission remains gated. See the ownership
audit for the remaining admission requirements. The latest production client baseline is
1,860 tests plus coverage/build (`library-elimination-label-verify.log` in the
client repository). Library request IDs persist per table/player across page reload.
Completing Look/Stop now retires the older uncertain opposite inspection request;
both lost-response directions pass two live inspection cycles and all-seat reload.
Four-browser accepted-draw and accepted-shuffle response loss, reconnect, full reload
and same-ID retry are verified. The shuffle run also proves exactly one shuffle event
before retry and no additional library events afterward (`library-shuffle-reload-live.log`).
Default manual search now also passes four-browser accepted-response loss,
reconnect, actor reload and same-ID retry, with exactly one search/one shuffle and
unchanged state/event counts after retry (`library-search-retry-live.log`).
Other action variants still need corresponding live acceptance.

| Remaining requirement | Current boundary and next proof needed |
| --- | --- |
| Interrupted library payment completion | Normal activation also admits earlier mill/library-exile pauses when only library costs remain and shared recovery assessment accepts the completed prefix. Public-command replay and four-player success/failure acceptance pass (`library-public-earlier-live.log`, `library-public-earlier-failure-live.log`). Non-library pauses, unsupported prefixes and non-library remaining costs remain restricted; compare those boundaries with required library interactions before widening admission. |
| Failure inside the interrupted operation | `ChoiceSystem.Resume` computes the resumed operation before calling `RetainPayment`. A thrown refusal there has no partial-outcome recovery contract. Two diagnostic Unsupported-effect tests now reproduce immediate/after-shuffle throws: the answer is refused without publishing partial events, and a supported alternative completes after replay with unchanged RNG/order. Unpayable replacement `PayCost` is separately handled as unpaid. Automatic recovery when no alternative can finish remains unverified; diagnostic injection is not evidence of an ordinary legal-card defect. |
| Recovery of all supported cost combinations | Movement batches can retain dependent cards while restoring independent cards. Mixed movement/life steps now reverse validated independent life occurrences with event-level provenance; engine-generated failure/replay passes. Other dependent mixed-property steps and incomplete steps remain unsupported. |
| Nested movement replacement history | Shared journals and event-level nested ownership support tested whole-step and partial recovery, including serialized effect/activation continuations and source departure. Normal-command and four-browser evidence covers the recorded nested/partial scenarios; it does not establish arbitrary nesting or every mixed-property recovery. Earlier activation pauses remain restricted. Mill effects and costs share event emission; hidden hand/library filtered triggers, replacement-created extra-card references, and old paused-log migration remain unresolved. See `research/LIBRARY_OWNERSHIP_AUDIT.md`, `research/LIBRARY_ACCEPTANCE_AUDIT.md`, and the remaining-work register below. |
| SOLID ownership | Ordering/shuffle, draw, opening-hand and inspection/disclosure call-site audits are complete. Interrupted payment responsibilities are mapped, with an unresolved outcome/recovery contract for exceptions inside resumed operations. Do not treat that map as full recovery support or complete ownership acceptance. See `research/LIBRARY_OWNERSHIP_AUDIT.md`. |
| Four-player and reconnect acceptance | Recorded live coverage includes all-player scry/surveil, active-seat-2 ordering, draw/shuffle/search response-loss retry, both Look/Stop retry directions, arrangement and top/bottom reposition retries. Cross-action arrangement/reposition and A → B → A runs also pass. Reconcile the finite UI inventory with its recorded follow-ups; these runs do not establish arbitrary interleavings. See `research/LIBRARY_ACCEPTANCE_AUDIT.md` and `research/LIBRARY_UI_ACCEPTANCE_MATRIX.md`. |
| MTGO method comparison | Keep recovered method evidence separate from inferred behavior. Local passing tests do not establish unknown MTGO server algorithms or complete interaction parity. See `research/mtgo/LIBRARY_METHOD_PARITY.md`. |
| Final UI acceptance | Complete the library-state inventory at required viewport sizes. Nested payment choices now pass landscape 44px/reachability/hit-testing and orientation-preserved choice IDs (library-nested-phone-live.log); portrait rotation-notice capture has now been visually confirmed, with identical SHA-256 hashes for all three pending-choice captures. The earlier blank-capture diagnosis was incorrect. Browser touch emulation is verified in recorded scenarios; physical-device behavior remains unverified. |

The actionable open items are tracked in `research/LIBRARY_REMAINING_WORK.md`.

No production card expansion or commit is authorized by this checklist. Scope
remains the library. Detailed evidence is recorded in the acceptance, ownership
and payment-rules audits under `research/`.

## Historical checkpoints — 2026-10-23

Live arrangement expiry and browser touch verified on four-seat table
50102311-2367-4ea9-bf54-a549091c5048. Native Chrome touch events operated the
landscape editor, moved cards, cancelled locally and confirmed the exact order.
While a draft was open, a second authenticated connection ended the same player's
inspection; the editor and Arrange control disappeared. A fresh look restored
eligibility and a new draft completed normally. Private reinspection and final
reload agreed across seats. Evidence: library-order-revocation-live.log; inspected
tools/TableQa/library-arrange-revoked.png and
library-arrange-touch-revocation-live-four-editor-landscape.png.
Only the QA harness changed; production/client verification remains 1,849 tests.
This is browser-emulated touch, not physical-device acceptance. No commit.

Full-library ordering editor implemented in the existing zone viewer, available
after an authorised own-library look. A scoped LibraryArrangementDraftService
reuses ChoiceSelectionService and ChoiceControlsComponent; edits remain local until
confirmation, Cancel sends nothing, and changed inspection/permission/owner/order
invalidates the draft. The editor opts into a memoized selected-order presentation
so visible card positions follow the draft. Default server-choice presentation is
unchanged. The initial component regression failed on the absent Arrange control.

Final client npm run verify passes with 1,849 tests, coverage and production build
(library-order-editor-verify-final.log); only the existing card-modal budget warning
remains. Four live seats on table 824e5148-8385-4e95-a769-cab862de3ad8 verified local
edit/cancel, exact-order confirmation, inspection expiry, private reinspection of
the authoritative order, and final reload (library-order-visual-live.log).
Desktop, 375x667 and 667x375 editor captures inspected:
tools/TableQa/library-arrange-live-four-editor-*.png. Overflow and reachable 44px
confirmation checks pass. The first harness run was corrected to collapse the
expanded inspection before clicking the covered library pile. No commit; broader
library acceptance and MTGO equivalence remain incomplete.

Client arrangement transport added through SignalRService, engineCommand and
LibraryActionsService. Submission snapshots the order array; its retry key includes
the exact sequence, so reversed orders cannot reuse each other's uncertain request.
The transport regression first failed with Invalid engine identifier: eager target
parsing treated the card-ID array as cast targets. Target parsing is now scoped to
CastSpell/ActivateAbility; arrangement has explicit field mapping. No caller-supplied
player ID is transported.
Ordering editor work remains open. ChoiceControlsComponent already delegates move
operations to a scoped ChoiceSelectionService; the editor should provide its own
scope so a local draft never replaces the board's server-choice selection.
Client npm run verify passes with 1,845 tests, coverage and production build
(library-arrange-transport-verify-final.log); existing card-modal SCSS warning only.
No visible arrangement control or live arrangement acceptance is claimed yet.

Atomic manual full-library arrangement command/API added. ManualLibraryArrange
requires an active own-library inspection and an exact permutation of current,
inspected card IDs; missing, duplicate, foreign and null lists are refused without
log changes. A newly added, uninspected card also forces renewed inspection.
ManualLibraryCoordinator reuses LibraryOrder.Arrange and ZoneReordered, preserving
objects and avoiding move/search/shuffle events. Existing request identities and
honour-system table checks apply. Tests verify ordering after session restart,
duplicate-request refusal and unchanged hidden objects. Full rules 1,790 / API 837
pass (library-manual-arrange-rules.log, library-manual-arrange-api.log).
Client arrangement controls and live acceptance are not implemented yet. No commit.

Four-seat manual search variant verified on table
51512843-1f99-444d-8beb-54d2c9d3d587: quantity two, Land + Basic + Forest filters,
battlefield destination, reveal enabled, enter tapped enabled, shuffle disabled.
All four projections showed exactly two new tapped Forests, a two-card public
reveal, unchanged hand counts and two fewer library cards. Selection survived
reload and required explicit confirmation. An authenticated QA-only count endpoint
verified one search, one reveal and zero shuffle events; it exposes no card data.
Desktop, landscape form, and both acting/opposing final board captures inspected.
Evidence: library-search-battlefield-live.log and
tools/TableQa/library-search-battlefield-live-four-*.png. QA host build passes;
no production changes in this pass. The last client verify remains 1,843 tests.
Remaining variants and broader library acceptance are still open; no commit.

Complementary default-search event check also passed with four live seats on table
2088946a-1edf-4468-b55a-e5488d73d5b9: exactly one search, one shuffle, no public
reveal, one land added privately to hand. See library-search-event-counts-live.log.

Manual Search library controls are now available inside the permitted own-library
viewer. The form exposes up-to count, destination, type/supertype/subtype filters,
reveal, shuffle, battlefield tapped state and library placement. It delegates to
LibraryActionsService; search and existing actions share one component submission
path. Component regression failed before implementation (missing submit control)
then passed. Client npm run verify passes: 1,843 tests, coverage and production
build (library-search-controls-verify.log); existing card-modal budget warning only.

Four live browser seats completed a Land-filtered search to hand on table
701a460e-aff5-4e5f-bab3-66eddf8519f5 (library-search-controls-live.log). The run
verified private inspection/options, reload during selection, explicit confirmation,
one-card library/hand change, hidden hand identity, inspection expiry, all-seat
broadcast and final reload. Desktop, 375x667 and 667x375 form captures were inspected,
including scrolled submit controls; document overflow and submit reach/44px height
checks pass. Captures: tools/TableQa/library-search-manual-live-four-*.png.
The isolated QA cards intentionally have no artwork. This is not live acceptance
of every destination/filter/replacement variant, physical devices or full MTGO
equivalence. Nothing committed.

Client search transport now has a typed LibrarySearchRequest, a SignalR method
and explicit DTO field mapping. LibraryActionsService uses its existing shared
busy/retry mechanism and binds the request identity to the search options.
The new transport test first failed with `Unsupported game command:
ManualLibrarySearch` (client library-search-transport-red.log). Retry coverage
checks equivalent requests, changed counts and serialization with other library
actions. No visible search control has been added yet; live acceptance remains
unfinished. Client npm run verify passed with 1,842 tests, coverage and production
build (client library-search-transport-verify.log). The existing card-modal SCSS
18.05 kB / 16 kB budget warning remains. No capture or live interaction claim is
made for this transport-only pass; nothing committed.

Manual library search API integration added through the existing discriminated
GameCommandDto route (`manualLibrarySearch`). The DTO takes explicit search
parameters, sets IsManual, and derives the acting player from authentication.
The hub/session integration test verifies private own-library options, persisted
restart and normal AnswerChoice completion, duplicate request refusal, and
enforced-table rejection at both entry points. Three invalid-input cases verify
negative count, stack destination and unknown placement leave the log, pending
choice and inspection unchanged. Full API 832 pass
(library-manual-search-api-route-full.log). Client controls and live multiplayer
acceptance remain unfinished; no client verification or commit in this pass.

ManualLibrarySearch now routes through ManualLibraryCoordinator and the shared
interpreter. Its explicit instructions include up-to count, filter, destination,
reveal, shuffle, tapped state and placement; source zone and owner remain the
acting player's library. It shares eligibility, request-ID logging and retry
refusal with the existing manual library commands. Five normal-command tests
cover destination zones, private ownership, serialized selection/answer replay
and refusal of the same request before and after completion.

Two further standalone-search tests exercise actual competing move and shuffle
replacements. Each answer uses a fresh engine and a rebuilt event log. Two-card
searches finish with the expected replacement life changes, exact zone counts,
one search event, one shuffle, expired inspection and no stack resolution. Both
viewers' projected prompts withhold the saved search instruction; the opponent
never receives private inspected cards. Full rules 1,789 and API 828 pass
(library-manual-search-command-rules.log, library-manual-search-command-api.log).
API DTO, client controls, live browser
acceptance and exact MTGO method equivalence remain unfinished. No commit.

Standalone search continuation implemented: source-less root Search instructions
persist their typed Search payload in server-owned choice context. The existing
interpreter reconstructs that instruction on answer and retains it across later
prompts; card-backed effect lookup is unchanged. ChoiceSecrets removes the saved
instruction from client views. Two tests resume serialized selection prompts using
a fresh interpreter, covering one selection and no selection, exact zone counts,
inspection expiry, one search event, one shuffle and no fabricated stack object.
Full rules 1,782 and API 828 pass (library-standalone-search-rules-final.log,
library-standalone-search-api-final.log).
Manual command/API/UI integration and replacement-prompt replay acceptance remain
unfinished. No catalog definitions added and no commit made.

Manual-search integration audit: calling IInterpreter.Run with a source-less Search
is not yet a complete implementation. EffectInterpreter.Ask persists the controller,
selection plans and remembered values, but Elsewhere resumes by resolving Tree;
Tree returns null when ChoiceContext.Source is absent. The first selection answer
would therefore be refused. This is source inspection, not a runtime acceptance test.

The implementation must first give server-created manual search instructions a
persisted, typed continuation identity and reconstruct their Search through the
existing interpreter. Reuse LibrarySearchPlanner, LibrarySearchCoordinator and the
existing replacement/movement/shuffle continuation. Do not add a synthetic catalog
card or accept arbitrary effect trees from the client. Retain authenticated ownership,
table permission and request-ID checks. Verify serialized restart at selection and
replacement prompts, single search/shuffle events, private inspection expiry and
four-seat projection before claiming manual-search acceptance. Search instruction
parameters (quantity, filter, destination, reveal and shuffle) must be explicit;
Look at library remains inspection, not a LibrarySearched event.

Removed duplicate mana-block identity validation from payment recovery. Checked
ManaChanged application now delegates to the same generic ManaPool fold used by
normal events; that owner validates negative blocks before pruning. Recovery keeps
only journal chronology and expected/restored pool checks. Full rules 1,780 / API
828 pass (library-mana-fold-owner-rules.log, library-mana-fold-owner-api.log).
No UI change, new browser acceptance or commit. Wider ownership audit remains open.

Final library-payment source lifetime verified through normal commands: four added
cases sacrifice the activation source before mill/library-exile replacement choices,
rebuild each choice from the log, complete payment and resolve after two priority
passes. Saved source context survives while the old object stays absent; exactly
one sacrifice and activation occur, and the ability resolves once. Cases include
one/two cards and revealed-top tracking. Full rules 1,780 pass
(library-source-sacrifice-payment-rules.log). Existing implementation needed no
production change. No browser acceptance or general earlier-pause claim; no commit.

Two-card retained-spending batches now serialize/rebuild before each replacement
answer, alternate replacement options and assert no recovery before batch completion.
Four added cases cover ordinary/restricted pools and one/two reversible payments.
Exactly two spends/shuffles survive; later failure produces one recovery, correct
refunds and unchanged library membership. Full rules 1,776 pass
(library-retained-spend-batch-rules.log). This validates interrupted batch completion
before a later cost fails, not failure halfway through the same cost or general
normal-command pause support. No production changes or commit in this pass.

Paused retained-spending recovery verified through the internal continuation seam:
four ordinary/restricted and one/two-payment cases serialize/rebuild the pending
replacement choice, answer it, then recover the later failed library cost. Combined
events replay with exactly one retained shuffle/spend and one recovery; pools,
library membership and priority match. Tests explicitly assert that the normal
ActivateAbility route still refuses these earlier pauses. Full rules 1,772 pass
(library-retained-spend-paused-rules.log). No production gate was broadened, no
catalog definitions added and no commit made.

Normal-command retained-spending recovery now has four engine-generated cases:
ActivateAbility pays one/two ordinary costs, a mill replacement pays mana and
shuffles, and a later mill fails. Recovery retains that replacement's spending,
refunds the other payments, preserves the library, creates no activation stack
object and restores priority. Ordinary/restricted mana and exact source amounts
survive event replay. Full rules 1,768 pass
(library-retained-spend-activation-rules.log). Definitions are test-only fixtures.
These synchronous failures do not establish earlier replacement-choice pauses,
mana-ability reversal or live-browser recovery acceptance. No commit.

Retained-spending journal integration passes for ordinary/restricted mana, each
with and without a later reversible mana payment. Serialized ActivationPayment
classifies the shuffle/spend step as retained and the other payments as reversible.
PaymentRecovery.Apply and event replay preserve the exact retained spend/source
amounts, refund other payments, restore priority and emit no extra ManaChanged.
Full rules 1,764 pass (library-mana-spending-replay-rules.log). These are constructed
journal integrations, not normal earlier-pause activation or browser acceptance.
No production code or UI changed in this follow-up; no commit.

Validated retained mana spending added to the shared recovery fold. Recorded
negative deltas preserve exact source/key/color and restriction/color amounts;
underflow is rejected before the shared fold discards exhausted blocks. Both
ordinary and restricted spending cases pass with corrupt provenance and pool
overdraw refusal. Rules 1,762 / API 828 pass (library-mana-spending-rules-final.log,
library-mana-spending-api.log); the initial ordinary case reproduced refusal.
Full payment-journal integration for spending, mana-ability dependency reversal
and general earlier activation pauses remain incomplete. No client changes or commit.

Retained emptying now has full PaymentRecovery.Apply/event-replay coverage in both
chronology cases. A serialized activation journal classifies the mana payment as
reversible and the shuffle/emptying step as retained. Recovery emits one restoration,
does not add a mana-production occurrence, preserves the expected player pools and
library/RNG/choice state after event replay, and restores priority with no pending
choice. Rules 1,760 pass (library-mana-empty-replay-rules-final.log). This is a
constructed journal integration test, not a normal activation or browser claim.
No production code changed in this follow-up.

Retained mana-pool emptying supported by the recovery fold. PaymentManaRecovery
preserves the chronology of production and emptying when checking/reconstructing
pools; spending remains unsupported. CostPaymentStep records emptying even when
the pool was already empty. Two chronology cases reproduced refusal before the
change; three focused regressions now cover serialization, missing journal events,
unexpected final state and no revival of refunded mana. Rules 1,760 / API 828 pass
(library-mana-empty-rules-final.log, library-mana-empty-api.log).
This is recovery-fold capability, not end-to-end general activation support.
No client changes, browser acceptance claim, production card additions or commit.

Payment source audit reconciled with current implementation: descriptor ordering
and CostObjects selection already exist in LibraryPaymentService; earlier notes
claiming they are absent are historical. Normal activation pauses are restricted
to final mill/library-exile steps with no unpaid remainder. PaymentManaRecovery
explicitly rejects retained spending/emptying; its production-only fold and exact
pool checks remain intact. See the new current checkpoint in
research/LIBRARY_PAYMENT_RULES_AUDIT.md for the concrete remaining dependency work.
This pass changes documentation only and makes no new runtime-completion claim.

Idle-mode guard now verified in four live browsers on table
0e60b43b-830e-4c5f-9627-6d740f62c8f6. While a Cinder Spark cast draft is open,
the library inspection menu omits both reposition actions. Cancelling the draft
does not advance any server revision; top placement becomes available again and
passes expiry, broadcasts and reload. Evidence: library-reposition-mode-guard-live.log
and inspected tools/TableQa/library-reposition-during-cast.png. This closes the
specific browser-verification gap recorded below. No production change this pass.

Reposition eligibility now includes the board's existing idle interaction mode,
matching the own-library modal policy. The component regression changes the mode
while the menu is open and verifies actions disappear and no command is emitted.
Full client verify passes 1,840 tests, coverage and production build
(library-reposition-idle-verify.log). Browser verification of this specific mode
transition remains unverified; previous touch/menu captures are not new evidence
for it. The acceptance ledger's manual-control/UI rows now reflect current work.

Existing surveil/detail-expiry live regression rerun passed after the tap-menu
change: table 584594db-8666-4147-a4b2-ecab4e1e7e35, existing Desert/Arid Archway
decks. Private inspection survives refresh; Card details opens through the menu;
the authoritative answer removes active inspection/detail and both seats agree on
the public graveyard card. Direct choice-push privacy assertions also pass.
Evidence: sibling client library-surveil-menu-expiry-live.log and inspected
e2e/screenshots/library-surveil-{active-detail,completed-landscape}.png.
No new card definitions, production changes or commit in this follow-up.

Touch-accessible inspection: tapping/clicking an actively inspected card now opens
the existing action menu; Card details remains its first action. Historical cards
still open details directly. The active-detail expiry regression follows the menu
route, and the surveil detail-expiry harness was updated (syntax checked, not rerun).
Full client verify passes 1,840 tests, coverage and build (library-touch-menu-verify.log).
Four-seat Chrome touch-input emulation passed on table
877a627d-cf82-4cb7-8bbd-3835397fe83a: tap card, tap bottom placement, inspection
expiry, broadcasts and reload. Log: library-reposition-touch-live.log. Landscape
capture inspected: tools/TableQa/library-reposition-bottom-touch-menu-landscape.png.
Physical Android/iOS behavior remains unverified; no physical-device claim is made.

Bottom-menu browser follow-up passed on four-seat table
8215372f-bfd3-4768-873a-debd6f046539 using --reposition --bottom --four.
The menu action, inspection expiry, all-seat broadcasts and final reload passed;
landscape capture inspected. Evidence: library-reposition-bottom-live.log and
tools/TableQa/library-reposition-bottom-menu-{desktop,landscape}.png. API tests
remain the exact top/bottom order evidence. Native touch invocation is still
unverified; this harness dispatches contextmenu. No production changes in this pass.

Manual reposition menu now offers Put on top / Put on bottom only for an active
own-library manual look with server permission and no current choice. It delegates
through GameBoardComponent to LibraryActionsService; CardOptionsMenuComponent owns
the shared menu rendering and keyboard handling. Stale inspection, other owners and
pending submissions cannot emit reposition. Client verify passes 1,840 tests,
coverage and production build (library-reposition-menu-verify.log in client).
Four-seat table a442e5ac-2b80-44b4-8e74-a9ae5f20ca08 passes top-menu submission,
inspection expiry, broadcasts and reload (library-reposition-live.log). Desktop and
667x375 menu captures inspected: tools/TableQa/library-reposition-menu-*.png.
This run dispatches contextmenu programmatically; it does not prove native touch
invocation. Bottom ordering has API/component coverage but not a separate live run.
No commit. Broader manual search/order and full MTGO parity remain incomplete.

Client reposition transport/service wired through SignalRService and engineCommand.
LibraryActionsService now shares a single execution/retry owner for parameterless
actions and repositioning. Retry keys include card ID and destination; regression
coverage verifies distinct requests for different targets, preservation after an
uncertain failure, and the shared busy guard. The DTO mapping carries no player ID.
The inspection menu and browser acceptance remain outstanding.
Full npm run verify passed: 1,839 tests, changed-file coverage and production build
(sibling client library-reposition-transport-verify.log). Existing 18.05 KB
card-modal stylesheet budget warning remains. No UI/capture completion claim.

Library-scoped reposition command added: ManualLibraryRepositionDto carries request
ID, card ID and position; actor comes from authentication. ManualLibraryCoordinator
requires a current own-library manual inspection containing that card, then delegates
to LibraryOrder.Place and ZoneReordered. Table permission and request replay guards
are shared with draw/shuffle/look. Generic ManualMove remains subject to its existing
per-card enforcement; the new library action is an explicit honour-system operation.
API 828 / rules 1,757 pass (library-reposition-command-{api,rules}.log), including
top/bottom ordering, missing inspection, opponent card refusal, JSON transport,
enforced-table rejection, inspection expiry and persisted retry refusal.
Client menu wiring, browser captures and client verification remain outstanding.

Manual repositioning audit verified the existing shared LibraryOrder.Place route.
Five existing position/replay regressions now check manual inspection lifetime too:
changed order expires it, no-op placement preserves it, and independent effect
inspection remains intact. Rules 1,757 pass (library-reposition-inspection-rules.log).
The inspection panel still lacks the manual reposition control; engine support is
not being counted as completed UI or MTGO parity.

Simultaneous inspection audit: two API regressions now verify both players can
inspect independently, drawing removes the moved identity from the owner's library
inspection, and stopping/shuffling one library preserves the other player's exact
inspected identities after replay. Full API suite: 826 passed
(library-look-independent-api-final.log). Production behavior needed no change.

Manual library look/stop-looking now uses the existing inspection events and private
projection. These are own-library honour-system controls; enforced tables reject both.
Inspection survives replay and ends on explicit stop or actual reorder/shuffle.
Collapsing the card list does not end inspection. No search event or card movement is
introduced. API 824, rules 1,757 and npm run verify (1,837 tests, coverage gate and
production build) pass. The existing card-modal stylesheet budget warning remains.
Final four-seat live table: 30eb0609-5f42-4a48-98db-95bb85c33404; owner-only look,
active reload, stop, all-seat broadcasts and final reload passed. Desktop/landscape
captures were inspected: tools/TableQa/library-look-live-four-*.png. Evidence:
library-manual-look-{api,rules,live-final}.log and the sibling client's
library-manual-look-verify-final.log. Synthetic fixture cards have no artwork.
Manual search/order controls and the wider acceptance/parity audit remain incomplete.
No commit made.

Shared arrangement invariant: LibraryOrder.Arrange now emits each current card once
even when top/bottom inputs repeat a reference. It preserves first-occurrence order,
ignores stale references and retains the relative order of untouched cards. The
focused invariant regression reproduced duplicate entries before the change
(library-order-invariant-red.log). This protects all shared arrangement callers;
search still separately owns uniqueness of its result set for non-ordering consumers.
Rules 1,757 / API 820 pass (library-order-invariant-rules.log,
library-order-invariant-api.log). No UI change, new browser acceptance or commit.

Shared-search result uniqueness: two players selecting the same card produced
duplicate references in the combined remembered result. A following top/bottom
Reorder turned a two-card library into three entries; both serialized engine
regressions reproduced it (library-order-unique-red.log). LibrarySearchCoordinator
now combines arrived cards once per identity, preserving first occurrence order.
The existing eight shared-search identity/replacement cases also assert unique
remembered results. Full rules 1,756 / API 820 pass:
library-shared-search-unique-rules.log, library-shared-search-unique-api.log.
No client source changes, new browser acceptance, catalog additions or commit.

Four-seat live manual-library validation passed: draw table
ad62faee-96d4-4058-a866-b24b1da25b7e and shuffle table
732186dc-8fc5-4744-b927-5bb358435c16. Both flows refresh while pending, answer real
replacement choices, synchronize all four browsers, and reload the completed view.
All three opponents receive draw counts without the drawn-card identity. The harness
now validates the four-player summary rail rather than assuming the two-player layout.
Inspected draw and shuffle phone-landscape prompts and the fourth seat's completed
draw view. Logs: library-four-draw-live-final.log, library-four-shuffle-live.log.
Captures: tools/TableQa/library-{draw,shuffle}-live-four-*.png. Synthetic isolated
fixtures; no client source changes, new npm run or commit. Broader acceptance remains open.

Live draw replacement acceptance passed on isolated two-seat table
54ad791c-1347-415a-86ef-e19228698f44. The own-library Draw control opens a real
replacement choice, refresh preserves it, and answering draws exactly one card.
Actor hand 5->6 / library 57->56; both seats retain 40 life. The opponent receives
the new hand count but no drawn-card identity anywhere in its projected object tree.
Final reload matches the actor's broadcast view. Log: library-draw-live.log.
Inspected tools/TableQa/library-draw-live-pending-landscape.png and both seat captures.
This uses synthetic QA definitions; no production card or client source changes,
new npm run or commit. Full library acceptance remains open.

Nested draw replacement follow-up: four bounded one/two-source, empty/nonempty
library regressions reproduced self-reapplication after a replacement-created draw
(library-nested-draw-red.log). The draw seam now forwards the existing used set and
PendingDrawBatch preserves inherited history through serialized deferred batches.
A fifth regression checks two resumed draws and private continuation projection.

The first full suite caught history leaking from a nested replacement into the next
independent draw (two existing disclosure regressions failed in
library-nested-draw-rules.log). ProposeThrough now keeps nested used keys in the inner
continuation and writes only the parent's inherited set into its outer context.
All 1,754 rules and 820 API tests pass (library-nested-draw-rules-final.log,
library-nested-draw-api.log). No new UI/browser verification, catalog definitions or
commit. Library acceptance remains incomplete.

Two-browser shuffle replacement acceptance passed on isolated table
c3ed8e30-87c1-4629-a0ee-7506afadc5a2. The existing own-library modal starts a manual
shuffle, then refresh preserves the real replacement choice; answering synchronizes
both seats and a final reload preserves the completed view. Life and library counts
remain unchanged. Inspected desktop and 667x375 pending-choice captures:
tools/TableQa/library-shuffle-live-pending.png and -pending-landscape.png.
Evidence: library-shuffle-live-final.log. This uses synthetic QA definitions, not
catalog-backed Commander acceptance. The first attempt correctly lacked manual
controls because the fixture disabled honour-system actions; only the shuffle fixture
now enables that setting. No client source changes, new npm verification or commit.

Nested shuffle replacement follow-up: direct and search-created shuffles dropped
the interpreter's used-replacement set, allowing their source to apply again. Two
bounded regressions reproduced this (library-nested-shuffle-red.log). The shuffle
seam now carries that set through ZoneSystem and LibraryShuffleCoordinator. Two-source
tests then exposed history loss when entering the next replacement effect
(library-nested-shuffle-chain-red.log); replacement application now seeds the full
set into the existing EffectContext for instead/choosing effects and sequence steps.

Four cases cover one/two sources, direct/search-generated shuffle and serialized
choice reloads, asserting each source applies once and exactly one shuffle completes.
Rules 1,749 / API 820 pass (library-nested-shuffle-rules.log,
library-nested-shuffle-api.log). No UI changes, new browser acceptance, catalog
additions or commit. Broader library requirements remain incomplete.

Shuffle replacement integration: the shared zone shuffle previously bypassed the
replacement system entirely (four interrupted-search cases failed to pause in
library-shared-search-pause.log). LibraryShuffleCoordinator now offers an uncommitted
shuffle before consuming randomness and commits only after replacement decisions.
Its completion path is shared by immediate execution and replacement resumption;
it is the sole production Rng.Shuffle caller. Pending proposals contain no randomized
order, and the existing projected-choice boundary strips the proposed event.

Enabling the pause exposed two continuation defects: shared search re-recorded old
within-library placement IDs on resume, and ordinary Shuffle effects ran following
instructions while waiting (library-shuffle-effect-red.log). Placement progress is
now recorded once, and ordinary shuffle uses the existing proposal ordinal mechanism.
Eight search identity variants include replacement pauses and event-log reloads;
additional tests cover replaced empty/singleton/multi-card shuffles, unchanged RNG,
hidden proposals and following-effect sequencing. Rules 1,745 / API 820 pass:
library-shuffle-replacement-rules-final.log, library-shuffle-replacement-api.log.
No new browser acceptance, catalog additions or commit. Nested replacement effects
and broader library acceptance still require auditing; this is not full completion.

Shared-library search identity fix: when two searchers selected the same revealed
card for library placement, only the first participant's saved result followed the
shuffle identity reset. Final zone counts passed, but checking the serialized
remembered results reproduced two stale-reference failures
(library-shared-search-identity-reference.log). Search now refreshes bookkeeping
and inspection scopes for all participants in that search operation through the
existing identity mapper. Other operations retain their own scope.

Four top/bottom, distinct/overlapping selection cases replay each decision and
verify saved references before a following effect consumes the results. Full rules
1,737 and API 820 pass (library-shared-search-identity-rules.log and
library-shared-search-identity-api.log). No UI change, new browser acceptance,
catalog addition or commit. Full library acceptance remains incomplete.

Serialized activation scenarios now preserve explicit library payment order through
the existing command adapter. Two regressions first failed when the adapter dropped
the field; they now verify selected exile before mill and refusal of an invalid order.
Validation remains in the shared payment engine. Scenario and embedded card-file
schemas expose the optional paymentOrder field; regeneration also retains the current
Search Top/LibraryOf vocabulary in definition schemas.

Verification: 1,733 rules, 326 card/schema and 820 API tests pass. Logs:
library-scenario-payment-order-rules.log, library-scenario-payment-cards.log and
library-scenario-payment-api-host.log. The initial API shell stalled without an active
test runner; it was stopped before the successful host-permission run. No client
source changes, new UI validation, catalog additions or commit in this increment.

Final library-movement continuation now includes ExileFromZoneCost from Library as
well as MillCost, only when no later costs remain. Four selected-exile regressions
reproduced refusal (library-final-exile-pause-red.log); normal-command replay now
preserves reversed selected IDs, leaves the unselected library card in place and
charges life exactly once. Hidden/revealed and one-/two-card variants pass. Two-/four-seat
API cases reload between replacement answers and verify original selected identities.
Earlier library-cost pauses retain their recovery gate.

Verification: 1,731 rules / 820 API tests pass (library-final-exile-pause-rules.log,
library-final-exile-pause-api.log). Two live browsers passed combined selection and
replacement on isolated table befd0864-d012-4fa3-a1c1-c00e0138988a, including cancel,
private top-card eligibility, refresh while pending, completed counts and final reload.
Log: library-final-exile-pause-live.log. Inspected combined-flow captures under
tools/TableQa/library-selection-replacement-live-*, including pending-landscape and
opponent final view. No client source change or new npm run in this increment; the
preceding 1,835-test full verification remains current. No catalog changes or commit.
General recovery, hidden-card selection and the full library acceptance remain open.

Replacement fallback labels no longer expose internal event type names. Supplied
ability text is preserved; otherwise draw/mill/shuffle/movement get readable action
descriptions. Public current sources add controller, effective card name and ability
number. Hidden sources, face-down sources and hypothetical entry-probe objects do
not gain a source name through this fallback. Five new regressions cover hidden and
public sources, supplied text and an unrevealed library card's hypothetical entry.

The first live capture exposed excessive wrapping in the shared card-tile layout.
Replacement-only choices now use wider text rows with 12px text; other choice kinds
retain their layout. Final desktop and 667x375 captures show all four options at once.
Two-browser refresh/answer/broadcast/reload passed on isolated table
6dbb6269-3dd6-4ca1-a6c7-fe4deeac4768 (library-replacement-label-live-final.log).
Inspected tools/TableQa/library-replacement-live-pending.png and
library-replacement-live-pending-landscape.png. These files supersede older captures
at the same paths. No claim of complete replacement-effect descriptions or MTGO parity.

Verification: 1,727 rules / 818 API / 1,835 client tests pass. Logs:
library-replacement-label-rules-complete.log, library-replacement-label-api.log,
mtg-client/library-replacement-label-client-verify.log. Full npm verify includes
coverage and production build; existing card-modal budget warning remains.
Nothing committed. General payment recovery and broader library acceptance remain open.

Normal non-mana activation now permits a replacement pause during its final MillCost
when RemainingCostPayment.Costs is empty. Earlier pauses and other cost kinds retain
the gate; this is not general payment recovery. The interrupted batch and activation
use the existing serialized continuation. Four normal-command regressions (one/two
cards, hidden/revealed library) reproduced refusal in library-final-payment-pause-red-2.log
and now pass, verifying one life charge, one activation, redacted continuation and
replay at every answer. The earlier refusal fixture now includes a genuinely unpaid
library cost; its old written-order life cost was actually paid before the mill.

Verification: 1,722 rules / 818 API pass (library-final-payment-pause-rules.log,
library-final-payment-pause-api.log). API cases use normal hub commands with two/four
seats and reload the saved session between answers. Two live browsers passed on
isolated table daa98e8d-97c8-4661-9988-925f98d9e54e, including browser refresh during
replacement choice, no completed activation while pending, final library 55 from 57,
life 39, and one stack ability. Log: library-final-payment-pause-live-final.log.
Inspected tools/TableQa/library-replacement-live-{pending,seat-0,seat-1}.png. The
prompt currently displays internal replacement event names; readable labels remain
unfinished. No client code changed, so the preceding full npm gate remains the latest.
No catalog definitions changed; no commit made. General replacement-payment support,
hidden selections and broader library acceptance remain incomplete.

Visible-card payment picker is implemented. LibraryPaymentService binds selections
by expanded payment index, enforces exact required counts, and serializes CostObjects
using written-order offsets regardless of reordered payments. Only server-eligible
IDs present in the current client snapshot receive card buttons. Cancel/replacement
snapshot/disconnection clears the decision. Single-cost selections now prompt too.
Insufficient visible candidates keep confirmation disabled with an explanation.

Live testing caught a missing preparation offer: LegalActions omitted library
abilities whose initial check needed object selections, making the picker unreachable.
Such offers now carry RequiresPaymentSelection after announcement validation and
candidate-count checks; execution still rejects missing/invalid selections. Regression
proves an offer can begin without accepting an unselected activation.

Two real browser seats passed selected-card cancellation, confirmation, broadcasts,
and reload on isolated table 9ddd8279-b596-4e0e-a9ca-0db147a82f0f. Only the privately
lookable top card was offered; the opponent did not receive that look. Both boards
then showed life 39, library 56 and one additional exiled card. Evidence:
library-payment-picker-live-final.log; reviewed tools/TableQa/library-selection-live-
{prompt,landscape,portrait,seat-1}.png. Landscape fits with 44px controls; portrait
retains the rotation notice. Synthetic fixture, not catalog-backed deck acceptance.

Verification: 1,718 rules, 816 API and 1,835 client tests pass. Full npm run verify
passed in mtg-client/library-payment-picker-verify-final.log (coverage/build included,
existing card-modal 18.05kB budget warning only). Backend logs:
library-payment-picker-rules.log / library-payment-picker-api.log. No commit made.
Hidden selections without visibility, alternative-cost prompting, normal replacement
payment pauses and broader library acceptance remain unfinished.

Activation previews now return SelectableObjects restricted to the actor's current
projected objects. CostChoice carries its expanded payment index, preventing
non-object costs or reordering from shifting candidate bindings. GameSession uses
one immutable snapshot for validation, candidate discovery and visibility. Shared
VisibleGameObjects replaces the artwork service's duplicate current-object traversal;
historical reveals remain excluded from selectable current identities. Hidden library
cards and hidden face-down definitions are not returned. Candidate IDs are sorted
by visible identity, not undisclosed library position. Existing visibility permits
only the looked-at top card when that is all the player can see. No look permission
is created by this read-only endpoint. Client picker integration remains unfinished.
Verification: 1,717 rules / 816 API tests pass (library-payment-visible-rules-final.log,
library-payment-visible-api-final.log), including private/public top access, revoked
permission, hidden candidates, offset/index matching and the actual HTTP controller.
No new client verification in this backend increment. Nothing committed.


Payment descriptors now carry ObjectCount and ObjectOffset for the selected-object
slots in expanded written cost order. CostPaymentOrder computes these alongside
the execution binding, so reordering payments preserves the selection positions.
Counts describe required selections, never the size of a hidden candidate set;
no identities or eligibility lists are exposed. Regression covers library exile
of two cards plus discard/mill, explicit reordering, empty selection preview and
unchanged library state. The selection UI and its authorized disclosure workflow
remain unfinished; this supplies shared command metadata, not a completed picker.
Verification: 1,715 rules tests and 814 API tests pass in
library-payment-selection-rules.log and library-payment-selection-api.log.
No client changes or new client verification in this increment. Nothing committed.

Two real headless browser seats passed the isolated library-payment flow on table
d8f640ec-0399-4704-9999-b70cf220d7fd. Cancellation preserved the full HTTP snapshot;
the prompt reordered Mill 2 before Mill 1; confirmation paid one life and milled three
cards, with both SignalR views and browser life displays updated. Reload retained
the paid snapshot. Evidence: library-payment-qa-browser-final.log and inspected
tools/TableQa/library-payment-live-{prompt,seat-0,seat-1}.png captures. The fixture
uses real controller/hub/engine paths and synthetic definitions, not catalog-backed
Commander deck acceptance. Aggregate counts do not independently prove the two mill
events' internal order; that remains covered by existing engine ordering tests.
Cost-card selection and normal replacement-pause activation remain unfinished.
Full npm run verify passed after the live run: 1,832 client tests, coverage for 35
changed files, and production build (mtg-client/library-payment-live-verify.log).
No production code changed in this follow-up; no commit made.

Isolated live payment fixture now runs in tools/TableQa with --libraryPayment=true
--port=4340, two seats and in-memory storage. Its existing deck adapter was updated
to StartingCard so the QA host builds again. Build passed in
library-payment-qa-build-final.log. The production payment controller returns indices
1,0,2 without changing the snapshot and refuses the opposing seat (HTTP 400).
Descriptors captured in library-payment-qa-preview.json. Real browser confirmation,
SignalR execution and two-browser synchronization are still unverified; this is
fixture preparation and HTTP evidence, not completed multiplayer acceptance.
No catalog definitions or production behavior changed. No commit made.

Live catalog audit found no executable MillCost or library ExileFromZoneCost
definition; existing exile costs use Graveyard/Battlefield/Hand. No catalog cards
were implemented to manufacture a live test. An isolated synthetic live fixture
is still needed for that acceptance path. Eight projection/privacy cases and a
client adapter regression verify the library-payment marker: 1,714 rules tests
(library-payment-marker-rules.log) and 22 targeted client transport tests
(mtg-client/library-payment-marker-client.log) pass. Test-only follow-up; no claim
of a new full npm/API run or completed live multiplayer payment acceptance.

Board payment-order control is implemented for server-marked library activations.
The ability projection marks nested mill/library-exile costs; the transport fetches
server descriptors only for those abilities. LibraryPaymentService owns one local
pending order, preserving descriptor indices and group boundaries. Confirmation
sends PaymentOrder through the existing Command path; cancellation sends nothing.
New snapshots, disconnect/close and reconnect initiation cancel pending decisions;
late previews are discarded if the snapshot, game or connection changed.

Headless fixture captures passed in mtg-client/library-payment-ui-captures-final.log.
Reviewed desktop and 667x375 landscape panels: labels fit, controls are at least
44px, no document overflow. 375x667 retains the rotate-phone notice. Images are
mtg-client/e2e/screenshots/library-payment-{desktop,landscape,portrait}.png.
These are controlled board fixtures, not live multiplayer acceptance. Cost-card
selection UI and live ordering validation remain unfinished. Backend suites pass
1,706 rules / 814 API (library-payment-ui-rules.log, library-payment-ui-api.log).
Final npm run verify passes: 1,831 client tests, changed-file coverage and production
build (mtg-client/library-payment-ui-verify-final.log). Existing card-modal SCSS
budget warning remains 18.05kB / 16kB. Full site-wide verify:ui was not run; scoped
payment captures and the existing portrait guard were inspected. Nothing committed.

Read-only activation payment descriptors are now available at POST
/api/games/{id}/activation-payments. The authenticated seat requests an ability;
the engine shares source/priority/timing validation with execution and returns
expanded indices, public labels and payment groups. No selected candidates or
library identities are returned, and no payment is executed. CostPaymentOrder
owns descriptor indices/grouping; labels do not evaluate hidden state. Rules:
1,706 passing (library-payment-descriptors-rules-final.log). Client order controls
and normal activation replacement prompts remain unfinished.
All 814 API tests pass (library-payment-descriptors-api.log), including two-/four-seat
read-only preview, privacy and authorization checks.

Payment-choice discovery now shares recursive expansion with execution. Nested
library alternatives previously returned no object questions (reproduced in
library-choice-expansion-red.log). The separate one-level expansion loop is gone;
command selections and cursor advancement now reach automatic alternative probes.
The board still lacks command-specific payment descriptors and order controls.
This prerequisite fix is not a claim that the player-facing order UI is complete.
Verification: 1,705 rules / 814 API tests pass (library-choice-expansion-rules-final.log,
library-choice-expansion-api.log), including nested alternatives and automatic
alternative discovery matching execution with selected objects.

Explicit activation payment order now reaches the shared ordering class through
ActivateAbility and ActivateAbilityDto, together with selected cost objects.
Indices refer to expanded costs before grouping; bound selections move with costs.
Invalid permutations or library-before-ordinary orders refuse before execution.
Default grouping is unchanged. One normal-command case reproduced ignored order
(library-explicit-order-red.log); six new cases and an explicit-order saved pause
pass in the 1,703-test rules suite (library-explicit-order-rules-final.log).
All 814 API tests pass (library-explicit-order-api.log), including DTO mapping and
two-/four-seat rejection of an invalid order without persistence or view broadcasts.
This is backend support only: client discovery/selection and general activation
pauses remain unfinished. No UI verification or full player-facing parity claim.

Saved-payment compatibility verified: the replacement-pause replay test now also
removes ObjectSelections from serialized JSON and resumes through the legacy
path. A flat legacy queue with library exile before discard keeps both named
objects attached during regrouping. Two malformed binding-count cases refuse
under 601.2c. All 1,697 rules tests pass
(library-payment-binding-compatibility-rules.log). Test-only follow-up; production
code and the prior 813-test API baseline are unchanged. Explicit player-selected
ordering and general activation pauses remain unfinished.

Selected library payments now bind objects before ordering. CostPaymentOrder owns
expansion/binding/grouping; ObjectCosts owns selection counts. Per-cost bindings
survive RemainingCostPayment serialization and resume, preventing an empty or
partial selection from borrowing another cost's objects. Five new cases cover
exile/discard order, alternatives, partial selections and replay. Both initial
order cases failed before the change (library-selected-order-red.log). Rules:
1,693 passing (library-selected-order-rules-final.log); all 813 API tests pass
(library-selected-order-api.log). Explicit player ordering,
omitted-field compatibility verification and normal activation pauses remain open.

Mill payment ordering now runs through the shared CostPayment.Begin/Resume path:
expand alternatives, preserve relative order, then defer mill until ordinary
payments finish. Four new regressions include normal-command event ordering,
nested alternatives, and no library events on an unpaid mana cost. The original
two cases failed before the change (library-payment-order-red.log). Updated
recovery/continuation fixtures follow the corrected sequence. Rules suite:
1,688 passing (library-payment-order-rules-final.log); API suite: 813 passing
(library-payment-order-api.log). Selected library exile
needs bound object selections before reordering; full player-selected ordering
and random-cost handling remain unfinished. No normal-pause gate was opened.

Rules audit found a prerequisite defect: CostPayment.Begin pays written order,
where CR 601.2h/602.2b require ordinary costs before random/library-to-public-zone
costs. See research/LIBRARY_PAYMENT_RULES_AUDIT.md for source evidence, affected
fixture classification, and requirements for alternatives, chosen objects and
resume. The latest mill-before-sacrifice fixture proves recovery mechanics but
does not prove a legal activation sequence. Fix payment ordering before expanding
retained dependencies. Audit only: no runtime edits or new test results.

Movement conflict checks now consider only later retained steps. Earlier retained
property effects are already in the departure snapshot and survive its restoration.
Four normal-command/replay cases cover retained shuffle plus tap/counters, followed
by sacrifice and failed mill, with ordinary and commander sources. All four
previously refused (library-prior-retained-movement-before.log). Full suites:
1,684 rules / 813 API (library-prior-retained-movement-rules.log,
library-prior-retained-movement-api.log). Later retained movement dependencies and
general activation pauses remain unfinished.

Recovery now serializes ordered PaymentRestoration stages and simulates each stage
before validating the preceding payment. Two replacement pauses demonstrate the
dependency: the first sacrifices/exiles the source, the next puts counters on its
arrival, then mill fails. Both previously refused (library-recovery-order-before.log)
and now restore the later counters before reversing the earlier movement through
replay. Legacy recovery fields remain readable and use the same application code;
a test removes the new stages field from serialized JSON and verifies old property
restoration. Full suites: 1,680 rules / 813 API
(library-recovery-order-rules-final.log, library-recovery-order-api.log).
Retained dependencies and normal activation-pause integration remain unfinished.

Mixed property/movement replacements now validate moved-object changes on each
side of the ObjectMoved event. Shared property validation checks original versus
departed snapshots and arrival versus final snapshots; event ordering prevents
pre-departure properties being attributed to an already-departed identity. The
movement restoration carries original/final identities, while a residual property
step restores players and objects that did not move. Four sacrifice/exile cases
cover counter changes before/after movement plus life, mana and another permanent's
tap state (red: library-mixed-movement-before.log). Full suites: 1,677 rules /
813 API (library-mixed-movement-rules.log, library-mixed-movement-api.log).
Retained movement dependencies and normal activation pauses remain unfinished.

Movement-only outcomes replacing mill now use the same batch restoration as
printed movement costs. PaymentMovementRecovery validates recorded departure
verbs and legal origin/destination pairs instead of inferring the action from
the cost type; identity, ordered zones, history and commander candidates retain
their existing checks. Four paused replay cases cover sacrifice/exile of ordinary
and commander sources. All four previously refused
(library-replaced-movement-before.log). Full suites: 1,673 rules / 813 API
(library-replaced-movement-rules.log, library-replaced-movement-api.log).
Mixed property/movement steps, dependencies and normal activation pauses remain
unfinished; this does not establish generic movement reversal.

Payment journals now retain snapshots for entities touched by property events even
when final values are unchanged. This permits validation/recovery of gain/loss,
tap/untap and add/remove-counter sequences and preserves their trigger provenance.
All three previously refused (library-net-zero-before.log). A second regression
found incorrect final tap state when a retained shuffle sequence returned to its
starting tap value; recovery now uses the last retained tap event, not net snapshot
change (library-net-zero-retained-before.log). Both directions pass through normal
commands and replay. Full suites: 1,669 rules / 813 API
(library-net-zero-rules-final.log, library-net-zero-api.log). General movement
dependencies and activation-pause integration remain unfinished.

Mixed property replacement recovery now validates supported events together.
PaymentPropertyValidation folds life, mana, tap and counter events onto recorded
entity snapshots and requires complete snapshot equality. It replaces the separate
property classifiers; counter/mana folds are shared with ordinary event application.
The property accumulator restores every affected counter kind and tap state, while
mana recovery is selected from events rather than the printed cost. Both orders of
a life/tap/counter/restricted-mana replacement sequence now replay back to the
original state after failed mill (red: library-mixed-properties-before.log).
Guards reject missing events, unrelated fields, pool emptying and counter overdrafts.
Full suites: 1,664 rules / 813 API (library-mixed-properties-rules-final.log,
library-mixed-properties-api.log). Mixed movements, retained dependencies and
normal activation pauses remain unfinished.

Property-only outcomes replacing mill now use shared tap/counter restoration.
Classification uses actual events and exact snapshots rather than requiring a
printed tap or counter-removal cost; counter restoration reads the validated
event kind. Four paused-command/replay cases cover tap, untap, adding/removing
charge counters and preservation of storage counters. All four reproduced refusal
(library-replaced-properties-red.log). Full suites pass 1,661 rules / 813 API
(library-replaced-properties-rules.log, library-replaced-properties-api.log).
General mixed-effect recovery and normal activation pauses remain incomplete.

Recovery now recognizes life-only replacement outcomes by their events and exact
player-state deltas rather than requiring a printed PayLife cost. A paused mill
can be replaced by either of two life gains, followed by an unpayable mill; both
branches restore original life/library state through replay and cancel triggers
from reversed gain/loss occurrences. No new gain/loss event is emitted by recovery.
The regression reproduced refusal (library-replaced-life-before.log). Full suites
pass 1,657 rules / 813 API (library-replaced-life-rules.log,
library-replaced-life-api.log). Normal activation pauses remain gated; this is one
additional supported recovery shape, not general replacement recovery.

The active inspector now marks selected search cards with an outline and explicit
text. Its picks input comes from the board's existing ChoiceSelectionService;
there is no second selection store or network path. The component regression
checks markers through multi-selection and reset. Four-seat live table
144cdcd5-3144-49e1-9344-2b97b6377fa7 passed, including two inspector markers after
deselection/reselection and successful confirmation. Reviewed
library-multi-search-four-inspection-selected.png. Client npm run verify passed
(library-inspector-selection-verify.log); live log is library-inspector-selection-live.log.

Library inspector layout now uses an expanded overlay and responsive card grid,
preserving the separate inspection and confirmation responsibilities. Four-seat
search and two-seat look/reconnect/mobile acceptance passed. The collapsed desktop
summary now wraps long names; live width and full-name assertions pass, and the
selected capture was reviewed without its former horizontal scrollbar. Final
npm run verify: 1,822 tests and production build passed
(client library-inspector-label-verify.log). Evidence is in
research/LIBRARY_ACCEPTANCE_AUDIT.md.

Live Commander search acceptance passed with four browser seats, a singleton
Blighted Woodland and a commander excluded from each 99-card library. See
research/LIBRARY_ACCEPTANCE_AUDIT.md for table, captures and payload evidence.
Client npm run verify passed; the capture review identified duplicated search
content in the narrow side panel as remaining UI work. No runtime code changed
in this acceptance pass and no commit was made.

Recovery ownership cleanup: tap/counter reconstruction now belongs to immutable
PaymentObjectRecovery, with one retained-change lookup and identity guard.
PaymentRecovery coordinates the property, mana and movement helpers. See
research/LIBRARY_OWNERSHIP_AUDIT.md for boundaries. Behavior/replay suites pass
1,656 rules / 813 API (library-recovery-ownership-rules.log,
library-recovery-ownership-api.log). Activation-pause support remains incomplete.

Mana dependency recovery now preserves later retained production while refunding
earlier payment. PaymentManaRecovery validates retained events against their
recorded player snapshots, folds additions onto both the paid and refunded pools,
and checks the reconstructed current pool. It reuses ManaChanged.ApplyTo, so color,
restriction and provenance folding have one implementation. Retained spending,
pool emptying and unexplained snapshot changes still refuse. Four normal-command
and replay cases cover unrestricted/restricted production, with/without another
payment afterwards. Initial refusal: library-retained-mana-before.log. The first
post-change test run used an incorrect unsuffixed provenance key; the final test
compares the actual production event's provenance. Full suites pass 1,656 rules /
813 API (library-retained-mana-rules-final.log, library-retained-mana-api.log).
Broader dependencies and normal replacement-prompt activation remain unfinished.

Persisted interrupted-payment acceptance now covers two and four seats through
GameSessionService reload and GameHub Join/Command. The fixture starts a real
replacement pause through the internal engine entry point (API tests now have
friend-assembly access), then persists its emitted events. Join sends the choice
only to its owner, redacts the payment frame and hides library card text. Another
player's answer refuses without saving. The owner's answer retains mill, refunds
life, clears the prompt and leaves no activation on the stack. A second reload
and duplicate answer leave the saved log unchanged. All 813 API tests pass
(library-paused-recovery-api-final.log); production runtime behavior is unchanged.
This is service/hub integration, not browser acceptance or normal activation-pause
enablement. The prior 1,652-rule result remains the latest rules verification.

Tap-state dependency recovery now preserves the last later retained tap/untap
transition while reversing payment steps in reverse order. Missing identities
and inconsistent reconstructed states still refuse. Four normal-command/replay
cases cover tap and untap costs before a retained shuffle effect, with and
without another payment after it. All four previously refused
(library-retained-tap-before.log). Full verification: 1,652 rules / 811 API
(library-retained-tap-rules.log, library-retained-tap-api.log). This does not
complete general retained dependencies or replacement-prompt activation support.

Same-kind counter dependency recovery now accounts for the recorded delta of
later retained steps. Before refunding a removal, recovery verifies the current
count equals the paid result plus those retained deltas; it restores the paid
amount without erasing retained additions. Object identity loss, inconsistent
counts and negative results still refuse. Counter validation also requires the
event kind to match the cost. A normal-command replacement shuffle adds charge
counters after charge payment; that case previously refused
(library-counter-delta-before.log). An additional case pays another charge
counter after the retained addition to verify reverse ordering.

Counter-kind dependencies: reversing RemoveCountersCost now checks/restores only
the paid counter type within the current counter map. Retained effects changing
another counter type survive; retained changes to that same type or object
existence still refuse. The normal-command replacement regression now covers
both tap payment and two charge counters, followed by retained shuffle/storage
counter and a failing mill. The counter-payment branch reproduced refusal in
library-counter-kind-before.log; the updated path restores charge counters while
preserving storage through replay. No second restoration event was introduced.

Retained property dependencies: tap restoration now rejects retained changes to
tap state/existence, rather than every change to that object. Counter restoration
similarly checks the retained counter map; full movement restoration still
rejects any retained mutation of its identities. A normal-command regression
taps the source, replaces mill with shuffle plus a charge counter, then fails
another mill. Replay untaps the source while keeping the shuffle/counter and
emitting no activation. Initial fixture attempts did not execute the entire
replacement sequence; corrected syntax uses ReplaceWith(Sequence). Those initial
failures are not evidence of the conflict-validation defect. Full rules pass
1,645; same-property dependencies remain unsupported.

Four-seat recovery acceptance extends the hub fixture to two and four players.
An unauthorized seat's activation is refused without views, saves or log changes.
Every seat receives the legitimate update and its reloaded projection preserves
library privacy. Resubmitting activation after reload is a new attempt: it mills
the remaining card, refunds life and reserves a distinct journal ID. A further
attempt against an empty library refuses with no save/log/life change. This is
not idempotent retry support; activation commands differ from identified choice
answers. All 811 API tests pass (library-four-seat-recovery-api.log). No mill-cost
catalog definition was found in the searched source trees; live-card coverage
has not been established and no new definition was added.

API acceptance for immediate recovery: a real GameHub.Command activation over
GameSessionService pays life, mills one card and cannot pay a second mill. The
test verifies no refusal/prompt, updates addressed to both players, exactly one
save, and reload through a fresh session service. Replayed life is refunded,
one card remains in each of library/graveyard, no ability reaches the stack,
and both projected views conceal the remaining library card. The fixture uses
primitive test definitions, not new catalog definitions or a live browser table.
Focused verification: library-hub-recovery-focused.log.

Normal-command immediate recovery: non-mana Activate now shares the initial
payment/journal path. A failed attempt with a completed retained library action
uses recovery; a fully reversible failed attempt still refuses, and replacement
pauses still refuse. Game.Apply's priority-grant update now preserves Outcome's
payment origins; the normal-command test exposed their loss because the refunded
life payment incorrectly fired a trigger. Both retained-only and life-plus-mill
cases now exercise normal Game.Apply and serialized replay, checking retained
mill, life refund, absence of activation/prompt events and only the library
trigger. Reproductions: library-normal-immediate-before.log (refusal) and
library-normal-immediate-rules.log (spurious life-loss trigger). This integrates
the no-prompt path, not general replacement-choice continuation.

Immediate recovery provenance verification: the regression now also covers a
retained mill with no reversible payment. Its empty reversal set still reserves
a journal ID and retains the library trigger. A second failed attempt after
replay receives a distinct ID/reversal set, refunds its life payment, and leaves
the existing stack entry intact. Tests only; all 1,644 rules pass in
library-immediate-provenance-rules.log. API evidence remains the preceding run.

Immediate failure recovery: internal ActivateCore now creates the same payment
journal for a failed initial payment as for a paused payment, then calls shared
PaymentRecovery. The recovery event records/reserves its journal ID when no
choice allocated one, preventing future provenance collisions. A regression
starts without replacement effects, pays life, mills the last card and fails
another mill; it verifies the retained movement, refunded life, no prompt or
activation event, one retained library trigger, removed life-loss trigger, and
replayed ID reservation. The first attempted regression had an ambiguous event
type and did not compile; it is not runtime reproduction evidence. The normal
Activate entry remains gated and unsupported recovery plans still refuse.

Mixed-destination batch testing found a zone-system defect: a later replacement
effect evaluated against the original simultaneous board also reused that board's
allocation counters, assigning the same new object ID to two different arrivals.
The recovery validator correctly rejected the collision. ApplyMove now supplies
current IDs/timestamps/choice IDs/RNG through ZoneMoveSnapshot.WithCurrentAllocation,
shared with resumed snapshot evaluation, while preserving the original board for
replacement applicability. The sacrifice batch theory now covers ordinary and
commander cards with common or mixed graveyard/exile destinations, unique arrival
IDs, original order/history restoration, commander-candidate cleanup and removal
of both deferred departure triggers. Initial failure and diagnosis are recorded
in library-mixed-batch-rules.log and library-mixed-batch-diagnostic.log.

Selected exile/return recovery: ExileFromZoneCost from battlefield, hand and
graveyard, and ReturnToHandCost now use shared batch validation/restoration.
Origin matching is explicit per cost; return-to-hand commander candidates are
not synthesized. Four two-card replay cases restore identities/order/history
and retain the completed mill. Battlefield exile and return reproduced refusal
before the change. Initial hand/graveyard fixtures incorrectly passed the
battlefield-default ObjectFilter.Any; corrected fixtures omit the optional
filter. Those fixture failures are not recovery-defect evidence. Broader
replacement and commander-batch coverage remains unfinished.

Selected nonrandom discard now uses the same movement-batch validation and
restoration as sacrifice. A regression discards two cards in reverse hand order,
leaving a third between their original positions, then pauses on mill and fails
the next mill. Replay must restore the exact hand identities/order and history
while keeping the milled card. Pre-change refusal:
library-batch-discard-before.log. Random discard remains explicitly unsupported.

Selected-sacrifice batch recovery: PaymentMovementRecovery now describes an
ordered list for both single and selected sacrifice movements, validating the
aggregate object/zone/history/candidate changes. PaymentRecovery restores that
list in reverse order through the existing restoration record. A two-card test
chooses cards in reverse battlefield order with a surviving permanent between
them, then pauses on mill and fails the next mill. Replay restores both original
identities, order and history, retains the mill, and removes both deferred
leaves-battlefield triggers. The pre-change refusal is recorded in
library-batch-sacrifice-before.log. Mixed replacement destinations and commander
batch cases still need coverage; selected discard/exile/return remain unfinished.

### Remaining payment integration work, checked against current source

The following inventory replaces the vague instruction to finish "remaining
costs". It is based on CostPayment.One, ObjectCosts.Pay,
PaymentRecoveryPlan.Assess and PaymentMovementRecovery.Describe. A declared Cost
record is not evidence that the engine can pay it.

| Payment family | Current implementation | Work before general activation pauses |
|---|---|---|
| ManaCost / PayManaCost; PayLife | Paid; pure payment recovery and retained mana additions implemented. Life-only replacement outcomes now use event/state evidence. | Retained mana spending/emptying and broader dependencies remain unsupported. |
| TapSelf / UntapSelf / TapOther | Paid; multiple named permanents and later retained tap/untap transitions recover. | Identity changes and interactions with movement recovery need wider coverage. |
| RemoveCountersCost | Paid; successive removals recover while preserving retained same-kind additions and other counter kinds. | Interaction with reversed movements and broader retained effects needs wider coverage. |
| SacrificeSelf / DiscardSelfCost / ExileSelfCost | Paid; shared single-movement restoration with validated origin/destination/history. | Complex replacement outcomes and dependencies are not general restoration. |
| SacrificeCost / DiscardCost | Selected sacrifice and nonrandom discard use shared batch recovery; random discard explicitly refuses. | Cover mixed destinations and commander candidates; two-sacrifice ordering/trigger cancellation and two-discard hand ordering now have regression evidence. |
| ExileFromZoneCost / ReturnToHandCost | Paid and restored through the shared movement batch for tested battlefield/hand/graveyard exile and battlefield return. | Library departures are retained. Broader replacement destinations and commander batches need coverage. |
| MillCost | Paid; completed library movements and tested replacement shuffle/reveal/arrival operations retained. | Partial batches with retained and reversible consequences need explicit operation/dependency treatment. |
| Costs / ChooseCost | Flattened or selected into the existing payment queue. | Recovery follows their actual child steps; these need no second payment engine. |
| DiscardHand / PayEnergy / PutCountersCost / LoyaltyCost / RevealCost / CollectEvidenceCost / ForageCost / TapUntappedCost | Declared in Costs.cs but absent from CostPayment.One and ObjectCosts.Pay. | Do not add these unrelated mechanics merely to finish library recovery. They currently refuse before yielding a completed payment step. |

Ordered next work:

1. Batch restoration now covers selected sacrifice with original identities/order,
   retained mill and trigger cancellation verified. Expand its replacement and
   commander coverage before treating this as general movement recovery.
2. Cover selected discard/exile/return using that same batch path, including a
   replacement that changes only one destination and a commander in the batch.
3. Resolve retained-operation dependencies and partial failures. The current
   step-level retain decision must not be mistaken for per-movement recovery.
4. Internal failures before the first pause now use shared journal/recovery.
   Immediate life/mill failure has replay and trigger coverage; broaden this to
   the other retained operation/dependency cases before normal integration.
5. Then integrate normal activation commands, prove successful and failed paths
   through API/replay/privacy tests, and run live players. Spell continuation and
   the other library acceptance requirements remain separate unfinished work.

This inventory is an implementation checkpoint, not permission to enable the
normal activation path or a completion claim. No runtime changes were made by
this audit.

Zero-amount recovery: completed steps with no player, object, zone or movement
changes and no events (or only zero-delta counter events) are recognized as
no-ops by one shared assessment branch. Four regressions cover zero life, tap,
mill and counter-removal costs before life payment and interrupted mill failure.
All four previously refused recovery (library-zero-cost-before.log). The tests
verify life restoration, unchanged source and retained mill through event replay.
This does not infer no-op behavior merely from a cost's declared amount.

Self-exile recovery extends PaymentMovementRecovery's existing restoration path
to ExileSelfCost from battlefield, hand and graveyard. History validation depends
on the actual origin, while identity, zone ordering and commander-candidate checks
remain shared. Six replay cases cover each origin with ordinary and commander
sources, retaining a completed mill and restoring the original source without a
new movement occurrence. The initial test run used an incorrect player-specific
exile key and is not evidence of the recovery defect; corrected tests inspect
the destination recorded by ObjectMoved. Broader replacement effects remain
outside this validated movement shape.

Counter-removal recovery now validates one pure counter-removal operation against
its recorded before/after object and event delta. Recovery walks successive
payments backwards, checks the current counter map against each recorded result,
and stores the original map in PaymentRecoveryApplied. Replay restores it without
emitting CountersChanged; retained operations touching the same object still
refuse. A regression removes one then two charge counters, pauses on mill, then
fails another mill; it restores all charge counters and preserves two unrelated
storage counters while retaining the milled card. The pre-change refusal is in
library-counter-recovery-before.log. Broader retained dependencies and normal
activation-pause enablement remain unfinished.

TapOther recovery now uses the existing tap-state validator and restoration path.
A regression names two creatures, verifies both were tapped before the replacement
choice, then fails a later mill cost. Both original objects are restored through
serialized choice recovery while the completed mill remains. Recovery emits no
new ObjectChanged occurrence. The pre-change refusal is recorded in
library-tap-other-before.log. No separate multi-object restoration algorithm or
normal activation-pause enablement was added.

Retained-action trigger check: the arrival/reveal recovery scenarios now settle
the initial payment, serialize the deferred life-loss trigger, and answer the
replacement choice through Game.Apply. Recovery removes that refunded-payment
trigger; the retained battlefield-to-library arrival produces exactly one trigger
from its watcher, while the reveal-only branch produces none. Both replay the
expected stack contents. All 1,621 rules tests pass in
library-arrival-trigger-rules.log. This extension changes tests only; API evidence
remains the preceding 809-test run.

Library-arrival and reveal recovery: completed movements into a library and
CardsRevealed events marked FromLibrary are now retained alongside departures
and shuffles. The shared reveal interpreter records this boolean at disclosure;
historical public card snapshots still contain no object identities. Older events
without the field default to false and do not establish library provenance.
Real replacement-effect tests replace a sacrifice with a library move or reveal,
then fail a later mill payment. Recovery refunds life and preserves the completed
library action through replay. Both cases failed before the change in
library-arrival-reveal-before.log. Separate hand/library reveal tests verify that
the source distinction survives event serialization. General recovery and normal
activation pausing remain incomplete.

Replacement-shuffle recovery now recognizes explicit library shuffle events as
retained operations under CR 733.1, even if no card leaves the library and its
order is unchanged. A real replacement-effect regression pays life, pauses on
competing mill replacements, shuffles instead of milling, then fails a later
mill payment. Choice answering restores life, retains the shuffle/order and RNG
state, and completes recovery through event replay. One-card and four-card
libraries are covered. The original failure is library-shuffle-recovery-before.log.
This uses primitive replacement effects, adds no card definitions, and does not
enable the normal activation entry point's payment pauses.

Life-payment recovery validation now checks that player snapshots differ only in
life and that the recorded life delta equals its events. The same event-to-snapshot
check is shared with mana recovery. The library failure regression rejects both
an unrelated draw-counter change and a mismatched life total before restoration.
The malformed-journal test reproduced the gap in library-life-journal-before.log;
the updated rules suite passes 1,615 tests in library-life-journal-rules.log.
This is a recovery validation fix, not activation-pause enablement.

Latest verification: 1,648 rules tests (library-counter-delta-rules-final.log) and
811 API tests (library-counter-delta-api.log). Supported
internal recovery now includes life, mana (including tested restricted/Phyrexian
cases), tap state, ordinary/exile-replaced self-sacrifice and self-discard.
Normal activation pausing remains disabled; these are not general recovery claims.

Retained-draw regression: a constructed interrupted library operation mills one
card and draws another before a subsequent mill payment fails. Recovery restores
the discarded source while preserving the drawn card, its identity, draw count,
movement history and the milled card through serialized event replay. This tests
the recovery boundary directly, not live replacement-effect dispatch or a named
card implementation. No production change was needed for this case.

This checkpoint supersedes status sentences in the chronological entries below.
Those entries retain historical test counts and failures; they are not the current
completion checklist. The goal remains incomplete and restricted to library work.

| Requirement | Current evidence | Remaining work |
|---|---|---|
| Library inspection and search menu | LibraryRevealComponent uses the shared CardOptionsMenuComponent; GameBoardComponent.pick delegates to ChoiceSelectionService. Two-player single-card and two-card menu searches passed; see research/mtgo/LIBRARY_METHOD_PARITY.md. | Complete recovered offered-action mapping and conditional confirmation equivalence. Passing local searches does not prove MTGO parity. |
| Multi-card selection | Eight focused component tests cover limits, deselection, pending actions and order. Two-seat and four-seat live searches use existing Blighted Woodland. Four-seat table 50c845c8-4036-4063-a1a9-eefb5c85f58c verifies menu limits/deselection, observer privacy, tapped arrivals, counts and inspection expiry. | Broader search variants, four-seat responsive layout and Commander-format integration remain unproven by these casual-deck runs. |
| Commander opening library and mulligans | Four-seat Commander table e749a96b-23ff-4240-839c-b3ca7a2499d1 uses Avacyn plus 99 Plains through normal deck admission. It verifies command-zone exclusion, 40 life, 92-card opening libraries, multiplayer mulligans, private bottoming after refresh and the first-turn draw. | Commander search integration, alternative commander arrangements and full responsive coverage require separate evidence. |
| Temporary inspection access | EffectInterpreter.InspectDisclosedLibraries and FinishDisclosures retain resolution-scoped access across choices and expire it on completion. LibraryDisclosureLifetimeTests covers movement, replay and other active inspections. | Do not confuse active-access expiry with every revealed-card identity transition; that broader requirement still needs an audit. |
| Continuous top disclosure | LibraryRevealCoordinator reconciles permission transitions; ViewProjector calls LibraryDisclosurePolicy.TopAvailable for public and owner-only top access. Existing revealed-top search live test passed. | The payment visibility gate is tied to the internal interrupted-activation frame; it is not proof of every spell/ability announcement path. |
| Ordinary library costs | CostPayment.Pay executes completed costs and refuses a payment that pauses for a replacement choice. LibraryMillCostTests explicitly verifies that refusal. | Interrupted payment is not enabled in ordinary gameplay. |
| Interrupted library-cost activation | BeginActivationPayment is internal; serialized continuation and repeated replacement pauses are tested. ResumeActivationPayment completes the paid activation. Supported life and tap recovery passes through replayed choice commands while preserving completed library departures. | Complete recovery for remaining costs and retained dependencies, integrate the initial normal game command, verify trigger/disclosure timing, then exercise live. Spell payment continuation is also unfinished. |
| Failed/uncertain search delivery | Offline retry, reload after controlled accepted-response loss, and automatic rejoin after transport interruption have live evidence. The accepted-auto-reconnect test recovers a committed search after withholding the result, without reload or resubmission. Choice-correlated warnings now disappear on authoritative resolution; the live rerun verifies dismissal. | Packet-level network outages and other ambiguous-delivery orderings remain unverified. Controlled delivery suppression does not prove those cases. |
| Overall verification | Last full client verification: 1,822 tests plus coverage/build (library-choice-warning-verify.log). Current server counts and logs are listed above. | These are scoped checkpoints, not a completion claim for the full library plan. |

Next implementation priority is interrupted library-cost failure recovery. Keep
the current normal-command refusal until recovery is defined and tested; merely
switching Activate to allowPause would expose an unfinished transaction. This
priority stays within library behavior and does not authorize new card definitions.

### Mana recovery checkpoint

PaymentRecovery now restores supported pure ManaCost/PayManaCost steps from the
journal's full prior mana pool, preserving source provenance. Assessment requires
mana-only player changes and ManaChanged events. Recovery walks steps backwards,
checks their recorded after-pools against the current/reconstructed pool, and
refuses if a retained operation also changed that player's mana. Restoration
uses PaymentRecoveryApplied rather than emitting a new mana production event.
The shared serialized-content comparator also handles replayed tap snapshots.

A regression starts with three sourced green mana, pays two successive costs,
pauses on a mill replacement, then fails a later mill. Answering the replayed
choice restores all three mana and their source metadata, preserves the one
completed mill, clears the choice and leaves no ability on the stack. It failed
with Refused before the change (library-mana-recovery-before.log).
Full verification: 1,606 rules and 809 API tests in library-mana-recovery-rules.log
and library-mana-recovery-api.log. This does not establish every restricted-mana
or life-substitution case, reverse mana abilities, or enable normal activation
pausing. Sacrifice and other unsupported payments remain outstanding.

Restricted-mana follow-up: the same replayed failure regression now runs with
three green mana restricted to artifact payments, as well as unrestricted mana.
Both successive payments consume the restricted block; recovery restores its
original amount, exact spending filter and source provenance, without converting
it into unrestricted green mana or generating ManaChanged. The full rules suite
passes 1,607 tests (library-restricted-mana-recovery-rules.log). This test-only
follow-up does not establish life-substitution or dependent retained-mana cases.

Phyrexian-payment follow-up: recovery assessment now accepts negative life
payments within ManaCost/PayManaCost when the recorded player changes affect
only life and mana and the life delta matches those events. Existing recovery
refunds that life directly, with no LifeChanged or ManaChanged restoration event.
The replay regression now includes paying {B/P} with life before spending generic
mana, with both restricted and unrestricted pools. Both new cases failed before
the assessment change (library-phyrexian-recovery-before.log); they now restore
20 life and the original mana metadata while retaining the completed mill.
Full suites: 1,609 rules and 809 API tests (library-phyrexian-recovery-rules.log
and library-phyrexian-recovery-api.log). Retained-operation dependencies, sacrifice
recovery and normal activation enablement remain incomplete.

Zero-mana follow-up: a completed {0} ManaCost previously made the entire recovery
plan unsupported despite having no events or recorded state changes. All four
mana/restricted/Phyrexian regression variants reproduced that refusal when the
zero step was added (library-zero-payment-before.log). The mana assessment now
allows an empty event/empty player-change step while retaining its other state
change guards. Those variants and the full suites pass: 1,609 rules and 809 API
tests (library-zero-payment-rules.log, library-zero-payment-api.log).

## Continuous reveal transitions between effect instructions

Selective sacrifice recovery follow-up: PaymentSacrificeRecovery owns validation
of an ordinary SacrificeSelf journal and describes a replayable restoration.
It requires the expected sacrifice marker, battlefield-to-graveyard movement,
object/zone changes, departure/death history and commander-candidate change.
PaymentRecovery validates retained-object conflicts and the current state before
returning any refund. Restoration replaces only the sacrifice's object identity,
restores its battlefield position, removes its graveyard arrival and its own
departure/death history, and removes its commander-return candidate. The retained
mill remains in the same graveyard. Allocation counters are not rewound.

The former refusal-boundary regression now verifies restoration for ordinary and
commander sources through both internal recovery and Game.Apply choice answering,
then serializes/replays the returned events. It checks life, source properties,
battlefield ordering, retained mill, history, candidate removal and priority.
Changing the sacrificed arrival before recovery still refuses without a partial
refund. Full suites: 1,611 rules and 809 API tests
(library-sacrifice-recovery-rules.log and library-sacrifice-recovery-api.log).
Normal initial activation pausing remains disabled. Replaced sacrifices, changes
to battlefield membership after the sacrifice, and other retained dependencies
are deliberately unsupported by this implementation and still require work.

Sacrifice-recovery boundary audit: a new serialized scenario pays life, sacrifices
the source, completes a mill after a replacement choice, then fails a second mill.
The journal independently identifies the sacrifice and retained mill even though
both arrive in the same graveyard. Recovery correctly refuses the unsupported
sacrifice before refunding life; the failed-progress state retains both cards,
the source's departure history and the paid life. Full rules: 1,610 passed
(library-sacrifice-boundary-rules.log). This is a boundary regression, not a
sacrifice-recovery implementation. Required next work is selective object/zone
and history restoration, including commander-return candidates, with retained
dependency checks. Replacing the graveyard with its pre-sacrifice list would
incorrectly remove the retained mill and is not an acceptable implementation.

Tap/sacrifice trigger integration: the recovery scenario now taps the source
before paying life and sacrificing it, and runs Game.Settle before serializing
the replacement pause. A LeavesBattlefield watcher produces a deferred trigger
with payment provenance. Answering through Game.Apply restores the source's
original untapped state and removes that undone-sacrifice trigger. A separate
Library-to-Graveyard watcher still fires exactly once for the retained mill;
the final stack contains only that trigger. Both ordinary and commander variants
pass, as does the full 1,611-test rules suite (library-sacrifice-trigger-rules.log).
This test-only follow-up establishes selective trigger handling for this flow;
it does not enable normal activation pausing or cover all sacrifice replacements.

Exile replacement follow-up: the sacrifice recovery validator accepts a simple
battlefield-to-exile replacement, including its same-source "exiled" marker.
It validates departure history without a death entry, restores the original
source and tap state, and removes the exile identity/commander-return candidate.
Ordinary and commander cases both failed before support was added. All four
graveyard/exile and ordinary/commander variants now pass through command answering
and event replay, retaining the mill trigger and removing the sacrifice trigger.
Full suites: 1,613 rules and 809 API tests (library-exiled-sacrifice-rules.log,
library-exiled-sacrifice-api.log). Replacements that perform other effects and
retained dependencies beyond these supported shapes remain unfinished; normal
activation pausing is still disabled.

Retained battlefield-arrival follow-up: sacrifice restoration now permits added
permanents while requiring every original surviving battlefield object to remain
in its original order. It restores the source before its next original neighbor
(or at the end when none existed), preserving additional objects and their history.
A regression adds a token to the retained mill's journal, verifies the old blanket
battlefield equality check refused all four cases, then replays recovery and
checks the token, its entry history, original battlefield ordering and retained
mill. Missing/reordered original survivors still refuse. Full rules: 1,613;
API: 809 (library-recovery-arrival-rules.log, library-recovery-arrival-api.log).
The arrival is a constructed retained-operation journal in this regression, not
a new live card implementation or proof of arbitrary replacement dependencies.

Reproduced a single Sequence turning a reveal source face down and face up without
a settle boundary (library-reveal-sequence-red.log). The interpreter now invokes
the existing LibraryRevealCoordinator around executed effect nodes. No second
identity implementation was added. Tests cover an uninterrupted sequence and a
May prompt between hiding/revealing, plus serialized identity-event replay.
Search identity fixtures now use a real static reveal permission instead of a
tracking map inconsistent with their fake characteristics. Full rules 1,509 and
API 802 passed (library-reveal-sequence-*.log). This is engine verification;
the face-down/up sequence has not been run live. Temporary disclosure lifetimes,
intra-node transitions and interrupted casting visibility still need work.

## Live revealed-top search identity acceptance

Existing Courser of Kruphix and Worldly Tutor definitions exercised the combined
rule through two real browser seats, with normal land plays, mana payment and
casting. Table 45a4506a-0aa3-4ad6-8edd-f5988ff9a801 selected the already-public
top Courser, shuffled and restored it to top with a new ID in both views, then
drew a Courser on the caster's next draw step. Inspected the after-search capture.
Harness: client e2e/verify-library-revealed-search-live.js; log and captures:
e2e/screenshots/library-revealed-search-live.log, after-search.png and after-draw.png.
This verifies the combined public-top/search/shuffle path. Temporary disclosure
expiry, same-effect hide/reveal transitions and ambiguous delivery remain open.
No named card definitions changed. No commit made.

Previously failing regression: InterpreterTests.SearchRevealIdentity selects an already
revealed top card, searches it to the bottom with shuffle, then consumes RememberAs.
The result is lost because the shuffle's identity reset invalidates the saved ID
(library-search-reveal-identity-red.log). Previous passing suite totals predate this
new combination. Resolve search placement/continuation across reidentification
before extending temporary disclosure tracking. This is now fixed locally: after
shuffle/placement, search updates only its own remembered bookkeeping using the
reorder event's positional old/new identity correspondence. No global old-ID alias
is introduced. Three tests cover top/bottom with shuffle and bottom without shuffle,
then serialize a subsequent May prompt and consume the result after resuming.
Full rules 1,507 and API 802 passed (library-search-reveal-identity-*.log).
This combination remains unverified live; temporary disclosure lifecycle is still
outstanding. Nothing committed.

Current consolidated acceptance audit: research/LIBRARY_ACCEPTANCE_AUDIT.md.
It distinguishes demonstrated slices from missing requirements and supersedes
older blanket coverage claims. In particular, the former 401.6-tagged projection
test only checked a different top card after movement; it does not establish
revealed-card identity transitions. Its label and projection documentation are
corrected. Active disclosure lifetime, delayed visibility and same-card identity
transitions are the next required library implementation work.
The focused same-card reveal regression now reproduces the defect through
Game.Settle: the object remains #2 after reveal -> hidden -> reveal
(library-reveal-identity-red.log). This new regression is currently failing;
previous passing suite totals predate it. Ordinary casting/activation is batched,
so premature visibility there is not established solely by inspecting projection.

Follow-up: the reproduced settle-boundary identity failure is fixed by
LibraryRevealCoordinator and replayable tracking/reidentification events. The same
physical card receives a new object ID upon re-reveal, without a zone-move event.
Serialization, preserved physical identity/position and repeated-settle idempotence
are verified. Full rules 1,500 and API 802 passed (library-reveal-identity-*.log).
Within-effect transitions, shuffle/reorder disclosure expiry, temporary disclosures,
and interrupted-cost visibility remain open; live verification is outstanding.
No client changes or npm rerun for this server-only slice. Nothing committed.

Continuous-top shuffle/reorder follow-up: reproduced two identity failures in
library-reveal-reorder-red.log. ZoneReordered now applies the shared identity
reset for tracked revealed top cards, including a shuffle that keeps the same
order. Hidden cards retain IDs; identical non-shuffle order is a no-op. Replay and
subsequent settle do not reset identity twice. Rules 1,504 / API 802 passed in
library-reveal-reorder-*.log. Temporary disclosures and within-effect permission
transitions still require implementation; this rule has not yet been tested live.

## Library search confirmation from recovered MTGO methods

Correction: recovered CardSelectorViewModel select/OK commands are not wired to
the actual library-panel route established by later evidence. That route uses
Overlays.AddCardSelectorsAsChildren, Card_View action processing and the
PromptBox dispatch chain. Our explicit selection followed by Confirm is local
behavior with tested benefits; exact MTGO conditional confirmation equivalence
has not been established. See research/mtgo/LIBRARY_METHOD_PARITY.md.

Single-card search picks remain local until Confirm; choosing a different card
replaces the pick, while choosing the same card again retains it. Start over
clears it. Multi-card search choices toggle selected options within their bound.
Earlier live search, fail-to-find and four-player confirmation evidence remains
valid for that local behavior. It is not evidence for the incorrect MTGO
select/OK attribution above.
## Choice submission transport guard

A transport-boundary regression reproduced two concurrent sends for one pending
choice. SignalRService.choose now shares the in-flight promise by game and choice
ID, releasing it on completion. The test rejects the simulated request and checks
that retry sends again. This prevents simultaneous duplicate transport sends;
it is not an exactly-once delivery protocol or evidence of MTGO server behavior.
The initial red fixture also timed out because it rejected only the last request;
the fixture now rejects every outstanding request. The corrected fixture still
failed before the production guard was applied.

The UI currently clears local picks on submission. Preserving/recovering those
picks after uncertain delivery, and a live disconnect/reconnect acceptance test,
remain open. Do not report complete failure recovery from the transport test alone.
Verification: npm run verify passed 1,801 tests, changed-file coverage and build
(client e2e/screenshots/library-choice-flight-verify.log). Ordinary two-player
search passed at 711f1345-b1ad-4c74-9c00-886feb445d91
(library-choice-flight-live.log). The live run does not simulate a dropped transport;
failure and duplicate sends are exercised by the service regression. Nothing committed.

## Search selection recovery after failed delivery

Reproduced immediate loss of search picks in a board regression
(library-choice-retain-red.log). submitChoice now receives the current choice,
checks selection bounds, copies the outgoing answer, and retains picks for Search.
The existing choice binding clears selections on a different choice ID, and control
destruction clears a resolved choice. Other choice kinds retain previous cleanup.

Added --offline-retry to the existing two-player search harness. At table
4db6269a-c8c9-44ab-abd0-16abf702d8d4 it selected a card, emulated an offline network,
attempted Confirm, checked that the server choice stayed pending and the card
stayed selected, restored networking, and confirmed again. Final destination and
library-count assertions passed (library-choice-retain-live.log). Inspected
library-search-offline-retained.png, including the send-error notification.
This covers failed delivery followed by retry, not every ambiguous delivery or
page-reload scenario. The browser fixture deliberately does not persist private
local selections to storage.
Full npm run verify passed 1,802 tests, changed-file coverage and production build
(library-choice-retain-verify.log); existing stylesheet budget warning remains.
Nothing committed. Broader library-plan and MTGO parity work remains active.

## Search destination snapshot correction

The method audit reproduced a failing conditional-destination search: the first
selected card entering hand changed the destination calculated for the second.
LibrarySearchPlanner.Moves now freezes all selected destinations before movement,
including whether an operation repositions a card within its library. The
interpreter serializes the plan across replacement prompts and removes it after
completion; existing choice redaction withholds it from clients.

The new regression checks both cards reach the originally determined destination,
one shuffle, cleanup before the next May prompt, and remembered results after
serialized continuation. The four-player replacement test checks private plan
redaction and resumes with fresh subsystems. Full rules 1,499 and API 802 passed
(library-search-destinations-rules.log, library-search-destinations-api.log).
Client npm run verify passed (e2e/screenshots/library-search-destinations-verify.log).
This is a demonstrated correction to our implementation, not proof of MTGO server
parity. General simultaneous zone-change/trigger semantics remain open.

Live four-player search regression passed at table
5e75f5e7-3f3a-4722-8627-7d2937147052: private inspection, APNAP choices through
refresh, final library counts and one basic land per player. Log:
client e2e/screenshots/library-search-destinations-live.log. Captures:
library-four-search-choice-seat-0..3.png, choice-landscape.png and completed.png.
This existing Field of Ruin scenario verifies ordinary multiplayer search;
conditional destination freezing is verified by the engine regression above.
Full npm verification passed 1,799 tests, coverage and build; existing stylesheet
budget warning remains. No commit made.

Scope correction: the user's latest instruction is strictly the library. Earlier
card-identity/commander/lifecycle work below is historical and does not authorize
further work in those areas. Active scope is drawing, ordering, search, shuffle,
mulligans, look/reveal, mill, library privacy and reconnect behavior. Reuse existing
card/deck models; change shared code only as required by a library operation.

Latest library verification: private LookAt disclosures persist through replay and
are projected only to authorized viewers. The existing detail panel displays these
historical snapshots as "Last look - only you", without exposing current order.
Two-player live table afcbc870-83c1-4071-abe9-96e0ebc61d02 passed private disclosure,
opponent isolation, reconnect and landscape capture checks (client
e2e/screenshots/library-look-live.log). Inspected library-look-private.png.
Client npm run verify passed 1,793 tests, changed-file coverage and production
build (library-look-verify.log); the existing card-modal stylesheet budget warning
remains. This verifies this slice, not completion of the entire library plan.

## Library-only continuation: separate searcher and library owner

- Reproduced the wrong-library event/selection in library-other-search-red.log.
  Search now accepts optional LibraryOf while Who continues to identify the player
  searching. Existing definitions default to their previous own-library behavior.
- Candidate selection, search events, conditional shuffle tracking and subsequent
  shuffle/top/bottom placement all use the designated library. Search prohibitions
  and private choice visibility remain tied to the searcher. Other searched zones
  retain their existing owner semantics. No named card definitions were added.
- Rules: 1,472 passed (library-other-search-rules.log), including opponent-library
  choice privacy after serialized event replay, searcher-versus-owner prohibitions,
  correct-library shuffle, and top/bottom placement without creating a new object.
  Vocabulary: 326 passed; API: 795 passed. Schemas regenerated for the optional field.
- This new cross-player search path is not yet verified through the live client.
  Simultaneous searches across multiple libraries remain outside this completed slice.
  Nothing committed; the full library plan remains in progress.

## Library search persistence and answer authorization

- Added a four-seat API integration fixture using the real engine, serialized event
  log and fresh GameSessionService instances. This is an automated service test,
  not a live-browser multiplayer capture.
- An opponent-library search restores its three private choices only for the
  searcher. The other three seats receive neither option identities nor ChoiceCards.
- Wrong-player, stale-question and unoffered-option answers are refused without
  changing the log or saving. Two concurrent valid submissions produce exactly
  one applied answer, one refusal, one shuffle and one persistence write.
- A second service restart restores the completed search, with two cards still in
  the searched library and one exiled. Replaying the old answer is refused and all
  four views have cleared ChoiceCards.
- Focused test passed; full API suite passed 796 tests
  (library-search-reconnect-api-full.log). No production/UI changes in this slice;
  live cross-player search verification remains outstanding. Nothing committed.

## Scry/surveil completion and live surveil acceptance

- Reproduced missing completion triggers: four positive-count cases failed while
  the two zero-count cases passed (library-action-trigger-red.log).
- Added replayable PlayerScried/PlayerSurveilled events and wired the existing
  Scries/Surveils trigger vocabulary. Positive instructions finish even on an
  empty library; zero instructions emit no completion event. Surveil completion
  waits through all replacement pauses and fires once after the final decision.
  This follows the local comprehensive rules 701.22b/d and 701.25c/d.
- Full rules 1,478 and API 796 passed (library-action-trigger-rules.log and
  library-action-trigger-api.log), including player scope, serialization, zero,
  empty libraries, normal ordering and replacement interruption checks.
- Added verify-library-surveil-live.js using existing Desert/Arid Archway
  definitions and the shared LibraryTable harness; no named card changes.
  Two live players at fa4c1c0b-efb9-4c19-a4e4-29337242f776 passed private choices,
  refresh, landscape selection, correct library count and public graveyard result.
  The first test run incorrectly expected a Confirm button for a single option;
  corrected the harness to use the existing immediate single-selection behavior.
- Inspected desktop choice, opponent, landscape choice and completed captures in
  mtg-client/e2e/screenshots/library-surveil-*.png. The live scenario verifies the
  surveil interaction; watcher triggering is proven by the automated rules tests.
  Cross-player search and simultaneous multiple-library operations still require
  further acceptance work. Nothing committed.
- npm run verify passed 1,793 tests, changed-file coverage and production build
  (library-surveil-verify.log). Existing 18 lint warnings and the card-modal
  stylesheet budget warning remain.

## Multiplayer scry/surveil choices

- Reproduced missing all-player look/decision behavior in
  library-multiplayer-order-red.log. The shared arrangement path now freezes the
  affected players and top-card lists, privately discloses each player's own cards,
  and gathers selection plus top-order choices in active-player/nonactive-player
  order before altering any library. Completion events wait until all selected
  movements finish. Existing single-player memory keys remain readable.
- Internal remembered object lists are removed from projected choice context.
  They remain in the authoritative pending state for replay; authorized Options
  and ChoiceCards are the client contract. This prevents the later chooser from
  receiving another player's hidden library identities in continuation metadata.
- Four-seat tests restart the subsystem composition and deserialize the event log
  between answers. They cover keeping/reordering every card and selecting a card
  for bottom/graveyard, own-only snapshots, private choices, delayed library
  changes, and one completion event per affected player.
- Full rules 1,482 and API 796 passed (library-multiplayer-order-rules.log and
  library-multiplayer-order-api.log). This proves four-seat engine behavior;
  four live browsers performing the same all-player instruction remain unverified.
  No commit. The broader library architecture and acceptance audit remain open.
- Live two-seat regressions passed against this implementation: scry table
  3bf08434-4fa1-49a3-98c4-310955a8b077 confirmed top ordering across refresh and the
  next draw; surveil table 5f6e93e7-4f3e-4504-8261-88f5855d53e6 confirmed private
  choice, refresh, landscape selection and public graveyard movement. Inspected
  refreshed library-scry-v2-top-order-landscape.png and
  library-surveil-choice-landscape.png. The first surveil attempt ended before a
  game was created with a registration HTTP 500 immediately after API restart;
  the later run passed without an authentication code change. Evidence logs:
  library-scry-multiplayer-regression.log and
  library-surveil-multiplayer-regression-retry.log in client e2e/screenshots.
  Client source was unchanged; latest npm run verify evidence remains the prior
  passing library-surveil-verify.log, not a new run for this server-only slice.

## Search planning ownership

- Added LibrarySearchPlanner as the single owner of permitted zones, bounded
  candidate selection, quantity-bound planning and conditional destinations.
  It depends on IQuery/ICharacteristics and returns immutable planning data;
  it does not mutate game state, perform moves, or retain a second library list.
  The interpreter retains prompt/continuation orchestration and delegates moves
  to the existing zone system. Full search-coordinator extraction remains open.
- Removed the superseded candidate/destination/zone implementations from the
  interpreter. A call-site audit found only the planner invoking search bounds
  and evaluating the bounded candidate and conditional-destination rules.
- Reproduced duplicate search events from a repeated zone in a union, then made
  the zone union distinct in printed order. The regression verifies one search
  event and one option per card (library-search-planner-red.log).
- Full rules 1,483 and API 796 passed (library-search-planner-rules.log and
  library-search-planner-api.log). Live two-player search passed at
  a67c878e-0f54-4e67-8482-212386ec9aab, including filtering, details, privacy,
  tapped destination and counts. Choosing none with available matches passed at
  6fa3234e-9fd4-4953-867b-44f82da282ba. Logs are library-search-planner-live.log
  and library-search-planner-decline-live.log in client e2e/screenshots.
- Inspected the refreshed library-search-landscape.png capture. No client source
  changes or new npm verification run in this server-only slice. Nothing committed.

## Direct choice transport privacy

- The contextual-action audit confirmed dedicated manual library draw/shuffle
  commands are still missing. During transport tracing, found that the separate
  SignalR ChoiceRequested message sent authoritative PendingChoice directly,
  bypassing the redaction already applied to ViewUpdated.
- Reproduced the disclosure with a pending library instruction carrying another
  player's object-move proposal, remembered card IDs and deferred draws
  (library-choice-push-red.log). The hub now sends view.Pending from the same
  projected snapshot used by ViewUpdated; there is no second redaction policy.
- The regression checks both payload equivalence and the retained private server
  continuation, plus no direct choice push to the other seat. Full API suite:
  797 passed (library-choice-push-api.log).
- Extended the shared live LibraryTable harness to capture direct ChoiceRequested
  messages and assert they match the recipient's projected choice, contain no
  internal event/deferred movement/draw plan, and omit remembered card lists.
  Live scry/surveil tests now invoke these assertions. No named card changes.
- Both live runs passed with the direct-message assertions: surveil table
  0e8692e2-8022-4788-b2ba-38ad0043d432 and scry table
  4c67dd8c-35de-4e25-9f14-2a4d01ae889d. Evidence:
  client e2e/screenshots/library-choice-push-surveil-live.log and
  library-choice-push-scry-live.log. These runs also exercised the existing
  reconnect/order/phone interactions. No UI source changes, no new npm run verify
  for this API transport fix, and nothing committed.

## Card model first: foundation contract

### Reuse the existing collection/deck foundation

The user's clarification takes precedence over introducing replacement catalog
models: Collection, CollectionCard, the catalog CardDefinition, ICardLookup and
the shared ResolveForEntryAsync lookup already provide the collection/deck source.
Use and improve those boundaries. The conceptual responsibilities below do not
authorize building a second collection system or require a new class for each role.
The executable MtgEngine.Cards definition is a different representation, not proof
of duplicated card behavior merely because its type has the same short name.

The next implementation slice is the existing deck-to-game handoff. Establish what
must be preserved there before choosing additional runtime identity structures.
Do not lead with a new independent GameCard registry or a wholesale catalog rename.

Verified current data flow:

1. Collection owns CollectionCard rows. Each row already stores OracleId,
   ScryfallId, main/side/maybe board and nonfoil/foil/etched quantities. A row is
   a quantity-bearing entry, not one physical card or one game object.
2. ICardLookup.ResolveForEntryAsync resolves the selected printing first, with an
   Oracle-default fallback. CollectionReadService and CollectionService already
   reuse it; another printing resolver would duplicate this policy.
3. GameLobbyDecks reads the caller's existing collection, selects main-board rows,
   expands their quantities and separates commanders. Its resulting LobbyDeck
   contains StartingCard selections preserving Oracle ID, printing ID and finish.
4. GameLobbyStartup passes those lists to NewGameSeat. GameSessionService builds
   PlayerStart from the same selections and resolves definitions by Oracle ID.
5. CardFilePool reads executable definitions from generated cards/*.json. Before
   the catalog-bridge slice below, DeckGate could admit an untranslated card at an
   honour-system table while startup still refused its missing definition. Startup
   now resolves admitted manual cards from the same bulk catalog source when no
   compiled definition exists. Missing or unrepresentable catalog data still gives
   a typed refusal and retry; automated coverage is never inferred from printed text.
6. ZoneSystem creates GameObjects from those definitions and retains DeckSelection
   across zone changes and replay. GameArtworkService resolves authorized visible
   selections through the existing shared printing policy. Artwork keys distinguish
   different printings of the same Oracle card; face-down views omit the selection.

Existing source adaptation also needs reuse: the pipeline's Corpus reads the same
Scryfall bulk source and preserves faces/layout and textual stats. TemplateStage
constructs executable Face definitions for its supported templates. The catalog
CardParser intentionally presents a flatter view (including combined face text and
numeric stats), so blindly casting that view into engine definitions would lose
information. Audit/extract shared source mapping rather than writing another parser
or manually implementing named cards to repair the handoff.

Implementation acceptance for this handoff:

- Existing collections/decks remain the source; quantities expand exactly once.
- Selected printing and commander selection survive startup and event replay.
- Catalog availability and automated rules coverage remain distinct, explicit
  decisions. Reusing catalog data must never silently label a card fully enforced.
- Preserve the current strict-table refusal policy and excluded-card policy while
  defining a complete printed-data path for allowed manual cards.
- Build one source-to-definition adapter using the existing source mapping, with
  face/layout tests; no second metadata store or duplicate printing resolver.
- Feed the runtime identity/lifecycle work below from this same startup contract.

This section supersedes the earlier draw-first implementation order. The existing
library work remains available as behavior evidence, but the next implementation
slice establishes the existing deck-to-game definition boundary before extending zone
behavior. This is the target contract; the audit below distinguishes existing
behavior from work still required.

### Vocabulary and responsibilities

| Concept | Contract |
|---|---|
| Card rules definition | Immutable, versioned rules identity and face data shared by copies of a card. Oracle identity identifies the rules entry, not a printing or an individual game card. |
| Card printing | Printing identifier, edition, collector number, face artwork and presentation metadata. Prices and inventory are catalog concerns and never affect game resolution. |
| Rules program | Structured, executable abilities/costs/effects for a particular rules-definition revision, plus explicit coverage status. Displayable rules text does not imply executable support. |
| Deck entry | Rules identity, selected printing when present, quantity/finish, board, and deck role. A saved quantity expands into distinct cards at game initialization. |
| Game card | A particular non-token card admitted to this game: server-only CardInstanceId, immutable owner, original definition reference, optional printing reference, and persistent card designations such as commander. |
| Game object | The current rules object: ObjectId, zone, face/status, counters, damage, attachments, choices and stack context. Links to a game card when it represents one. Tokens, card/spell copies and abilities also require game objects without pretending to be deck cards. |
| Characteristics | Computed current name, types, color, power/toughness, abilities, controller and other affected values. Derive through the existing characteristics/layer system; do not overwrite the original definition. |
| Viewer projection | An explicit, permission-limited representation for one viewer. It is not a serialized GameObject or GameCard. Artwork/detail hydration happens only after visibility decisions. |

Names are descriptive target boundaries, not a requirement to introduce a class
for every row. Immutable records/value objects are appropriate for data; services
own behavior. Do not add Card.Draw(), Card.Shuffle(), database calls, image loading,
or UI state to a card. No class per named Magic card and no CreatureCard/LandCard
inheritance tree: a card can have multiple types and its characteristics can change.
Use generic ability/effect composition, explicit object kinds, and narrow service
contracts where dependencies actually vary.

### Identity and lifecycle invariants

1. Two copies with the same Oracle ID are distinct game cards. Printing choice
   neither merges their identities nor changes their rules behavior.
2. CardInstanceId persists for the underlying card; ObjectId identifies its current
   rules object. An ordinary zone change replaces ObjectId and resets object-local
   state through the one zone-change implementation. Explicit rule exceptions are
   handled by that lifecycle policy, not by carrying every field forward.
3. Targets, attachments, remembered objects and ordinary continuous effects refer
   to ObjectId. Persistent card identity must not make an old target follow a new
   object. Last-known information and rule-authorized tracking remain explicit.
4. Commander designation belongs to the game card. Commander damage/cast history
   references that identity, independent of copied characteristics, current face,
   current controller, or current object id. Copies do not acquire the designation.
5. Ownership is distinct from control. A control change must not rewrite the
   original owner; effective control comes from the existing rules computation.
6. Tapping, transforming and phasing are distinct transitions from changing zones.
   Model the applicable transition rather than resetting everything indiscriminately.
7. Face selection is valid for the layout and operation. Multi-face cards are one
   card, not independent deck entries; multi-card composite permanents need explicit
   constituent tracking rather than an assumption that every object has one card.
8. Tokens, copies of cards, copies of spells, ability objects and emblems are
   explicitly distinguishable. Copying values does not copy ownership, persistent
   identity, commander designation, counters or other noncopiable state.
9. Stable card identity is server-only. Neither hidden-card placeholders, detail
   endpoints, choice payloads nor image caches may expose a key that lets a viewer
   track a card through a shuffle or other loss of information. Public object ids
   and authorization lifetime need their own audit.
10. Creation, movement, copies and persistence are event-replayable. Definition and
    rules-program revisions used by a game must be pinned so later catalog updates
    cannot silently change replay behavior. No parallel mutable card registry.

### Definition and boundary validation

- Represent supertypes, types, subtypes, faces/layout, mana cost, color indicator,
  rules text and supported ability structure without inferring behavior from a name.
  Preserve variable printed stats and distinguish absent values from zero.
- Keep color identity (deck construction) separate from current color (game rules).
  Keep a face's mana cost distinct from context-dependent computed mana value.
- Prevent invalid face indexes/layout combinations and mutation through shared
  caller-owned collections. IReadOnlyList alone does not make a definition immutable.
- Keep source catalog data and compiled rules linked through one explicit adapter
  and revision contract; do not have two independently editable rule authorities.
- Missing executable support must yield an explicit admission/coverage decision.
  A card with artwork and rules text is still a valid catalog card; whether it is
  permitted at an automated or manual table is a separate policy.
- Validate deck quantities and identifiers before expansion, allocate each game-card
  identity exactly once, and preserve selected printing information at startup.

### Current-code audit

| Evidence | Finding / required change |
|---|---|
| MtgEngine.Cards/Definitions.cs | CardDefinition/Face already hold structured rules and abilities. Audit defensive immutability, definition revision, and layout validation; preserve reusable effect vocabulary. |
| MtgEngine.Domain/Models/Card.cs | A second CardDefinition includes Oracle rules, artwork, set, collector number and printing prices. Separate catalog rules from printing metadata and clarify naming; do not blindly merge catalog and executable AST types. |
| MtgEngine.Api/Services/GameLobbyDecks.cs | Now expands existing quantities into StartingCard selections and retains printing/finish when extracting commanders. Live selected-printing acceptance recorded below. |
| MtgEngine.Rules/Events/GameEvent.cs | PlayerStart now carries typed selections. StartupLogMigration upgrades older Oracle-only lists; old logs cannot recover printing selections that were never stored. |
| MtgEngine.Rules/State/GameObject.cs and Ids.cs | New startup cards now have a server-only CardInstanceId retained by Reborn; no parallel registry. Legacy objects remain without that identity until an explicit migration is implemented. |
| MtgEngine.Rules/State/PlayerState.cs and GameState.cs | CommanderIdentity.Of now shares the persistent card key across damage, casts and queries. Objects without identity retain the old owner/Oracle fallback; full historical backfill remains open. |
| MtgEngine.Rules/Zones/ZoneSystem.cs | Reborn centralizes zone-change resets. Retain one lifecycle implementation, carrying only authorized persistent references. |
| MtgEngine.Rules/Zones/ZoneSystem.Copies.cs | Copy-of-card uses a string designation alongside IsToken and StackEntry.IsCopy. Audit these paths and replace overlapping classifications with an explicit kind contract. |
| MtgEngine.Rules/Views/ViewProjector.cs | Builds explicit projections, a useful privacy boundary to preserve. New internal identity fields must remain absent by default. |
| MtgEngine.Api/Services/GameArtworkService.cs | Hydrates authorized objects by Oracle ID. Selected-printing and face-specific presentation need a separate authorized printing reference. |

This is a source audit, not proof that each listed gap causes a currently reproduced
runtime defect. No runtime model changes are made by this specification update.
Rules references were checked against the repository's unmodified Comprehensive
Rules: 108-113, 400.7, 707, and 903.3. The rules document remains reference material,
not an implementation-status tracker.

### Migration and acceptance order

1. Establish model-contract tests: duplicate Oracle IDs remain distinct; zone change
   changes ObjectId while preserving the underlying card; old targets stay invalid;
   commander identity survives copying/face/control changes; copy-created objects
   acquire no source-card identity; printing survives startup without affecting rules.
2. Introduce the validated identity/definition contracts and wire game creation,
   movement, copy creation and persisted events together. Remove superseded fields
   and paths in the same migration. Choose explicit log versioning/migration rather
   than guessing missing identities from current mutable state.
3. Migrate commander history, deck expansion and artwork projection to the same
   identities. Test serialization/replay and ensure all viewer payloads omit private
   identity. Test multi-face cards, non-card objects and composite-object requirements.
4. Resume zone/library coordinators against these contracts. Then complete deck
   validation, opening hands, draw/search/shuffle/ordering/reveal and all previously
   recorded full-plan requirements.
5. Run relevant .NET suites, client verification, live multiplayer, reconnect/privacy
   checks and rendered captures. Passing earlier library tests alone does not prove
   this model migration complete. Show the result before committing.

## Rewrite architecture and acceptance contract

The library work is a clean replacement of the affected implementation, not a
second library engine beside the existing one. Splitting a partial class into more
files does not establish separate responsibilities. Existing code is evidence of
behavior to preserve or correct, not an architectural constraint.

Target ownership (some classes below already exist; others are proposed):

| Owner | Responsibility |
|---|---|
| CommanderDeckValidator / CommanderPairPolicy | Pure deck and commander eligibility rules before game creation. |
| LibraryOrder | Pure ordered-library transformations; preserve identity when cards remain in the same zone. |
| LibraryDrawCoordinator (partially implemented) | Draw one at a time, including replacement choices, completion, and resuming the remaining draws. |
| LibrarySearchCoordinator (proposed) | Search authorization, candidate selection, reveal requirements, destination movement, and subsequent shuffle. |
| LibrarySearchPolicy | Pure search eligibility and selection constraints, including qualified fail-to-find. |
| LibrarySearchPlanner | Permitted zone union, bounded candidates, quantity bounds and conditional destination planning, using query/characteristic interfaces without mutating game state. |
| OpeningHandCoordinator | Multiplayer opening-hand decisions, redraw rounds, and London mulligan bottoming. |
| ZoneSystem | The single implementation of zone movement and new-object identity. Coordinators delegate movement here. |
| ReplacementSystem | The shared replacement pipeline; no separate replacement implementation for library operations. |
| ViewProjector | Viewer-authorized information only, including temporary look/search permission and public reveals. |
| ChoiceControlsComponent / ChoiceSelectionService | Render server-authorized choices and manage local selection; no client rule decisions. |

Keep the event-reduced GameState as the sole authoritative state. A Library object
must not maintain a second mutable list of cards. Persist paused operations in
replayable state so reconnects and a fresh service instance resume identically.
The interpreter translates instructions and delegates; it must not own a second
draw, search, shuffle, or mulligan implementation.

Apply SOLID concretely:

- One reason to change per coordinator or policy; no catch-all LibraryManager.
- Extend card behavior through generic instructions and existing replacement
  contracts, rather than branches on card names.
- All implementations of an operation contract preserve ordering, privacy,
  event replay, and interruption semantics.
- Introduce narrow interfaces at actual dependency boundaries; do not create an
  interface for every pure helper or expose the entire engine to each coordinator.
- Depend on movement, replacement, and randomness contracts where needed;
  domain policy must not depend on HTTP, SignalR, Angular, or database access.

Replace one operation end to end at a time: establish behavior tests, implement
the owner, route all callers through it, and remove the displaced implementation
and obsolete tests/adapters in that same slice. The card-model foundation above
now comes first; completed draw work supplies regression evidence for its migration.
Avoid a temporary permanent legacy fallback.

Acceptance includes a call-site/duplication audit as well as rule tests. Verify
replay, replacement interruptions, duplicate submissions, privacy, and reconnects;
then live multiplayer, rendered captures, and npm run verify. Show the results
before committing. The architecture above is a target, not a claim that the
rewrite is complete.

## Requirements and evidence

| Requirement | Current evidence / remaining work |
|---|---|
| Deck validation and commander separation | GameLobbyDecks and GameLobbyStartup exist; audit legality and 100-card/two-commander cases. |
| Ordered library, server shuffle, draw, zone movement | ZoneSystem has existing operations; verify replacements, empty draws, ordering, randomness, and event privacy. |
| Private library projection | ViewProjector hides contents. Added LookableTopCard and regression coverage for private permission, revocation, current top and empty library. Client transport, board projection, pile/viewer and detail lookup wired; 71 focused client tests passed. Rendered verification pending. |
| Public revealed top | Existing RevealedTopCard; inspect delayed visibility and reveal identity transitions. |
| Opening hands and mulligans | Round-based declarations implemented with replayable OpeningHandsChanged state. Ten mulligan tests pass, including four seats and bottom-choice replay. UI and live acceptance pending. |
| Search | Existing interpreter/choice kinds. Audit qualified fail-to-find, unrestricted search, scope, destination, reveal, and shuffle continuation. |
| Look/reveal/order, scry/surveil/mill | Audit existing engine operations; provide shared authorized choice UI, without a separate implementation per card. |
| Commander library replacement | Existing commander zone-change code; verify owner prompt, movement continuation and shuffle behavior. |
| Duplicate/stale actions | Audit hub/session revision and request identity; prevent repeated draw and choice submission. |
| Reconnect | Restore choices and permission-limited data; verify stale selection rejection and cleanup of cached private previews. |
| UI | Library pile/count, contextual desktop/touch actions, details, opening hand, search, top/bottom ordering, pending/error states. |
| Manual play | Explicitly authorized and logged manual operations, separate from engine permissions. |
| Verification | Four live seats, two-player opening rule variant, network payload privacy, desktop/phone captures, npm run verify, relevant .NET suites. Show before committing. |

## Evidence recorded so far

- `library-tests-red.log`: new private-top regression failed because the authorized
  owner received no card (1 failed, 1 passed).
- `library-tests-green.log`: all 19 HiddenInformationTests passed after projection fix.
- These tests prove only this engine visibility slice. They do not prove the UI,
  static ability integration, or the remainder of this plan complete.

## Continuation

1. Wire authorized top-card data through transport, board model, pile/viewer, and
   shared detail selection. Clear it when permission disappears.
2. Exercise effect creation and static permissions, not only a synthetic active effect.
3. Implement round-based mulligan state, then opening-hand UI.
4. Continue every requirement above and gather live acceptance evidence.

## Latest verification

- Full rules suite: 1,407 passed (library-rules-suite.log).
- Focused client suite: 71 passed (mtg-client/e2e/screenshots/library-preview-tests.log).
- Added event type is registered in events.txt. No commits made.
- Next: opening-hand UI, live library-preview captures, effect/static-permission integration, and the remaining requirement audit. Full npm verify and live four-seat tests remain outstanding.

## Clean architecture requirement

The user requires a clean SOLID rewrite with duplicate and obsolete paths removed.
OpeningHandCoordinator now owns decisions, redraws, bottoming and round advancement.
OpeningHandPrompts constructs the authorized choice payloads. ChoiceSystem validates
and dispatches answers; TurnSystem bridges completion to turn one. Redraw movement
has one implementation using IZoneSystem. Existing standalone choice resumption is
retained in the coordinator for pending choices without round state; it needs review
against the final persistence contract, not a second copy of movement logic.
Client search/ordering controls still live inside GameBoardComponent and must be
extracted into a dedicated choice component/service as the next UI step.

## Choice UI progress

- ChoiceSelectionService now owns ordered selections, offered-option validation,
  maximum picks, and division counts. ChoiceControlsComponent renders the controls;
  the board delegates and dispatches commands.
- 41 focused choice/board tests passed. Fixed an existing test that used an
  uninitialized store depending on randomized test order.
- Full client verification passed with 1,775 tests, coverage gate, and production
  build (library-verify.log); existing lint/style-budget warnings remain. A final
  stylesheet-only consolidation followed that run, with final landscape recapture.
- Six library prompt fixture captures completed. Visual inspection caught horizontal
  overflow; fixed wrapping and pinned action row. Final bottom-order landscape
  capture shows Confirm and Start over reachable.
- These are UI fixture captures, not evidence of live search or four-player play.
  Rich card selection, live permission/reconnect checks, and the remaining complete
  library acceptance audit are still outstanding. Nothing committed.

## Search policy and live opening verification

- LibrarySearchPolicy centralizes search bounds and qualified fail-to-find rules.
  New regressions first failed for unrestricted library searches and up-to-zero;
  full rules suite now passes 1,410 tests (library-search-green.log).
- GameSessionService now enables opening-hand decisions for actual tables. The API
  regression first showed turn one started immediately. All 35 GameHub tests pass,
  including four-seat private bottom choices, replay, and concurrent duplicate refusal.
- e2e/verify-library-live.js passed with four real browsers and independent SignalR
  viewers: declarations before redraw, free first mulligan, second-mulligan bottoming,
  hidden choices, refresh recovery, six-card final hand, and matching library counts.
- Live fixture uses four casual basic-land decks to isolate opening behavior. This
  does NOT establish Commander deck-construction validation, partner commanders,
  live search effects, first-turn draw, or the remainder of the full library plan.
- Captured library-live-private-bottom.png and library-live-kept.png. No commits.

## Single mulligan declaration path

- Removed the standalone legacy redraw/declaration fallback from OpeningHandCoordinator.
  Decisions now require replayed OpeningHands state and refuse absent, already decided,
  or completed round seats. The round coordinator remains the sole redraw implementation.
- Replaced tests of the obsolete standalone flow with round-state dispatch/refusal tests;
  existing integration tests retain redraw, keep, bottoming and multiplayer coverage.
  Added real-engine repeated mulligans through a zero-card hand and turn-one transition.
- New absent-round regressions failed first (2 failures). Full rules suite passes
  1,411 tests in library-single-path-green.log. API regression result is recorded in
  library-single-path-api.log. No UI changes or commits in this slice.
- Prior note describing a retained legacy fallback is superseded by this entry.
  Full Commander validation, remaining library interactions and final live/UI acceptance
  are still incomplete; this is not completion of the overall plan.

## Opening-hand concessions

- Reproduced three real-engine failures: the player currently declaring leaves,
  a later player leaves, and the player answering a bottom choice leaves.
- PlayerLost now removes the departing seat's opening obligations and its opening
  question in the replayable event fold. Game delegates continuation to TurnSystem;
  the existing coordinator advances remaining seats. Begin selects the next live
  seat when the original starting player has departed.
- Full rules suite: 1,414 passed (library-concession-green.log).
- API multiplayer suite: 36 passed (library-concession-api.log), including a new
  restart-after-concession test and rejection of the departed player's old choice.
- These are engine/API results, not a new rendered or browser acceptance claim.

## Commander admission foundation

- Added CommanderDeckAdmission (metadata resolution through ICardLookup) and pure
  CommanderDeckValidator (construction constraints). GameLobbyDecks delegates to
  them before DeckGate checks implementation coverage.
- Covers 100 cards including commander, legal/eligible commander, legality, color
  identity including colorless, basic land types, singleton/basic lands and explicit
  any-number/up-to copy text. Updated lobby fixtures to actual 100-card decks while
  retaining normal/foil/etched and honour-system coverage.
- First focused run passed 14 tests; two additional regressions are in the full API
  run recorded in library-deck-api-suite.log. No new UI or live Commander acceptance
  evidence yet.
- Not complete: collection model still selects one commander. Partner variants,
  interchangeable-name metadata and other card-specific deckbuilding overrides
  need implementation and acceptance evidence before full format coverage is claimed.
- Full API suite completed: 771 passed (library-deck-api-suite.log), including the two additional admission regressions.

## Commander pair policy

- CommanderPairPolicy now owns compatibility under CR 702.124: matching Partner,
  matching named Partner variants, reciprocal Partner with names, Choose a Background,
  and Doctor's companion. Requires distinct legal legendary cards, rejects mixed variants,
  and accepts the older Friends forever spelling. CommanderDeckValidator delegates
  pair eligibility and combines both identities while still enforcing exactly 100 cards.
- Focused policy/construction run passed 21 tests before the additional combined-identity
  test; full API result is in library-partner-api-suite.log.
- This is backend policy only: persisted secondary commander selection and its client
  editor are NOT wired yet. No claim of usable two-commander lobby support or live evidence.
- Full API suite passed: 782 tests, including combined pair identity/count validation.

## Commander pair persistence and admission

- Added nullable SecondaryCommanderOracleId to Collection, deck DTOs/requests and
  CollectionService create/list/read/update. Generated additive migration adds only
  that nullable column. Lobby loading separates both selected commanders before
  running shared legality validation.
- API suite: 784 passed (library-partner-persistence.log). Additional service roundtrip
  test and all eight lobby-deck tests pass (library-partner-roundtrip.log), covering
  saved pair loading into a 98-card library, missing second commander and clear/update.
- Client editor integration remains outstanding. Existing clients that omit the field
  when updating a deck clear it; update all client save paths before final acceptance.

## Client pair transport and save preservation

- Deck API models and update action/effect now carry secondaryCommanderOracleId.
  DeckApiService preserves the current server value when an existing Commander
  metadata save omits it; explicit null clears it, leaving Commander clears it.
  This centralizes compatibility for existing callers instead of duplicating merge
  logic across each notes/title/tags/cover handler.
- Three HTTP-client regression tests passed (library-client-partner-tests.log).
  Full npm verification is recorded in library-client-partner-verify.log.
- No rendered controls changed in this slice. Second-commander picker, combined
  identity display and removing both commanders from client library views remain.
- Full npm run verify passed: 1,778 tests, coverage and production build. Existing 18 lint warnings and card-modal style budget warning remain. Picker UI is still pending.

## Second commander client selector

- Added SecondaryCommanderComponent using the shared select menu and existing card
  detail route. It offers unique main-deck legendary cards other than the primary;
  server admission validates pair compatibility (the selector does not claim it).
- DeckCommanders centralizes selection membership for grid, side list and free-layout
  filtering, plus combined identity used by DeckLegalityService. New identity regression
  failed first, then 290 focused tests passed; selector interaction tests added.
- npm run verify passed: 1,782 tests, coverage and production build, with existing warnings.
- Created dedicated Commander pair preview test deck (Thrasios, Tymna, 98 Wastes).
  Captured and visually inspected desktop and 375x667 phone images:
  deckstate-deck-commander-commander-pair-preview.png in each device directory.
  Both show saved Tymna selection, reachable card details, and no document overflow.
- Initial captures were invalid (intro overlay and fallback freeform deck). Harness
  now dismisses intro and requires the selector before saving a Commander screenshot.
- Final browser save/reload interaction and four-player Commander admission remain
  part of the overall live acceptance; these captures alone do not prove those flows.

## Live pair saves and real Commander opening acceptance

- Live browser tests exposed a real integration omission: updateDeckMetaSuccess ignored
  secondaryCommanderOracleId in both active/listed deck state. Added a failing reducer
  regression, fixed both projections, and reran npm run verify: 1,783 tests plus coverage
  and production build passed (library-pair-reducer-verify.log).
- verify-commander-pair-live.js passes on 375x667 phone and desktop: remove, reselect,
  unrelated notes save, refresh and API-confirmed persistence. Captures saved as
  commander-pair-live.png in device folders; phone capture visually inspected.
- Extended verify-library-live.js with Commander mode and first-draw checks. Four real
  browsers using 100-card Etali/Mountain decks passed admission, commander separation,
  all-seat declarations before redraw, free first mulligan, private bottom choice,
  refresh recovery, hand/library counts, and the starting player's turn-one draw after
  four upkeep passes (library-commander-live.log). Harness now concedes all seats.
- Partner game-start mode is retained (--commander --partners), but its Thrasios/Tymna/
  Wastes fixture cannot start: those three engine definitions are absent. No card logic
  was added to work around this. Pair save/legality/zone separation have API/UI evidence;
  live partner-game startup remains unverified.
- A later live run encountered a missing port-4200 listener. Confirmed Angular process
  was gone, restarted it (library-client-server.log), and reran successfully.
- Full plan still requires rich library search/look/order UI, effect permissions and
  replacement audits, remaining construction exceptions and final acceptance review.

## Authorized choice card payload

- GameView now carries ChoiceCards, populated only for the choosing viewer and only
  for search, scry, surveil and mulligan-bottom choices. IDs are limited to offered
  options, deduplicated and projected through the existing face-down-safe object view.
  The collection becomes empty after the choice ends or for another viewer/kind.
- GameArtworkService now hydrates authorized choice cards and private lookable-top
  cards, still using only local metadata and skipping face-down identities.
- New privacy regressions added before the field existed. Full rules suite passes
  1,417 tests (library-choice-cards-green.log); full API suite passes 786 tests
  (library-choice-cards-api.log), including choice artwork filtering.
- Client option cards/detail rendering and live search privacy are still pending.
  This backend data contract alone is not evidence of a working search UI.

## Client authorized option-card mapping

- Engine transport accepts optional choiceCards and option object IDs, joining only
  matching non-face-down cards for the choosing viewer. Nonmatching options remain
  text-only; another viewer receives null options. No separate card cache is retained.
- ChoiceView and ChoiceDto carry optional card data. GameViewAdapter uses its existing
  card conversion so image, rules text and other details follow the shared path.
- New transport regression initially failed to compile before the contract existed;
  65 transport/adapter tests now pass (library-choice-transport-green.log).
- Rendering images/detail controls, filtering/ordering and live search remain pending.
  No user-visible UI change or full npm verification is claimed for this data-only slice.

## Choice images and shared card details

- ChoiceControlsComponent renders authorized option art and an independent Details
  action. Board delegates that action to its existing selected-card preview/modal.
  selectPreviewCard resolves current local choice cards only; ending or changing the
  authorized choice removes that source. Added interaction and revocation tests.
- Corrected the live prompt heading to Your choice for local pending choices.
- npm run verify passed: 1,786 tests, coverage and production build
  (library-choice-art-verify.log), with existing warnings.
- Four-browser Commander test now opens Details during a private bottom choice,
  verifies the choice ID is unchanged, closes the modal and completes the flow.
  It also captures 667x375 landscape and checks document width.
  library-choice-art-live.log passed; inspected library-live-private-bottom.png,
  library-live-choice-details.png and library-live-choice-landscape.png.
- Landscape fits but Details sits below the first visible row and needs scrolling;
  compact card sizing remains a visual improvement. Rich live search/scry/surveil
  interactions, filtering and explicit reordering are still pending.

## Shared choice filtering and ordering controls

- ChoiceSelectionService owns bounded adjacent reordering for ordering choices;
  invalid/unselected moves and nonordering/division choices leave selection alone.
  New choice IDs clear selection. Component controls reuse this service.
- ChoiceControls uses the existing SearchInputComponent to filter offered search
  labels without dropping selected options. A new choice clears the filter.
- Compact images below the shared navigation breakpoint retain 44px action targets.
  Inspected library-live-choice-landscape.png: the first row Details buttons are
  now visible without scrolling at 667x375.
- Red regression confirmed missing move method before implementation. Full npm run
  verify passed with 1,788 tests, coverage and production build; existing stylesheet
  budget warning remains (library-choice-order-verify.log).
- Four-browser Commander opening-hand/reconnect/privacy/first-draw test passed
  (library-choice-order-live.log). This does not verify live search or ordering.
- Pending: wire explicit ordering semantics for mulligan-bottom (transport currently
  derives isOrdering from kind containing order), live search/scry/surveil tests,
  and the remaining full-scope work noted above. Nothing committed.

## Live two-card mulligan ordering

- Transport now marks mulligan-bottom as ordered, activating the shared reorder
  controls. Regression first failed (library-bottom-order-red.log).
- Extended verify-library-live.js with --ordering: a third mulligan creates a
  two-card bottom choice; actual browser controls select two, reverse them, confirm
  once and verify both chosen objects leave the hand, five remain, and first draw
  and all library counts still match across four Commander seats.
- Four-player live run passed (library-bottom-order-live.log). Inspected
  library-live-two-card-bottom-order.png: numbers, arrows and Confirm render.
  This verifies UI submission and chosen membership, not hidden bottom order itself.
- npm run verify passed with 1,789 tests, coverage and production build
  (library-bottom-order-verify.log); pre-existing card-modal size warning remains.
- Next rules gap confirmed in EffectInterpreter.Actions.cs Sorted/Look: scry and
  surveil never ask to reorder remaining top cards. Need a shared resumable ordering
  operation and privacy/replay tests. Also audit same-library movement identity;
  existing scry test explicitly expects a new object, which needs rules review.
- Full objective remains in progress; nothing committed.

## Shared scry/surveil arrangement and ordinary library identity

- Added LibraryOrder.Arrange for pure top/rest/bottom ordering, preserving the objects
  and untouched cards. Removed the old Sorted and Look implementations.
- EffectInterpreter.Library supplies one resumable select/order flow for scry and
  surveil, saving looked/selected/top lists in the existing remembered context.
  Remaining groups larger than one produce library-top-order; other instructions
  wait until it is answered. Surveil moves still use the shared replacement-aware
  Relocate path and stop when a replacement prompt halts it.
- Ordinary scry emits ZoneReordered rather than ObjectMoved. Updated an obsolete
  test that expected a new object from a same-zone move. The special revealed-top
  identity rule CR 401.6 remains separate unfinished work.
- New tests first reproduced three failures (library-ordering-red.log). Final full
  rules suite: 1,424 passed (library-ordering-green.log), including choose-none,
  short/empty library, both verbs, subsequent instruction timing, selected bottom
  order, fresh-interpreter resumption, and private choice-card projection.
- Full API suite: 786 passed (library-ordering-api.log). API restarted with no-build
  after suites; parent launch PID 43832, runtime output library-api-runtime.log.
- Client maps scry/surveil to existing ordering controls; library-top-order already
  maps through the order kind. npm run verify passed, 1,789 tests plus build
  (library-scry-verify.log); existing stylesheet warning remains.
- New flow is NOT yet live-browser verified. Existing Castle Vantress definition
  has Scry 2 and is a candidate for live verification without new card logic.
  Replacement interruptions need dedicated actual-engine tests, plus all remaining
  full-plan requirements. No commits or completion claim.

## Actual replacement continuation and two-stage privacy verification

- Added LibraryReplacementContinuationTests using real LayerSystem, ReplacementSystem,
  ZoneSystem and EffectInterpreter. Two competing graveyard replacements interrupt
  surveil once per selected card. The test checks correct proposed card IDs, exact
  life changes, both original cards retained when movement is replaced, no graveyard
  entries, and one completion of the instruction following surveil.
- Initial fixture did not match library cards (query default zone); corrected its
  filter to In=Library. No production defect claimed from that fixture failure.
- Existing actual scry/surveil tests now project the second prompt for both viewers:
  only the choosing player receives remaining top cards; opponent options and context
  are empty; chooser ChoiceCards clears on completion.
- Full rules suite passes 1,425 tests (library-continuation-suite.log). No production
  code/client changes this slice; prior npm verify remains the latest client evidence.
- API restarted after tests. Live Castle Vantress ordering, full search workflows,
  revealed-top timing/identity and all other outstanding plan items remain open.
  No commit; objective not complete.

## Live two-player scry ordering

- Added client e2e/verify-library-scry-live.js, reusing board-driver API/auth and
  Selenium driver helpers. Uses two dedicated casual decks of Castle Vantress,
  already defined; no card definitions or production code changed.
- Plays lands through legal turns, pays four mana and activates Scry 2. Browser
  chooses none for bottom, refreshes the resulting library-top-order prompt,
  selects both cards, reverses via arrows and confirms.
- Independent viewer payloads verify opponent options/ChoiceCards are empty and
  authorized ChoiceCards clears after resolution.
- Repeats scry on the next own upkeep after exactly one normal draw. Checks library
  count decreased by one, the card ordered second is now on top with its original
  ID, and the card ordered first is absent from the new top two.
- Test harness corrections: handle cleanup discard while advancing turns; account
  for the normal draw between scries. Neither was a production defect.
- Final run passed (library-scry-live.log, table 35b1b4ad-0e86-45bb-86ff-b1796c7eea39).
  Inspected library-scry-top-order.png and library-scry-top-order-landscape.png.
  Landscape fits at 667x375; selected-card arrows/Details require inner scrolling,
  with Confirm pinned and visible.
- Previous full npm verify remains current production evidence (1,789 tests/build);
  only the live harness changed this slice. Search/surveil live coverage and the
  remaining full-plan requirements are still open. Nothing committed.

## Live private search and shared acceptance harness

- Extracted reusable LibraryTable into client e2e/helpers/library-table.js: seats,
  auth/decks, hub/view updates, legal turn advancement, captures and cleanup. Both
  verify-library-scry-live.js and verify-library-search-live.js now use it.
- Search test legally plays/activates existing Evolving Wilds, verifies basic-land
  choice cards are private, exercises no-match and matching filters, opens shared
  Details without submitting, and selects Mountain. Verifies it enters tapped,
  the library decreases by one, Wilds is in graveyard, and private cards clear.
- Initial fixture containing Forest was refused for missing engine definition
  b34bb2dc-c1af-4d77-b0b3-a0fb342a5fc6; changed only the test deck to existing
  Mountain definitions. This does not resolve missing definitions generally.
- Final live search passed (library-search-live.log, d83edcfe-c353-4a80-820f-2ce02cac4def).
  Inspected library-search-filtered.png, library-search-details.png and
  library-search-landscape.png; landscape document fits at 667x375.
- Scry regression passed after extraction (library-scry-shared-live.log,
  2ee6b292-09cc-4b25-94bd-44eb5acb1928), including retained server order after draw.
- No production source changed this slice; existing full verify evidence stands.
  Live fail-to-find/shuffle evidence, other search restrictions, surveil live,
  revealed-top timing/identity and remaining plan requirements remain open.
  No commits; objective remains active.

## Search completion when nothing is found

- Added rules regressions for qualified searches with available matches: choosing
  none emits exactly one shuffle when requested, no shuffle with only-if-found
  wording, and no shuffle when disabled. Original card membership/identities remain,
  no card moves, the next instruction executes and resolution completes once.
- Full rules suite: 1,428 passed (library-search-completion.log).
- Extended the shared live search test with --fail-to-find. Browser opens details
  and filters real matches, then chooses none. Both views finish the resolution,
  no land enters, library count stays unchanged, and private choice cards clear.
  Passed in library-search-none-live.log. Engine shuffle is proven by event tests;
  unchanged hidden counts alone are not treated as browser proof of randomization.
- API restored after tests. Production sources unchanged; prior client verify
  remains current. Still need search replacement interruption coverage, other
  search restrictions, surveil live and remaining full-plan work. No commits.

## Search replacement interruption fix

- Actual-engine regression reproduced premature shuffle while a selected move was
  waiting on replacement order (library-search-interrupt-red.log).
- Moved search completion into EffectInterpreter.Search. Selection, original library
  membership and arrived objects use existing remembered-state persistence. Each
  move halts correctly; shuffle runs through shared proposal continuation only
  after movement completes. Removed the former Found implementation.
- Resume returns to saved search selections instead of asking/searching again.
  Tests cover both selected moves being replaced, and one card moving before a
  later replacement pause. The latter verifies the arrived card remains remembered
  for a following Exile instruction, with no repeated move or lost continuation.
- Full rules suite 1,430 passed (library-search-interrupt-green.log); API suite
  786 passed (library-search-interrupt-api.log).
- API restarted (parent 43688, listener 4756 observed). Initial live run reached
  the proxy before startup finished; rerun after confirming listener passed
  (library-search-after-interruption-fix.log). No client production changes.
- Search restrictions/reveal behavior, top-card timing/identity, live surveil and
  remaining full-plan requirements are still outstanding. No commit.

## Library search prohibitions

- Reproduced CantSearchLibraries being ignored: static prohibition still offered
  private library cards (library-search-prohibition-red.log).
- Added the prohibition to shared PlayerLimits and LayerSystem folding/scoping,
  including resolving effects through interpreter player targeting. Search filters
  out prohibited library access before emitting LibrarySearched or offering cards.
- Other searched zones remain accessible. A wholly blocked library search completes
  without a prompt and still runs its separately requested shuffle/continuation.
- Tests cover static and resolving effects, affected player only, removal of the
  active effect, graveyard-only searches, mixed library/graveyard choices, no search
  event on blocked access, and following instructions.
- Full rules suite: 1,434 passed (library-search-prohibition-green.log). Full API:
  786 passed (library-search-prohibition-api.log). API restarted, launch PID 20152.
- No client changes or live prohibition claim. Limited top-N search restrictions,
  optional-search prohibitions, reveal semantics, top-card timing/identity, live
  surveil and remaining full-plan items remain open. No commit.

## Optional search prohibition

- Reproduced illegal optional-search Yes prompts for both Search with integrated
  shuffle and an optional Sequence beginning with Search then Shuffle.
- AccessibleSearchZones is now the shared access check for mandatory and optional
  searches. A prohibited optional branch records no and skips its shuffle; mandatory
  search retains its separately required shuffle behavior.
- The no answer is carried through the existing answered context, so a later prompt
  does not lose it. Regression verifies a subsequent unrelated May still runs and
  a later ChoiceIs(searched,no) branch sees the automatic decline.
- Full rules suite 1,437 passed (library-optional-search-green.log); API suite 786
  passed (library-optional-search-api.log). API restarted, launch PID 48184.
- No client changes or live optional-prohibition claim. Top-N search limits,
  reveal behavior, top-card timing/identity, live surveil and the remaining full-plan
  requirements remain open. No commit or completion claim.

## Static top-card permissions

- Reproduced static MayLookAtTopCard and PlayWithTopCardRevealed granting no visible
  top card (library-static-top-red.log). Projection previously scanned only active
  temporary effects.
- Both permissions now fold through shared PlayerLimits/LayerSystem, including
  effect-intrinsic player scope. Projection reads one evaluated rule set per seat
  instead of maintaining a separate active-effect permission implementation.
- Static sources that are face down or phased out do not grant player rules.
  Tests verify private owner-only access, public reveal, serialized opponent privacy,
  and revocation when the source leaves, turns face down or phases out.
- Removed the obsolete RevealedTop helper and its incorrect claim that CR 401.6
  needed no implementation. Reveal interruption identity and timing remain open.
- Full rules suite 1,439 passed (library-static-top-green.log); API suite 786 passed
  (library-static-top-api.log). API restarted after tests. No client changes and
  no live static-visibility claim. Remaining full-plan work continues; no commit.

## Empty-library draw replacements

- Reproduced skipped replacements when drawing from an empty library: two replaced
  draws left life unchanged and recorded failed draw attempts instead of effects
  (library-empty-replacement-red.log). CR 121.6a explicitly requires replacement
  even when no card can be drawn.
- ZoneSystem now offers DrawFromEmptyLibraryAttempted to the shared replacement
  system before recording it, stopping if a choice is pending. WouldDraw matching
  recognizes both successful and empty draw proposals, without inventing a card ID;
  affected-player context includes the empty draw attempt.
- Real replacement/interpreter regression verifies two replacement effects complete,
  no failed-draw flag/card movement/draw count occurs, and replay folds correctly.
- Full rules suite 1,440 passed (library-empty-replacement-green.log); API suite 786
  passed (library-empty-replacement-api.log). API restarted after tests.
- Remaining draw issue identified: interpreter Draw arm uses Merge(zones.Draw(count))
  without the shared resumable proposal path. Multi-draw interruption and resumed
  normal draw movement need dedicated tests/design. No claim of complete draw support.
  Other full-plan requirements remain open; no client changes or commits.

## Draw execution rewrite and replacement continuation

- Removed the draw loop/movement implementation from ZoneSystem. Its Draw entry
  point delegates to LibraryDrawCoordinator; movement preparation still uses the
  same zone-change factory as other zone operations.
- The coordinator completes surviving draw proposals with movement followed by
  ObjectDrawn. ReplacementSystem uses that same completion path on resumption.
  A serialized proposal marker distinguishes an unfinished draw from its committed
  event; it carries no additional card contents. Movement uses current object ids
  and library state, rather than reserving an id across a pause.
- Interpreter drawing now uses the shared resumable proposal path for each card.
  It preserves evaluated count and recipients across pauses, uses the existing
  active-player/turn-order helper, and waits before subsequent instructions.
- Five regressions first failed (library-draw-continuation-red.log), then passed.
  Coverage includes empty/nonempty libraries, dynamic amounts changed by earlier
  replacements, serialized choices, fresh service instances, and resumed normal
  draws when replacement candidates no longer apply.
- Full rules suite: 1,445 passed (library-draw-rules.log). API suite: 786 passed
  (library-draw-api.log). Client verification and live evidence are recorded below
  when completed.
- This does not finish all draw semantics: standalone multi-draw continuation,
  nested choices inside replacement effects, draw-count replacement effects,
  draw prohibitions/cost legality, and broader deck/library requirements remain
  to be audited and completed. No commit.

### Draw rewrite live/client verification

- npm run verify passed: 1,789 tests, changed-file coverage gate, production build
  (mtg-client/e2e/screenshots/library-draw-verify.log). Existing 18 lint warnings
  and the 18.05 KB card-modal stylesheet warning remain.
- Two-player live table 5caa264c-0c3c-4ff9-a236-e9d437f0949e passed legal setup,
  private scry/top-order choices, refresh recovery, ordering, and the next normal
  draw consuming the correct card (library-draw-live.log). This is live evidence
  for ordinary draw integration, not a live replacement-effect scenario.
- Inspected library-scry-v2-top-order.png and its landscape capture. Confirm and
  Start over remain visible at 667x375; reorder/details controls require scrolling
  inside the choice area. No visual redesign was made in this draw slice.
- API restored on port 7001 after the final build. No commit.

## Existing-catalog to game-definition bridge

- Reproduced refusal of an allowed manual deck whose card exists in the catalog
  but lacks a generated definition (library-catalog-bridge-red.log).
- Moved the pipeline's type/layout/face-role mapping into PrintedCardMapping in
  MtgEngine.Cards and removed its old implementation. TemplateStage now shares
  printed-face construction with the runtime catalog projection. Rules text stays
  text; this mapping creates no executable abilities or per-card special cases.
- BulkDataService exposes a printed-definition projection built from the same
  source records as its existing catalog index. This is not a separately downloaded
  catalog, database, or mutable card authority. Multi-face source data, variable
  stats and color indicators are preserved; unsupported shapes return no definition.
- GameDefinitionResolver prefers compiled definitions and resolves absent ones from
  that projection only for admitted honour-system games. DeckGate still refuses
  strict-table unverified cards and excluded cards. Missing/unrepresentable source
  data still produces the existing typed startup refusal and reopens the lobby.
- Removed the duplicated missing-definition block from GameSessionService. Definitions
  are snapshotted in existing object-creation events and survive replay without a
  new catalog lookup or generated card file.
- A new restart regression exposed coverage being recomputed from changed generated
  files (library-catalog-restart-red.log). New GameStarted events now persist the
  admitted EnforcedOracleIds, including an explicitly empty manual set. Pre-existing
  logs without that field retain their old reconstruction behavior; they cannot
  retroactively prove the original admission decision.
- Full suites passed: cards 326 (library-catalog-cards.log), pipeline 322
  (library-catalog-pipeline.log), rules 1,445 (library-catalog-rules.log), API 789
  (library-catalog-bridge-api.log). Tests include catalog-only creation, deduplicated
  lookup, compiled precedence, strict refusal, serialized replay without catalog,
  and unchanged manual coverage after a generated definition becomes verified.
- Four live players started the previously blocked Thrasios/Tymna/98 Wastes decks
  without adding any card definitions. Table 8626942d-b901-43a1-bd91-1898135ae9fe
  passed four-player mulligan rounds, private choices, refresh recovery, two-card
  bottom ordering, commander counts, and the first-turn draw. Evidence:
  mtg-client/e2e/screenshots/library-catalog-partners-live.log.
- Inspected library-live-two-card-bottom-order.png and choice-landscape.png from
  this run. They show Wastes art/details and private choices; phone choices scroll.
  This does not claim full automated partner-commander abilities or complete manual
  action coverage. Printing/finish preservation, identity/lifecycle migration and
  the remaining full library plan are still open. No commit.
- npm run verify passed with 1,789 client tests, changed-file coverage and production
  build (mtg-client/e2e/screenshots/library-catalog-verify.log). Existing 18 lint
  warnings and the card-modal stylesheet budget warning remain. API restored.

## Selected printing and finish handoff

- Existing CollectionCard quantities expand into StartingCard selections; commander
  extraction retains the selection. No replacement collection/catalog was added.
- GameObject.DeckSelection survives movement and serialized replay. Copies and tokens
  do not inherit it. Legacy Oracle-only startup logs migrate to selections without
  fabricated printing IDs. This does not establish persistent physical-card identity.
- Visible artwork shares the collection printing-resolution policy, with local-only
  lookups during game updates and Oracle fallback. Client artwork keys distinguish
  two printings of the same Oracle card. Hidden and face-down views omit selections.
- Regression suites passed: rules 1,448, API 794, pipeline 322. Logs:
  library-printing-rules.log, library-printing-api.log, library-printing-pipeline.log.
  API coverage includes session creation and restoration preserving every selection.
- npm run verify passed with 1,790 tests, changed-file coverage and production build
  (mtg-client/e2e/screenshots/library-printing-verify.log). Existing lint and card-modal
  stylesheet warnings remain.
- Live two-player table cbc81cba-e0a4-4729-aa6c-06aa0f07c867 passed private scry,
  refresh, top ordering and the subsequent draw. Inspected the resulting landscape
  capture: art and confirm/start-over controls render; the choice area scrolls.
  Evidence: mtg-client/e2e/screenshots/library-printing-live.log and
  library-scry-v2-top-order-landscape.png. This fixture uses one printing; separate
  selected-printing browser acceptance remains unverified. Finish data is preserved;
  no foil visual effect is claimed. API restored; no commit.

### Selected-printing live acceptance

- Added verify-library-printing-live.js using the shared LibraryTable harness and
  an optional fixture-deck preparation callback. It uses isolated test accounts,
  setting one Mountain printing per player through the existing deck-entry API.
- Table d6376db2-6ec4-4b8e-8700-64320dce3a4c passed owner-hand printing/artwork
  assertions, opponent-hand privacy, refresh recovery and legal land plays. Both
  clients received distinct artwork keys and loaded both selected printing images
  when the lands became public. This closes the selected-printing browser gap above.
- Inspected library-printing-landscape.png: both lands show their different artwork.
  Portrait capture shows the existing rotate-phone guard, not a playable portrait
  board. Both captures passed document-overflow checks. The prior full npm verify
  remains the production-code verification; this slice changes only the E2E harness.
- Live evidence: mtg-client/e2e/screenshots/library-printing-selected-live.log.
  No commit. Persistent card identity and commander cast/damage migration remain
  open: the current CommanderIdentity still keys by owner plus Oracle ID.

## Persistent card identity foundation

- Starting cards now receive a distinct CardInstanceId at their initial creation,
  using the already-unique allocation sequence. Reborn carries that identity while
  allocating a new ObjectId. This adds no parallel mutable registry or new catalog.
- Regression assertions cover distinct identities for same-Oracle copies, retention
  through draw/move/replay, exclusion from copied objects and exclusion from views.
  Full rules suite passed: 1,448 (library-identity-rules.log).
- This is an intermediate migration: old logs have null identity, and commander
  damage/cast histories still use their old surrogate. These must move together with
  replay compatibility and privacy checks before the identity contract is complete.
- API suite passed: 794 (library-identity-api.log). No client code changed in this
  identity slice. API relaunched after testing; no commit.

### Commander histories use individual card identity

- Reproduced the old owner/Oracle collision with two distinct commander objects:
  casting one incorrectly incremented the other's count (library-commander-identity-red.log).
  This is a rules-boundary identity test, not a claim that duplicate commanders pass
  Commander deck admission.
- CommanderIdentity.Of is now the shared key factory for command-zone cast records,
  commander cast queries and damage records, including last-known sources. It carries
  CardInstanceId and original deck Oracle identity so copied characteristics cannot
  rewrite the key. Deterministic lethal-source selection also orders distinct instances.
- Regression checks cover independent cast/damage totals, movement, serialized replay
  and copied Oracle characteristics. The tax-legality fixture now seeds the actual
  identity key. Full rules 1,449 and API 794 passed (library-commander-identity-rules.log
  and library-commander-identity-api.log).
- Objects from older logs without CardInstanceId retain the previous owner/Oracle
  policy. Historical identity backfill remains unfinished; no completion claim for
  the full lifecycle contract. No client changes or commit in this slice.

### Player departure shares the zone lifecycle

- Historical-log audit exposed a second rebirth implementation in PlayerLost: a
  departing controller exiled another player's card while dropping DeckSelection
  and CardInstanceId. A three-player replay regression reproduced the missing ID
  (library-concession-identity-red.log).
- Removed that duplicate object construction. PlayerLost now uses the same Reborn
  method as ordinary zone movement, retaining only persistent card fields and
  resetting object-local state through one implementation.
- Full rules suite: 1,450 passed (library-concession-identity-rules.log). The new
  test checks a fresh ObjectId, stable card identity, printing/finish and original
  owner after serialized replay. Historical identity backfill remains next; this
  fixes the live and replayed departure path, not that separate migration.

## Library-only continuation: standalone multi-draw

- Reproduced a lost remainder when ZoneSystem.Draw(2) paused for a replacement
  choice: answering completed the first draw and forgot the second
  (library-standalone-draw-red.log).
- LibraryDrawCoordinator now persists remaining draw batches in choice context.
  Choice resumption delegates back to that coordinator; a further pause retains
  remaining batches. The existing per-card draw/replacement path remains shared.
- ChoiceSecrets strips the continuation from player-visible payloads. Regression
  tests serialize/reload between answers and verify both replacements finish once,
  no cards are drawn when both draws are replaced, and the log reconstructs the pause.
- No new card definitions, commander changes or unrelated lifecycle work in this slice.
- Full suites passed: rules 1,451 (library-standalone-draw-rules.log), API 794
  (library-standalone-draw-api.log). No client changes; this interrupted-draw scenario
  is verified at engine/API-test level, not a new live browser scenario. API relaunched.

## Library-only continuation: interrupted milling

- Reproduced overwritten replacement prompts and skipped movement when milling two
  cards (library-mill-red.log). The old mill loop continued after a pause and rebuilt
  its top-card list from the changed library when resumed.
- Milling now snapshots its selected top cards once and delegates to the existing
  resumable Move path. This removes its separate movement loop and retains actual
  arrivals for RememberAs across replacement pauses.
- Extended shared library replacement regressions with milling, a first card moving
  before the next pauses, serialized pending-choice recovery, and milling five with
  only two cards available. Assertions ensure milling does not count as drawing or
  set the empty-draw loss flag. This is targeted continuation coverage, not completion
  of all milling semantics or a live browser claim.
- Full rules 1,454 and API 794 passed (library-mill-rules.log, library-mill-api.log).
  No client changes; API relaunched. Nothing committed.

## Library-only continuation: bounded search

- Search now accepts optional Top, evaluated before matching library cards. Omitting
  it preserves whole-library searches; it does not limit any other searched zone.
  This is generic library vocabulary, not a named card implementation.
- Reproduced the old behavior offering a matching creature beneath the searched
  top card (library-top-search-red.log). Regression checks cover limiting before
  filtering, zero/one/oversized bounds, normal shuffle completion and searcher-only
  ChoiceCards projection. Definition/card-file schemas regenerated for the new field.
- Full suites passed: rules 1,458, cards 326, pipeline 322 and API 794
  (library-top-search-{rules,cards,pipeline,api}.log). No client changes or live
  bounded-search browser scenario in this slice. Nothing committed.

## Library-only continuation: draw limits

- Existing CantDrawMoreThan vocabulary had no reader in player restrictions or
  draw replacement processing. Three regressions reproduced extra draws and an
  empty-library failure despite a zero draw limit (library-draw-limit-red.log).
- Player restrictions now combine applicable draw limits using the smallest cap.
  Static amounts evaluate in their source context; resolved effects use the existing
  amount-freezing path and now target their specified players. Draw and empty-draw
  proposals are blocked before replacement candidates when the cap is reached.
- Tests cover zero/one caps, empty-library prohibition, resolved amount freezing,
  player scope and removal of the resolved effect. Existing conditional-static
  limitations remain; no claim of every possible draw prohibition or cost rule.
- No draw-cost vocabulary was found, so draw-cost support remains unimplemented.
  No new named card definitions or client changes in this slice.
- Full rules 1,462 and API 794 passed (library-draw-limit-rules.log and
  library-draw-limit-api.log). API relaunched; nothing committed.

## Library-only continuation: required search reveals

- Source audit found Search.Reveal unused and the general Reveal action only
  remembering internal objects. Both now use one RevealCards helper and a persisted
  CardsRevealed event. Required search reveals occur once before selected movement.
- LastReveal projects public historical card details, with no ObjectId,
  CardInstanceId or zone. It does not grant access to current hidden objects or the
  rest of the library. The event vocabulary snapshot includes the new event.
- Tests compare revealing/nonrevealing searches, event ordering, serialized replay,
  opponent hand privacy and the absence of tracking fields in the disclosure shape.
  Full rules 1,464 and API 794 passed (library-reveal-rules.log, library-reveal-api.log).
- This is the server half only: client reveal presentation/artwork, live verification
  and private LookAt disclosure are unfinished. No claim of a visible UI feature yet.
  No named card definitions or unrelated changes. Nothing committed.

### Library reveal board integration

- Added a Last reveal control using the existing card-detail modal. Transport and
  the existing view adapter carry historical details separately from live objects;
  no game object id is manufactured. Reveal artwork uses the same authorized local
  lookup as other visible cards, including selected printings.
- Standing harness verify-library-reveal-live.js uses two real players and an
  existing Expedition Map definition. Table 0bc3f426-6114-486b-9536-d66098673b61
  passed selected-card disclosure, remaining-library privacy, both-player detail
  opening and refresh. The first run's modal selector assumed it stayed inside the
  reveal component; the existing modal portals to body. Corrected the selector and
  reran successfully.
- Inspected library-reveal-landscape.png: the shared detail modal displays the
  revealed Mountain artwork, with content vertically scrollable at 667x375.
  This is a historical reveal panel; it does not expose current library contents.
- API tests: 795 passed (library-reveal-ui-api.log), including reveal printing
  lookup without a live object. Client verification result recorded when complete.
  Private LookAt disclosure and the broader library plan remain unfinished.
- npm run verify passed: 1,793 tests, changed-file coverage and production build
  (mtg-client/e2e/screenshots/library-reveal-verify.log). Existing lint warnings and
  the 18.05 KB card-modal stylesheet budget warning remain. API restored; no commit.

## Library-only continuation: search placement after shuffle

- Reproduced search-to-library top/bottom placement failures: the selected object
  was recreated and then shuffled away from the requested end
  (library-search-placement-red.log).
- Found now retains same-library selections without a zone-change event and stores
  requested placements across pauses. After shuffling, the shared LibraryOrder
  arranges those cards at the requested end. Cards arriving from other zones still
  use the existing zone-change path.
- Full rules 1,466 and API 795 passed (library-search-placement-rules.log and
  library-search-placement-api.log). Tests check selected object identity, exact
  top/bottom position, unchanged count, one shuffle and one reveal.
- Live two-player table fec25fce-a27c-4f85-ab7e-bfe3761c6216 used the existing
  Worldly Tutor definition, revealed Llanowar Elves and drew it on the caster's
  next normal draw. Remaining search choices stayed private. Evidence:
  mtg-client/e2e/screenshots/library-top-search-live.log and inspected
  library-top-search-after-draw.png. Capture inspection found a garbled separator in
  the Last reveal caption; corrected it, passed formatting and reran the live scenario
  successfully at fe6dbf51-688e-4f66-9df0-1af49d057203. Reinspected the corrected
  capture. No functional client changes; no additional full client verify for the
  caption-only correction. Nothing committed.

## Library-only continuation: manual draw and shuffle

- The own-library viewer now offers Draw a card and Shuffle library only when the
  server explicitly permits honour-system actions. Opponent library controls are
  absent; hidden library contents stay private. Existing card/deck models are reused.
- ManualLibraryCoordinator delegates to the existing zone draw/shuffle operations.
  The hub and session enforce table permissions; pending choices, opening hands,
  finished games and lost players block these actions. Persisted request IDs prevent
  repeated operations after a retry or replay. The client retains IDs after uncertain
  transport failures and does not mutate state optimistically.
- Backend verification: 802 API and 1,483 rules tests passed in
  library-manual-api.log and library-manual-rules.log.
- Live two-player table aa4f3272-e5a6-4216-90bc-909ba85fe8eb passed draw/shuffle,
  owner-only controls, duplicate-request refusal and refresh. Evidence is in
  mtg-client/e2e/screenshots/library-manual-live.log. Desktop, portrait and landscape
  captures were visually inspected; controls fit at 375x667 and 667x375.
- npm run verify passed with 1,798 tests, changed-file coverage and production build
  (mtg-client/e2e/screenshots/library-manual-verify.log). The initial run caught a
  test-call signature error, corrected before the successful rerun. Existing lint
  warnings and the 18.05 KB card-modal stylesheet budget warning remain.
- This completes the manual draw/shuffle slice, not the entire library plan.
  No commit was made. Scope remains strictly library behavior, controls and privacy.

## Library-only continuation: inspect all permitted search cards

- Local rules 701.23a require inspecting the searched zone, not only cards matching
  the selection filter. Two new regressions reproduced missing private disclosures
  (library-search-inspection-red.log).
- LibrarySearchPlanner now separates Inspected cards from selectable Candidates.
  Both use the same permitted-zone/bounded-top plan, before filtering. Search emits
  a private CardsLookedAt snapshot once at entry, reusing existing look projection,
  artwork and detail presentation. Nonmatches remain unavailable for selection.
- Rules 1,485 and API 802 passed (library-search-inspection-rules.log and
  library-search-inspection-api.log). Tests cover another player's library,
  top-limited inspection, nonmatches, serialized disclosure privacy and no repeated
  look on answer. The first API build hit a running development API's DLL lock;
  stopped that process, reran successfully and restored the API.
- Live two-player table 49505375-a424-44c5-a906-cf19e73dfffb passed private inspection
  of all 53 remaining cards, selection restricted to Mountain, refresh, filtering,
  detail viewing and destination/count checks. Evidence: client
  e2e/screenshots/library-search-inspection-live.log.
- Live pointer tracing exposed sidebar overlap with a full-library list. Inspection
  now has bounded scroll height; the existing stack scroll containment applies at
  desktop sizes too. A stable disclosure key preserves open panels across view
  updates, covered by a component regression. Final live run uses ordinary clicks
  and asserts the stack ends before the inspection summary begins.
- Inspected desktop, 375x667 and 667x375 captures. Portrait shows the existing
  rotate-phone screen. Landscape avoids page overflow, but the narrow sidebar
  requires scrolling to inspect the full list; a roomier library inspection UI
  remains desirable. Do not claim full mobile-library acceptance from this capture.
- Audit also confirmed Search.Who still selects only FirstPlayer: simultaneous
  multi-player searches (701.23i), full search-coordinator ownership and broader
  library acceptance remain open. No named card definitions changed. No commit.
- Final npm run verify passed: 1,799 tests, changed-file coverage and production
  build (client e2e/screenshots/library-search-inspection-verify.log). Existing lint
  warnings and the 18.05 KB card-modal stylesheet budget warning remain.

## Relationship to the MTGO reconstruction

### Required acceptance standard: method traceability

The first source-level selection audit is recorded in
research/mtgo/LIBRARY_METHOD_PARITY.md. It identifies matching membership/count
invariants, different invalid-input behavior, and unresolved single-selection
submission behavior. These are bounded findings, not a full parity verdict.

Every library capability needs a recovered MTGO method mapping where evidence
exists. Matching architecture or passing our own tests is not proof of parity.
The initial mapping below is a responsibility mapping, not an equivalence verdict.

| Recovered MTGO method | Our corresponding responsibility | Acceptance evidence still needed |
| --- | --- | --- |
| Game.ProcessPlayerStatusElement | PlayerView.LibraryCount and player view projection | Compare library count transitions for draw, search, shuffle and mulligan against the recovered assignments and recorded MTGO scenarios. |
| Game.ProcessInteractStateElement | PendingChoice, choice projection and client prompt controls | Trace library prompt types, chooser, available buttons, limits and completion through both implementations. |
| GameCard.ResolveCardActionsHelper | Offered library contextual actions | Inventory each library action and its enablement conditions; identify missing controls explicitly. |
| GameCard.ResolveActionTargets; TargetSet.AddTarget | LibrarySearchPlanner.Plan; ChoiceSystem.Validate and client selection | Verify offered membership, minimum/maximum, deselection and submission behavior case by case. Target selection is not automatically identical to library search selection. |
| GameAction.Execute; CardAction.GetMessage | Command submission and AnswerChoice | Trace selected IDs, current choice/state identity, rejection and resulting refresh. MTGO stale-request rejection is not established by the timestamp field alone. |
| Game.ProcessInteractStateElement mulligan branches | TurnSystem mulligan flow and client opening-hand prompt | Compare keep/mulligan decisions and ordering prompts. Client prompt code does not establish the server's mulligan algorithm. |
| No server method recovered | LibraryDrawCoordinator.Draw; LibrarySearchPlanner; LibraryOrder; shuffle and mulligan execution | Validate against published rules and our replay/privacy tests. MTGO method parity remains unknown until additional evidence is available. |

Evidence baseline: research/mtgo/ACTION_STATE_AND_CARD_FINDINGS.md and its
saved reconstructions, client build 3.4.159.4708; gameplay member evidence
ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265.
This is static evidence, not an observed MTGO game session.

For each row, retain the exact source method/location, our method/location,
inputs, preconditions, state transitions, visibility, failure behavior and tests.
Use verdicts: equivalent demonstrated, intentional divergence demonstrated,
gap, or unknown. No row currently establishes complete library parity.

A divergence requires a concrete benefit and supporting comparison: fewer
duplicated policy implementations, a reproduced correctness defect fixed,
measured latency/allocation improvement, or a demonstrated privacy/replay
guarantee. Cleaner names, more classes and SOLID terminology alone are not
evidence that our implementation is better. Improvements verified only against
our previous code must be labeled as such, not as improvements over MTGO.
Preserve observable behavior when consolidating responsibilities; one-to-one
traceability does not require copying MTGO's class or method count.

The library implementation follows the client/server boundary observed in
research/mtgo/ACTION_STATE_AND_CARD_FINDINGS.md. It is not a reconstruction of
MTGO's private server classes or algorithms. That investigation recovered outgoing
NewCardActionMessage/PromptActionMessage requests, incoming zone/count/interaction
updates, action menus built from received metadata, and local selection checks.
It did not recover authoritative draw, search, shuffle or mulligan implementations.

- ChoiceSystem/AnswerChoice correspond to the decision/validation responsibility;
  our choice IDs and validation policy are our implementation, not MTGO's protocol.
- ViewProjector and GameHub provide player-authorized state and choice messages.
  Private projection is our security design; the MTGO notes recommend it without
  claiming to have recovered its exact server visibility policy.
- LibrarySearchPlanner, search continuation, LibraryOrder, draw and opening-hand
  coordinators implement published Magic rules independently using existing engine
  seams. Their names, class boundaries and algorithms are not recovered MTGO code.
- Exact library UI parity and method-for-method equivalence have not been proven.
  Architecture alignment must not be reported as that stronger claim.

## Library-only continuation: multiple searchers

- The source audit found Search.Who consumed through FirstPlayer. Searching now
  evaluates all affected players in APNAP order, freezes each permitted plan and
  sends every authorized private inspection before the first selection prompt.
  Single-player searches use the same path. SearchPlanner continues to own zone,
  candidate and bound decisions; no second implementation of those policies.
- All selections are gathered before movement. Per-player movement progress stays
  until the whole instruction finishes; shuffles follow the selected movements.
  Plans persist through pending choices and are stripped from projected choice
  metadata alongside internal remembered IDs. This is our independent rules
  implementation, not recovered MTGO server code.
- Four-seat tests serialize/replay every answer with new subsystem instances. They
  cover APNAP starting at seat 2, private full-library looks, matching-only options,
  one player finding nothing, and a competing replacement prompt after earlier
  players have already moved cards. Resume does not repeat those moves or shuffles.
  The initial regression invocation stopped at the formatting analyzer, so its
  red log is not evidence of a behavioral failure. After formatting and implementation,
  all 1,488 rules and 802 API tests passed (library-multiplayer-search-rules.log,
  library-multiplayer-search-api.log).
- Extended the shared live LibraryTable harness with an optional capacity (default
  remains two). Direct choice payload assertions now also reject private search-plan
  metadata. No client production source or named card definitions changed here.
- Four live browsers at table 75527548-b25a-4856-a3b2-764d5eaf9c34 legally played and
  activated the existing Field of Ruin. All four searched, refreshed at their own
  prompt, selected a Mountain, and received exactly one additional land. Counts
  stayed unchanged until the last selection. Direct pushes matched private views.
  Evidence: client e2e/screenshots/library-multiplayer-search-live.log; inspected
  library-four-search-choice-landscape.png and library-four-search-completed.png.
- Two-player decline regression passed at 278cd9f3-53a0-4a74-9f7e-d717d8a09ab0
  (library-multiplayer-search-decline-regression.log). Final npm run verify passed
  with 1,799 tests, changed-file coverage and production build
  (library-multiplayer-search-verify.log). Existing lint/style budget warnings remain.
- This proves the multiple-searcher choice/inspection/continuation path, not full
  simultaneous movement/trigger semantics for every effect or shared-library
  contention. Those semantics, full search-coordinator extraction, and the complete
  library acceptance audit remain open. Nothing committed; API restored.

## Library-only continuation: generic reveal-until operation

- Audit found RevealUntil in the existing effect vocabulary with no interpreter
  handler. After correcting the regression fixture's missing namespace import,
  eight cases failed with the missing-handler refusal (library-reveal-until-red.log).
- LibraryRevealPlanner is a read-only class depending on IQuery. It scans a named
  library from the top, stops at the first match or optional maximum, and returns
  the revealed prefix, matching card and preceding cards. It never moves, draws,
  shuffles or owns a second card collection.
- The interpreter delegates to that planner, uses the existing public disclosure
  helper, and remembers hit/rest groups for subsequent existing movement effects.
  Multiple affected libraries are included in one public historical snapshot.
  No vocabulary changes, named card edits, collection/deck model work or new UI.
- Ten regression cases cover zero/negative limits, bounded/unbounded scans, no hit,
  empty library, named opponent/multiple libraries, unchanged order/identity during
  reveal, public replayed snapshots, and remembered groups surviving a later prompt.
  Resuming does not reveal again or create a draw event/empty-library draw failure.
- Full rules: 1,498 passed; API: 802 passed (library-reveal-until-rules.log and
  library-reveal-until-api.log). API restored after testing.
- The existing two-player public-reveal UI regression passed at
  faf696c3-0204-4b38-bf40-519179dfcfa3; both players refreshed and opened card details.
  Inspected library-reveal-landscape.png. Evidence: client
  e2e/screenshots/library-reveal-until-ui-regression.log. This scenario uses the
  existing search reveal and validates the shared display, not RevealUntil itself.
  Live end-to-end RevealUntil remains unverified; tests establish its engine/replay
  behavior. No new npm run verify for this server-only slice; latest client gate
  remains library-multiplayer-search-verify.log (1,799 tests and production build).
- This does not establish complete behavior for cards that compose RevealUntil
  with optional draw replacement, casting or arbitrary-order follow-up movement.
  Active reveal lifetime/identity rules, search continuation audit and the remaining
  library plan are still open. Nothing committed.

## Interrupted library-payment failure reproduction — 2026-10-23

LibraryMillCostTests.Unpayable_later_library_cost_leaves_internal_activation_stuck_at_the_replayed_choice
now characterizes the unresolved failure. A synthetic ability pays one life,
mills one card through competing replacement choices, then owes another mill
with only one card initially available. The internal BeginActivationPayment path
commits the life payment before pausing. After serializing/replaying its events,
every offered answer fails with rule 701.17b on the later cost. The caller retains
the same pending choice, 19 life, and no stacked ability.

This is evidence of an internal continuation limitation, not a passing acceptance
test for recovery. Normal Activate still disables pausing and is not changed.
Recovery must address the already-committed pre-pause events, not merely catch
the later exception or clear the pending choice. No real card definitions changed.

The focused reproduction passed (library-payment-failure-characterization.log).
The whole LibraryMillCostTests class also passed
(library-payment-failure-regression.log). These tests characterize current
behavior; they do not establish rollback or completion of interrupted payment.

## Recovery constraints and implementation boundary — 2026-10-23

Checked the official Comprehensive Rules effective September 25, 2026:
https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt
Rules 733.1–733.2 require reversal of an incomplete illegal action and cancellation
of its payments, but prohibit reversing certain library movement, shuffle and
reveal operations. Undone operations do not produce triggers/effects, and the
prior priority holder retains priority. Consequently a blanket initial-state
snapshot restore is not a valid general solution.

Current-source findings:
- ActivationPayment.History is a flat event list. It has no payment-step
  boundaries or parent/child operation provenance.
- CostPayment.Begin evaluates each cost into an immutable intermediate state,
  then accumulates its events. A later thrown refusal does not return the
  successfully completed intermediate steps to its caller.
- ResumeActivationPayment calls CostPayment.Resume directly; its exception
  escapes before completion or a recovery result can be emitted.
- Game.Settle records deferred trigger occurrences across pending choices.
  Recovery must reconcile those occurrences with retained versus undone actions;
  blindly clearing or firing them is not sufficient.
- InitialLibraryTops is disclosure gating data, not a recovery journal.

Implementation sequence (proposed local design, not recovered MTGO internals):
1. Extend the existing payment progress contract to return typed failure with
   the successful prefix and exact failing step, while preserving Pay's existing
   atomic refusal contract for ordinary callers. Do not catch arbitrary exceptions.
2. Record payment-step provenance in the existing serialized continuation. Keep
   announcement data and private recovery data out of viewer projections.
3. Define a recovery planner over those operations, with explicit handling for
   library actions that cannot be reversed. Do not infer reversibility solely
   from an event's class or introduce inverse gameplay events that retrigger effects.
4. Apply recovery through replayable state events and reconcile deferred trigger,
   disclosure, object identity and priority state. Test failures before and after
   library movement, repeated pauses, source departure and fresh-service replay.
5. Only then enable the normal command entry point and verify a real game,
   including independent viewer privacy and reconnect. Spell payment integration
   remains a separate unfinished path.

The immediate prerequisite is step 1: the existing flat exception contract loses
the completed prefix on the answer that fails. The regression added previously
is a characterization of this limitation, not proof of compliant recovery.
No runtime behavior changed during this rules/source audit.

## Typed failed-payment progress — 2026-10-23

CostPayment.Begin now returns CostPaymentFailure for a rule refusal at an
individual step. The result preserves the completed event prefix/state, failed
cost, and unpaid queue (including the failed cost), with named-object choices
captured before that step. Only RefusedException is converted; unexpected
exceptions still escape. Resume uses the same path.

Pay, BeginActivationPayment and ResumeActivationPayment explicitly rethrow failed
results through ThrowIfFailed, preserving existing caller behavior until the
recovery planner is implemented. The new result is internal and is not itself
committed or projected as a successful payment.

A regression pays life, completes one mill, fails the next mill, and checks the
exact remainder plus serialized replay of completed events. It also verifies
ordinary Pay still refuses and leaves the caller's initial state unchanged.
Full rules: 1,597 passed. API: 809 passed. Logs:
library-payment-failure-result-rules.log and library-payment-failure-result-api.log.
Payment-step provenance and compliant recovery remain unfinished; normal gameplay
still does not enable interrupted activation payments.

## Payment step records — 2026-10-23

CostPaymentProgress now carries ordered CostPaymentStep records: the attempted
cost, its emitted events and whether it finished. A paused step is explicitly
incomplete. A refused step is represented by Failure and the unpaid queue rather
than being mislabeled completed. Expanded ChooseCost containers contribute their
executed leaf costs. The records make no claim that completed actions can be
reversed.

Existing regressions now check event ownership for life versus library movement,
failure-prefix event concatenation, paused-step status, and the distinct costs
executed after Resume. These are per-run internal records, not yet a durable
cross-choice recovery journal. Persisting step provenance through replacement
continuations, attaching replacement-completion events, and implementing the
recovery planner remain unfinished. No normal command behavior was enabled.
Validation: 1,597 rules tests and 809 API tests passed (library-payment-step-records-rules.log and library-payment-step-records-api.log).

## Serialized activation payment journal — 2026-10-23

ActivationPayment now retains CostPaymentStep records through replacement
choices. Begin captures the initial steps; Resume attaches operation events to
the current incomplete step, marks it complete only after that operation has no
pending choice, and appends subsequently paid steps. The existing SpentOn and
PaymentHistory helpers normalize both flat history and step events. Choice
control events are excluded to avoid recursively embedding continuation records.

Repeated replacement-pause tests serialize/replay every answer with fresh
subsystems, check the journal's concatenated events equal the flat history,
verify only the last step is incomplete, and verify the earlier mill and source
sacrifice retain separate records when the next mill pauses. Both player
projections continue to strip RemainingPayment including its journal.

Older continuations default to an empty journal. They retain their existing
completion behavior; that default must not be mistaken for proven recovery
provenance. No recovery planner is implemented and normal gameplay still refuses
interrupted payments. Failure handling must still consume the preserved prefix
rather than losing it when the existing callers rethrow.
Validation: 1,597 rules and 809 API tests passed (library-payment-journal-rules.log and library-payment-journal-api.log).

## Activation-level failure progress — 2026-10-23

ResumeActivationPaymentProgress now preserves the completed replacement operation,
subsequent successful costs, updated step journal and unpaid costs when a later
cost fails. It returns the typed refusal without pretending the activation
completed or reattaching a failed cost as an ordinary replacement choice.
ResumeActivationPayment remains the compatibility wrapper and still rethrows.

The existing stuck-payment regression now also finishes the replacement operation
without automatically continuing activation, then inspects the new internal result.
It checks the moved library card, retained life payment, empty stack, complete
journal steps, failed remainder, matching flat history and serialized replay.
This supplies recovery input; it does not itself recover the game. Normal
activation pausing remains disabled pending a compliant recovery planner.
Validation: 1,597 rules and 809 API tests passed (library-activation-failure-progress-rules.log and library-activation-failure-progress-api.log).

## Recovery-input audit — 2026-10-23

Current code establishes additional prerequisites before applying recovery:

| Inspected implementation | Finding | Required recovery invariant |
|---|---|---|
| ObjectChanged.Apply | Events carry new field values, not previous values. | Capture prior values for affected fields; do not guess that undoing a tap always means untapping. |
| ObjectMoved.Apply | Movement updates object identity, entry/death/departure history and commander-return candidates. | Restoring a reversible move must restore the affected state without executing a second gameplay move or generating new triggers/history. |
| PendingTrigger | Stores source, ability, subject and controller but no payment-step origin. | Distinguish triggers caused by retained operations from triggers caused by undone operations. Source identity alone is insufficient. |
| Game.Settle | Detects and retains trigger occurrences while a replacement choice is pending, then collects them when pending clears. | Recovery must reconcile already-deferred occurrences as well as the current command's events. |
| Game.Execute refusal handling | Returns Refused and discards the attempted outcome; it does not commit recovery. | A completed recovery must be an explicit replayable outcome, not an exception carrying a hidden mutation. |

Consequently the current step journal is necessary but insufficient for a general
recovery planner. Next capture prior state for the specific fields changed by
each payment operation and preserve its trigger provenance. Do not implement
recovery by publishing opposite LifeChanged/ObjectMoved events, clearing all
deferred triggers, or restoring the whole initial game snapshot. The former can
create new gameplay consequences; the latter can erase retained library actions.

Acceptance must include a source that was already tapped/modified, a reversible
sacrifice with history and commander bookkeeping, two triggers from the same
source caused by different retained/undone operations, and serialized resumption.
These are newly identified proof obligations from the current source audit, not
claims that those recovery behaviors are implemented or tested.

## Prior-value capture for payment steps — 2026-10-23

CostPaymentStep.Capture records before/after PlayerState and GameObject values
for entries changed by a cost's initial invocation. It records creation/removal
with null sides and excludes unchanged entries. Existing step records carry these
values privately in ActivationPayment; old serialized records default to empty
change lists and must not be treated as complete recovery evidence.

Tests verify the life delta's actual prior value, the library object's removal
and graveyard object's creation, and the life records surviving repeated
serialized replacement-choice replay. This is not a general rollback snapshot:
zones, history, commander candidates, later replacement-operation changes and
trigger provenance still need capture/reconciliation. No recovery is enabled.
Validation: full rules 1,597 passed; after the extra replay assertions, all 16 library mill-cost tests passed; API 809 passed. Logs: library-payment-prior-values-rules.log, library-payment-prior-values-replay.log, library-payment-prior-values-api.log.

## Replacement-continuation prior values — 2026-10-23

ChoiceSystem now passes the state before replacement resumption to casting
through a forwarding-compatible overload. The active payment step merges actual
player/object changes from that operation, keeping the earliest before-value and
latest after-value for each entry across pauses. Existing event recording remains
separate; it is not duplicated by the state-change capture.

Repeated serialized replacement tests now verify that a mill completed after a
choice records the removed library object and created graveyard object before
the later mill pauses. Player projections still omit the private continuation.
The old overload remains compatible but cannot supply before-state evidence;
recovery must not infer completeness for calls that omit it.

The interface-decorator audit initially caught missing forwarding in the scenario
test recorder. Added forwarding and reran: 1,597 rules and 809 API tests passed
(library-payment-continuation-values-rules.log and library-payment-continuation-values-api.log).
Zone/history/commander metadata and trigger provenance are still unfinished;
this change does not enable recovery or interrupted normal activations.

## Movement bookkeeping in payment journal — 2026-10-23

CostPaymentStep now captures changed zone contents (including absent versus
empty entries), turn-history before/after values, and commander-return candidate
sets. Replacement continuation merges these records using earliest before and
latest after values, as it already does for player/object changes. These are
private observations, not instructions to restore a whole zone or history:
recovery must preserve unrelated/irreversible operations.

Repeated replay assertions verify library counts/order snapshots for the
completed first mill and the source sacrifice's departure history before the
second mill pauses. Commander-candidate capture is implemented but its dedicated
nonempty transition acceptance remains outstanding. Trigger-origin tracking and
the actual recovery application are also unfinished. Normal interruption remains
disabled.
Validation: 1,597 rules and 809 API tests passed (library-payment-movement-journal-rules.log and library-payment-movement-journal-api.log).

## Commander and tap-state journal acceptance — 2026-10-23

A new synthetic-cost regression begins with a tapped commander source, untaps
and sacrifices it, then pauses at a library mill replacement choice. After
serialized replay it verifies: original tapped state versus paid untapped state,
the sacrifice's prior/current battlefield membership, departure history retaining
commander designation, and the new commander-return candidate. Both viewers'
projections omit RemainingPayment and its journal.

This closes the dedicated nonempty commander-candidate capture test noted above.
It verifies recovery inputs, not actual reversal, commander return resolution,
or a live interrupted-payment flow. All 17 LibraryMillCostTests cases passed
(library-payment-commander-journal.log). Test-only change; production recovery
and trigger-origin tracking remain unfinished.

## Deferred trigger causes — 2026-10-23

PendingTrigger now retains its causing GameEvent and the event's index within
the detection batch. TriggerSystem.Fire supplies both for ordinary and delayed
triggers. Old serialized records default to null. CauseIndex is explicitly not
a global event-log ID and must not be compared across batches without batch
provenance.

A new regression serializes two identical library-search occurrences from the
same watcher and verifies distinct batch positions. Existing search-pause replay
tests verify the retained LibrarySearched cause and continue to check that both
player projections strip deferred triggers. This identifies occurrences within a
batch; linking them durably to payment-step IDs across batches remains required.
No trigger filtering or payment recovery is enabled by this metadata.
Validation: 1,599 rules and 809 API tests passed (library-trigger-cause-rules.log and library-trigger-cause-api.log).

## Trigger-to-payment linkage boundary — 2026-10-23

Source inspection identifies the required transport for the next change:
Game.Apply currently passes only Outcome.State and Outcome.Events into Settle.
Settle builds its detection batch by removing ChoiceRequested/ChoiceAnswered.
CostPaymentStep.Events similarly excludes those control events in the persisted
activation journal. However, the journal spans several command batches while
PendingTrigger.CauseIndex addresses only one detection batch.

Do not join a trigger to a payment step using event equality, reference equality,
the last matching event, or only a count-based suffix. Equal life/mana events
can occur in multiple steps; serialization changes reference identity; callers
can settle events unrelated to the retained payment frame. These joins could
cause recovery to discard the wrong trigger.

Required implementation boundary: execution emits an explicit event-position to
payment-operation/step mapping alongside its Outcome, and Game.Apply forwards
that provenance through its control-event filtering into trigger detection.
Persist the resulting operation/step identity on deferred triggers. Preserve
preexisting trigger provenance when adding occurrences from later commands.
Recovery must treat legacy or unmapped triggers as unknown, never infer a safe
undo from missing metadata.

Acceptance: identical events from two costs within one batch, identical events
across two replacement-choice batches, unrelated events while a payment frame
exists, and serialized deferred-trigger replay. The existing identical-search
test proves batch positions survive; it does not prove this cross-batch link.
No runtime changes were made in this linkage audit.

## Outcome provenance transport — 2026-10-23

Outcome now has an explicit PaymentOrigins map keyed by event position.
PaymentEventOrigin identifies the interrupted activation by its first replacement
choice ID and the journal step by index. Outcome.Then preserves earlier entries
and offsets following entries by the actual event count; unmapped events remain
unknown. This avoids equating identical event values with identical operations.

The composition regression combines identical life events from different steps
and activations, an unrelated choice-control event, and an empty outcome. It
verifies the distinct positions/origins and immutability of the original map.
Full rules: 1,600 passed (library-payment-outcome-provenance-rules.log).
This is the transport contract only: payment execution must populate it and
Game/trigger detection must consume it before cross-batch linking is implemented.
Manual reconstruction of Outcome at other call sites also requires auditing for
metadata preservation along that path. No recovery behavior is enabled.
API verification: 809 passed (library-payment-outcome-provenance-api.log).

## Explicit payment-trigger linkage — 2026-10-23

Interrupted activation execution now assigns event-position provenance directly
from the cost-step boundaries. The first replacement choice ID becomes the
persisted journal identity. Resumed replacement events retain the interrupted
step identity; subsequently paid costs receive their later journal indices.
Choice control events stay unmapped. Retaining a pending payment preserves
Outcome metadata, and Outcome.Then offsets it through choice-answer composition.

Game.Apply forwards those origins into Settle, offsetting legacy deferred events.
Settle explicitly translates filtered trigger-batch positions back to original
event positions and attaches PaymentOrigin to detected deferred triggers.
Existing deferred origins remain unchanged; absent mappings stay unknown.

The regression pays identical life costs at steps 0 and 1, pauses at a mill,
answers through Game.Apply with fresh subsystems, pays identical life at step 3,
and pauses again. Serialized events retain trigger origins 0, 1, 3 with one
activation identity despite different choice IDs. Both views withhold deferred
trigger data. This verifies the explicit cross-command linkage for this internal
activation path, not recovery filtering/application or all possible callers.
Normal activation interruption remains disabled.

Rules: 1,601 passed (library-payment-trigger-link-rules.log).
API: 809 passed (library-payment-trigger-link-api.log).

## Exact-origin trigger filtering — 2026-10-23

PaymentTriggerRecovery.Retain removes deferred triggers only when their complete
PaymentEventOrigin belongs to an explicit set of reversed operations. It keeps
other steps in the same activation, equal step indices in other activations,
and unknown/legacy origins, preserving order. It does not decide which operations
are reversible and does not mutate game state.

A serialized regression uses identical causes and the same watcher across all
four cases, verifies the exact retained origins, checks the input is unchanged,
and checks repeated/empty filtering. Full rules: 1,602 passed
(library-payment-trigger-recovery-rules.log).

The helper is not yet called by gameplay: the recovery application must first
produce an authoritative reversed-operation set and reconcile current-command
trigger occurrences as well as already-deferred triggers. Unknown provenance
remaining in the list is not evidence that a whole recovery is safe. Normal
interrupted activation remains disabled.

## Initial recovery capability assessment — 2026-10-23

PaymentRecoveryPlan.Assess classifies the reproduced life-then-mill failure:
a completed pure life payment with recorded prior player data is a supported
reversal; an operation that moved a card from the library outside the stack is
retained. Unknown costs and incomplete/unidentified steps are explicitly
unsupported. HasJournal distinguishes missing/empty legacy provenance.

The regression checks reverse step 0, retain step 1, and rejection of missing
journal identity. This is intentionally an incomplete capability assessment:
other reversal types, movement dependencies, library reveal/shuffle cases and
state application are not implemented. A plan with unsupported entries cannot
authorize partial recovery. No gameplay caller invokes it yet.

Validation: full rules 1,602 passed before the final HasJournal indicator;
the final focused library payment suite passed (library-payment-recovery-plan-rules.log,
library-payment-recovery-plan-focused.log). Existing normal-command refusal remains.

## Supported life-payment recovery application — 2026-10-23

PaymentRecovery.Apply now applies a failed activation result only when its
journal is present, no choice remains, and the capability assessment has no
unsupported steps. It produces PaymentRecoveryApplied, a replayable restoration
event that refunds supported life payments directly, preserves library departures,
clears the failed choice and restores the activating player's priority. It does
not emit LifeChanged or ObjectMoved for the restoration. Unsupported plans
continue to refuse instead of applying a partial reversal.

The reproduced failed mill-payment test now applies that recovery and replays
its events: life returns to 20, the milled card remains in the graveyard, library
stays empty, no ability reaches the stack, and the choice is cleared.
Game.Settle recognizes the explicitly reversed origins, removes their deferred
triggers and excludes their current-batch occurrences using the execution map.
A settlement regression verifies a reversed life-loss occurrence does not fire
and the restoration is not interpreted as life gain.

This is a supported internal slice, not general recovery: other payment types,
retained-operation dependencies and unknown provenance still require handling.
ResumeActivationPayment and normal Activate are not wired to auto-recover yet.
The original command refusal remains until that integration is validated.
Validation: 1,603 rules and 809 API tests passed (library-payment-recovery-apply-rules.log and library-payment-recovery-apply-api.log).

## Supported recovery through choice commands — 2026-10-23

ResumeActivationPayment now applies PaymentRecovery when resumed progress fails,
instead of discarding a supported recovery result by rethrowing immediately.
PaymentRecovery still refuses missing, incomplete or unsupported plans.
The normal Activate entry point continues to use allowPause:false.

The former stuck-choice regression now creates life-loss and life-gain watchers,
settles the initial internal activation pause, serializes it, and answers every
offered replacement option through Game.Apply. Both answers restore life to 20,
leave the milled card in the graveyard, clear the choice, leave no ability or
trigger on the stack, return priority, and emit no TriggerFired. Returned events
are replayed to verify the same state. The independent progress/result assertions
remain, distinguishing the failed payment state from the recovered state.

This closes the reproduced internal choice-resumption failure for the supported
life-payment/library-departure combination. It does not enable pausing ordinary
activations or establish general recovery for mana, tap, sacrifice, other costs,
unknown trigger provenance, or every retained-operation dependency.
Rules: 1,603 passed (library-payment-recovery-command-rules.log).
API: 809 passed (library-payment-recovery-command-api.log).

## Tap-state recovery for interrupted library payments — 2026-10-23

Supported recovery now includes TapSelf and UntapSelf steps with recorded object
states and tap-only events. Recovery restores the recorded prior tap state via
PaymentRecoveryApplied, without generating an ObjectChanged occurrence. It checks
the reverse sequence against the current tap state and refuses recovery if a
retained operation changed the same object.

Two regression cases serialize the initial library replacement pause, answer it
through Game.Apply, and replay the resulting events. Both restore the original
tap state, preserve the completed mill, clear the choice, and leave the stack empty.
Rules: 1,605 passed (library-payment-tap-recovery-rules.log).
API: 809 passed (library-payment-tap-recovery-api.log).

Normal activation pausing remains disabled. Mana, sacrifice, other unsupported
costs, and broader retained-operation dependencies still need recovery support;
this does not establish general illegal-action recovery or complete library parity.

### Recovery journal validation

Tap recovery now requires matching object identities and before/after contents
that differ only in Tapped. A regression modifies the recorded damage while
leaving the tap event unchanged; both tap and untap cases previously classified
that step as reversible and now reject it. Comparison uses serialized content
with EventLogSerializer.Options so deserialized collection references do not
cause valid replayed payments to be refused. Existing serialized command/replay
assertions continue to pass. The focused failure is recorded in
library-tap-boundary-before.log; full verification is recorded in
library-tap-boundary-rules.log and library-tap-boundary-api.log.
