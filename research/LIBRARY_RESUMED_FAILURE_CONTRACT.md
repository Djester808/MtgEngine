# Resumed library payment failure contract

## Payment boundary acceptance decision — 2026-10-24

Current source review confirms ActivationPaymentAdmission accepts final library
pauses and earlier ordinary/library pauses with a library-only remainder and an
assessable completed prefix. CostPaymentOrder rejects ordinary payments after
library/random payments. PaymentRecoveryPlan retains evidenced irreversible
library events and delegates reversible validation to the shared recovery owners.
The combined current payment filter passes 307 tests, zero failures/skips,
exit 0 in library-payment-boundary-audit.log. This includes the newly extended
successful public activation through full resolution/replay.

Decision: no generic catch-and-recover implementation is justified by the audited
resumption branches. Invalid answers retain their retry/decline contract; missing
or corrupt evidence retains unchanged-pending refusal. Neither is a missing
successful library operation. Unclassified replacement events remain an explicit
capability limit, not certified reversible. Any extension must reproduce a
required supported event and its exact unsupported classifier; it must not be
implemented by swallowing all interpreter refusals. This closes the speculative
exception-wrapper prerequisite, not all conceivable replacement capabilities.
No production code changed and no commit was made.

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

## Primary web confirmation — 2026-10-24

[CR 733.1](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf)
requires reversing illegal unfinished actions, with exceptions for library
movement (except library-to-stack), shuffling and revealing library cards.
The [official MTGO undo guide](https://www.mtgo.com/getting-started/getting-started-tips-tricks)
independently documents the draw/mill restriction for mana-ability undo.
This supports preserving irreversible library evidence in recovery; it does
not certify every local replacement classifier or require a generic catch-all
exception wrapper. Remaining extensions still need a concrete supported legal
interaction and a regression demonstrating the missing behavior.

Status: ordinary-prefix and library-prefix admission/recovery are implemented and
have public-command and live evidence. Unclassified replacement evidence remains
restricted. Typed partial-failure propagation is not implemented, and no required
legal escaping-partial-result case has yet been established.

## Ordinary-prefix public admission implemented — 2026-10-24

The nested-recovery fixture now begins activation through Game.Apply. Before the
change, both activation variants failed admission while three effect-payment
controls passed (library-prefix-admission-red.log). ActivationPaymentAdmission
now permits an interrupted ordinary prefix when the remaining costs are entirely
library costs and the existing shared assessment accepts the completed prefix.
A final non-library pause and non-library remainders retain their restrictions.
The interrupted step continues through the existing journal/resume/recovery path;
no second payment executor or generic exception-recovery wrapper was added.

PartialNestedLibraryRecoveryTests now verifies public activation, serialized
answers, nested evidence, retained library movement, life refund and one retained
departure trigger. Duplicate answers are refused. The missing-evidence variant
still refuses with the saved-payment diagnostic and preserves its pending choice.
Mixed and chained movement fixtures also start through public commands; the
dependent-sacrifice fixture checks public admission while retaining its explicit
internal settlement assertions. Twenty old refusal expectations were reached by
the widened path and updated only for those now-admitted cases.

Full rules: 2,080 passed, zero failures/skips (library-prefix-admission-rules.log).
Full API: 840 passed, zero failures/skips (library-prefix-admission-api.log).
Live acceptance now passes through the activation panel on four-seat table
b529279d-d282-40fc-9727-44f4c148d3b4 (library-prefix-panel-live.log): cancellation,
confirmation, three replacement choices, retained library movement/shuffle,
life refund, surviving trigger resolution and all-seat reload. The later six-case
PartialNestedLibraryRecoveryTests run (library-prefix-success-tests.log) also
verifies successful payment/stack creation through public commands, serialized
answers and duplicate refusal. Its success variant uses a zero final mill cost;
it does not establish resolution of that activated ability. No commit was made.

## Current restriction decisions — 2026-10-24

### Exact completed-prefix classifiers

The current source audit distinguishes the following refusal categories. These
are source findings, not new test results or a claim that arbitrary replacements
are supported.

| Owner | Accepted evidence | Remaining refusal category |
| --- | --- | --- |
| PaymentRecoveryPlan.Assess | Completed journal steps; evidenced library movement, shuffle or reveal are retained before reversible classification | Missing journal/incomplete step is an integrity boundary. Otherwise the exact residual event must be identified rather than treating every Unsupported index as a library defect. |
| PaymentPropertyValidation.Matches | Life and mana changes; tap-only ObjectChanged; counter changes; exact resulting player/object snapshots | Other ObjectChanged fields and event types are not classified. A RemoveCountersCost also rejects a different counter kind. Unexplained snapshot deltas are rejected even when every event type is recognized. |
| PaymentCreationRecovery.Describe | Battlefield token creation with unique identity, exact creation history, zone membership and supported subsequent properties; moved creations delegate to PaymentCreationMovementRecovery | Non-token creation, missing/contradictory creation history and unsupported residual events are rejected. An arbitrary ObjectCreated event is not sufficient evidence for safe reversal. |
| PaymentMovementRecovery.Describe | Unique departure/arrival identities, supported cost markers or evidenced additional/intermediate movement, exact zone/history reconstruction and supported properties | Additional/intermediate arrivals outside hand/graveyard/exile are rejected except the final commander return. In particular, an ordinary non-token battlefield arrival is not classified here. Missing departures, inconsistent markers and unexplained history changes are integrity failures. |
| PaymentMovementRecovery.Partition | Movement dependencies on retained library operations, with event-level origins for partial disposition | Missing event origins for partial recovery and unresolved overlap with retained object changes are refused rather than restoring an object over retained state. |

This makes the remaining capability question concrete: demonstrate a required
legal library payment whose ordinary replacement prefix produces one of the
unclassified event/property/arrival categories above. The existing original plan
requires shared replacement/interruption semantics; it does not require a second
generic effect executor or permission to implement unrelated card capabilities.
No such new interaction was established in this pass. F1 remains open until that
capability boundary receives a final scoped acceptance decision; missing or
corrupt journals already have an explicit refusal contract. The separate
escaping-exception trace below found no new supported failure to wrap.

Re-read ActivationPaymentAdmission, ActivateCore, CostPaymentOrder,
ChoiceSystem.ResumeOperation and PaymentRecoveryPlan after the ownership audit.
The remaining work must distinguish these concrete branches:

| Boundary | Current decision / remaining requirement |
| --- | --- |
| Pause in the final library cost | Publicly admitted; remaining cost list is empty. Do not describe this as blocked. |
| Pause in a library cost followed by library costs | Publicly admitted when shared recovery assessment supports the completed prefix. Existing public replay and live success/failure evidence applies. |
| Ordinary cost after a library cost | Rejected by preparation, including explicit ordering. Not a reachable supported continuation requiring a new failure mechanism. |
| Random discard after a library cost | Shared random-discard execution remains unimplemented. Do not widen library admission to pretend this cost is supported. |
| Replacement pause during an ordinary prefix before library costs | Publicly admitted when all remaining costs are library costs and the completed prefix passes shared recovery assessment. PartialNestedLibraryRecoveryTests and the four-seat activation-panel run above establish this path, superseding the old refusal/internal-seam finding. |
| Library pause with an unsupported completed prefix | Still refused by PaymentRecoveryPlan. Every new proposed prefix must identify which event evidence cannot be classified; do not add a second recovery engine or blanket admission. |
| Exception inside resumed operation | ResumeOperation composes interpreter, resolution completion, draw, zone batch and shuffle before RetainPayment. It has no typed partial-failure result. Prior audits have not demonstrated a supported legal operation requiring such an escaping partial result; invalid nested casting answers retain retry/decline semantics. This remains an investigation, not proof that a generic wrapper must be implemented. |
| Missing legacy recovery evidence | Explicit incompatibility with unchanged pending choice; no inferred migration. See the saved-choice contract and public refusal regression. |

The ordinary-prefix discriminator is closed. Remaining F1 investigation concerns
unsupported replacement event evidence and any supported legal operation that
requires partial failure to escape ResumeOperation. A proposed case must identify
the exact event rejected by PaymentCreationRecovery, PaymentPropertyValidation or
PaymentMovementRecovery, or the exact legal interpreter branch that throws after
an irreversible operation. Missing journals, incomplete steps and deliberately
corrupted snapshots are compatibility/integrity refusals, not new legal-operation
requirements. The diagnostic Unsupported effect in
Refused_replacement_resume_preserves_the_pending_payment_and_allows_another_answer
proves retry semantics only; it must not be cited as a legal partial-failure defect.

Source recheck: ResumeOperation completes interpreter resolution, deferred draws,
zone batches and deferred shuffles before RetainPayment. PaymentRecoveryPlan
retains evidenced library movement/shuffle/reveal and delegates reversible
creation/property/movement classification to shared owners. Its default refusal
does not itself prove a missing library feature; nor does current primitive
coverage prove every arbitrary replacement is supported.

## Reachable remaining-cost audit — 2026-10-24

### Library-owned refusal-path trace

Shared explicit-refusal follow-up: the interpreter's refusal sites were enumerated
and read. Missing effect/resume trees, unimplemented duration/meld/copy/cost channels,
undefined choice/type values, unsupported counter distribution and ambiguous
attacking/counter destinations are capability or input-contract boundaries. They
must not be silently converted into successful partial library payments.
MayCast.CostQuestion relays casting refusals when the chosen cast cannot be paid
or cannot supply valid additional-cost selections. The existing public-command
Unaffordable_nested_cast_can_be_declined_after_reload_without_repeating_prior_shuffle
regression covers mana and additional-life variants: an unaffordable Yes is refused,
the saved choice remains available, and No completes without repeating the prior
shuffle. Both cases are included in the latest passing 2,085-test rules suite.

ReplacementSystem.Resume throws only for a mismatched dispatched kind or absent
saved proposed event at its entry; an offered replacement that no longer applies
instead resumes the shared pipeline. These invalid-context guards are not a
legal-cost recovery path. No new explicit legal escaping-partial-result case was
established by this trace. The generic catch-and-recover proposal therefore remains
unjustified; required future fixes must name the concrete supported branch and
preserve retry/decline behavior. This is not certification of arbitrary effects.

Inspected LibraryDrawCoordinator, LibraryMillContinuation,
LibraryShuffleContinuation, LibrarySearchContinuation, and the interpreter's
Drawing/Library/Search adapters. These do not introduce an explicit gameplay
RefusedException branch during ordinary resumption. Draw handles an empty library
with DrawFromEmptyLibraryAttempted; mill records actual departures; shuffle
delegates to IZoneSystem and defers remaining players when a choice is pending.
Search continuation reads saved plan JSON (invalid persisted JSON is a data-format
failure), rather than inventing a separate payment exception policy.

ManualLibraryCoordinator's explicit refusals validate request identity,
availability, search arguments, current inspected membership or exact permutation.
They are command validation, not a demonstrated resumed payment failure. Shared
ZoneSystem.Entering has a refusal for nonnumeric printed planeswalker loyalty,
explicitly documented there as missing cast-X propagation. That is an unsupported
shared card-entry capability, not proof that supported library cost recovery must
catch all exceptions and publish partial events. Missing card definitions likewise
remain configuration failures. No change to these shared capabilities is justified
solely by this library audit.

This narrows the remaining discriminator to a supported replacement/interpreter
branch reached through those shared dependencies. It does not prove that no such
branch exists, and does not classify every generic effect or malformed saved log
as supported. No production changes or new test results are claimed by this trace.

Completed-prefix evidence update: the existing LibraryMillCostTests class passes
223 cases in library-payment-prefix-audit-tests.log. Source inspection maps the
implemented ordinary costs to existing recovery owners: life/mana to player
property recovery; tap/untap and removed counters to object property recovery;
sacrifice/discard/exile/return to movement recovery. Their tests include retained
library dependencies, original zone order, exact properties and replay.
This is coverage of implemented primitives, not every arbitrary replacement.

The selected exile/return batch test previously used BeginActivationPayment and
bypassed public admission. It now starts with Game.Apply(ActivateAbility).
All four variants pass in library-public-prefix-batches.log: selected exile from
battlefield, hand or graveyard, and returning battlefield objects to hand. They
resume through a public answer, retain the completed mill, restore both paid
objects in original order/state, and leave no activated ability on the stack.
No production change was needed. Other internal-helper tests must not be reported
as public-command coverage without checking their entry points.

The non-library-remaining-cost gate is not evidence that ordinary life/mana costs
after a library pause need a new recovery contract. `CostPaymentOrder.Prepare`
expands nested Costs/ChooseCost, orders ordinary entries before deferred entries,
and rejects explicit orders that place an ordinary entry after a deferred one.
`Deferred` currently names exactly MillCost, library ExileFromZoneCost and random
DiscardCost. `CostPayment.Continue` consumes that flattened ordered list;
`Resume` preserves it. Thus the non-library deferred remainder currently reachable
under normal preparation is random discard, not arbitrary ordinary costs.

`ObjectCosts.Discard` explicitly refuses Random=true with rule 701.9b because
random-discard cost execution is not implemented. `ActivationPaymentAdmission`
therefore must not simply widen its remaining-cost gate: that would admit a
library pause into an unsupported cost. This is an existing shared-cost capability
boundary, not a demonstrated missing library rollback algorithm. Random-discard
implementation is not authorized merely by discovering it during library work.

This closes the ordinary-cost-after-library hypothesis for the current prepared
cost vocabulary. It does not close unsupported completed-prefix combinations,
legacy paused records bypassing current preparation, or all replacement effects.
The next F1 investigation is the completed-prefix assessment, where actual
recovery evidence—not a new cost kind—can still reject an otherwise supported
library activation. No admission or gameplay policy was changed by this audit.

## Public library continuation checkpoint — 2026-10-24

`ActivationPaymentAdmission` now admits an interrupted library cost before further
library costs when `PaymentRecoveryPlan` can assess every completed prefix step.
The existing final-library-cost admission remains. Non-library pauses, unsupported
completed prefixes and non-library remaining costs remain restricted. This is a
partial public admission implementation, not completion of all payment recovery.

Five existing internal cases first failed through public activation commands
(`library-public-earlier-red.log`). They now use public activation for both later
cost failure/refund and successful repeated choices with a departed source.
The success variants use public answers, serialized replay, private views and
duplicate-answer refusal. Recovery retains the library movement, refunds life,
cancels the reversed life triggers and returns priority without stacking an ability.
All 1,994 rules tests pass in `library-public-earlier-rules.log`. Prior assertions
of blanket refusal were updated only for admitted cases; non-library pause and
invalid payment-order refusal checks remain. Live public earlier-pause acceptance
of failure/recovery is still required. Existing live effect-payment runs do not
establish that result.

Live public success now passes: table 4d977b33-171d-415d-ad13-2be37f86e5ab,
four browser seats (`library-public-earlier-live.log`). The ordinary activation
panel shows pay one life, mill one, mill one. The test explicitly reads the isolated
QA continuation count and verifies one remaining cost at the initial prompt.
Cancellation, confirmation, pending reload, replacement choices, private observer
contexts, final all-seat views and reloads pass. Life decreases once, library count
decreases by two, and one activation reaches the stack. Reloads append no library
events. Payment-order, landscape choice and final actor captures were inspected.
This run proves successful payment completion; it does not resolve the resulting
ability or exercise later-cost recovery.

Live public recovery now passes separately: table
f7a795dd-2fac-4a56-b345-7175cc7d5e9c, four browser seats
(`library-public-earlier-failure-live.log`). The same public activation begins with
one library card and two one-card mill costs. The first replacement prompt has
one remaining unpaid cost. After answering, the first mill remains applied and
the second cannot be paid. Life returns to 40, library count is zero, no activation
reaches the stack, and the QA event counts prove exactly one mill and one recovery.
Private observer contexts, pending reload and all-seat final reload pass with no
additional events or command refusals. Landscape prompt and final actor captures
were inspected. This fixture has no life-loss trigger; trigger cancellation is
established by the rules regression, not by its zero live trigger count.

## Concrete completed-step admission gap — 2026-10-24

Multiple-token follow-up: ten targeted variants pass in
library-multiple-token-dependency-tests.log. Added two-token reversal with and
without same-step tap/movement, plus selective retention: one green token enters
the library while a red token from the same replacement is undone. An existing
token remains untouched. Assertions check exact creation-history identities,
original artifact restoration, life, library counts, entry/death/departure history,
one recovery, serialization and duplicate refusal. The retained trigger must name
the green token's original creation identity. This follow-up requires no production
change; it verifies the shared implementation handles these compositions. Live
selective multiple-token acceptance is recorded below. The full suite, including
the final exact trigger-subject assertion, passes 1,994 tests in
library-multiple-token-rules.log. The earlier ten-case focused run preceded that
last assertion; the full run is the authoritative evidence for it.

Four-player selective follow-up: table 201196ae-de5d-4878-9cc5-71bc7268dbf6
passes the admitted spell-resolution path (`library-token-selective-live.log`).
The QA replacement creates a green and red token; only green is selected by the
later library movement. The test observes two tokens and two creation-history
entries before answering, then one retained history entry and one trigger.
The original artifact returns with its identity/tap state, life is 40, library
count is 2, and neither token remains on the battlefield. One shuffle, one recovery
and one subsequent trigger resolution occur. Private choices, actor choice reload,
and all-seat final/resolved reloads pass. Landscape choice, portrait rotation
notice and resolved actor captures were inspected. Exact trigger-subject identity
is covered by the rules test, not exposed by this live count check. No production
code changed; the earlier activation gate remains unchanged.

Same-step token movement checkpoint: the creation/movement adapter now seeds a
validation-only state with the existing ObjectCreated reducer, then validates the
remaining movement chain through PaymentMovementRecovery. It does not duplicate
zone-change or token-creation rules. Creation restoration records all owned
identities plus validated departure/death snapshots, removes the final token
identity and owned history, and maps creation/movement/property occurrences back
to their original payment indices. Unrelated cards/tokens remain intact.

The new create → optional tap → graveyard variants initially exposed a missing
entry trigger: final-state observation had lost the created token's old identity
(library-token-movement-red.log and library-token-movement-tests.log). TriggerObservation
(formerly TapTriggerObservation) now observes token creation as well as taps at
occurrence time, using the existing detector; committed events are unchanged and
the observed occurrences are excluded from subsequent detection to avoid duplicates.
Six focused variants pass in library-token-movement-observed-tests.log. Full rules
coverage, including missing departure/death evidence refusal, passes 1,990 tests
in library-token-movement-rules.log. Same-step live acceptance and broader mixed
creation/movement layouts remain to verify; public admission is unchanged.
The 840 API tests also pass in library-token-movement-api.log.

Same-step live follow-up: table eb8fc455-decb-43ec-80f8-b5a9cc434b57 passes four
browser seats on the admitted spell-resolution payment path. The fixture creates
a token, moves it to the graveyard in the same replacement, then taps/moves the
artifact; the later library operation retains the artifact. Before answering,
the actor's graveyard contains the token and creation/death history each increase
by one. After recovery, both counts return to baseline, the token is absent,
life is 40 and library count is 3. Exactly one shuffle, one recovery and one
retained artifact tap trigger occur; that trigger resolves once. No token entry
or leave trigger reaches the stack. Choice reload/rotation, private contexts,
hidden opponent options and all-seat reloads before/after resolution pass with
no hub refusals or repeated events. Evidence: library-token-movement-live.log
and tools/TableQa/library-token-movement-*.png. Inspected the landscape prompt,
portrait rotation notice and resolved actor board. Isolated host stopped.
Broader mixed creation/movement layouts and earlier activation admission remain open.

Token implementation follow-up: PaymentCreationRecovery validates battlefield
token creation against the journal's object, zone and creation/entry-history
snapshots. It extracts creation-owned events and properties, preserving a mapping
to original event indices. Existing movement and property recovery validate the
remaining step; trigger/nested provenance is mapped back to the original stream.
PaymentRestoration has an additive Creations list, replayed before movement stages.
Removal validates the exact current object and creation history; object/timestamp
allocators stay monotonic. Independent tokens and their history are removed without
new zone-change occurrences. A later retained movement consuming the token keeps
the creation and its trigger. Other retained mutations conservatively refuse.

The original characterization is now Token_created_before_a_library_pause_follows_its_recovery_dependency.
It verifies both reversal and retention, token tap properties, preserved unrelated
tokens and their history, serialized pauses/recovery, duplicate refusal, missing
history refusal, cancellation of the restored artifact's leave trigger, and exact
token entry-trigger disposition. The first two cases pass in
library-token-dependency-tests.log. Tokens moved within their creation step remain
unsupported: they need combined creation/movement evidence. Live acceptance and
broader creation dependencies remain outstanding; public admission is unchanged.
The following token-boundary paragraphs record the pre-fix diagnosis.
Final verification: all 1,988 rules tests and 840 API tests pass in
library-token-recovery-rules.log and library-token-recovery-api.log. This includes
four token dependency/property variants with unrelated-token preservation and
entry/leave trigger ownership checks. Shared property identity lookup now lives
in PaymentPropertyValidation.IsFor.

Live token acceptance: two four-browser runs now verify complementary mixed-step
outcomes through the admitted spell-resolution PayCost path. Table
fb643e42-1dce-4916-b8ed-f44bf2d8b5dc removes the independent token and its turn
history while retaining the artifact's library movement and tap trigger (library
count 3). Table 1bcf86e5-48bc-4af0-802a-892f776f3d55 consumes the token in the
retained library operation, keeps its creation history/entry trigger, and restores
the original artifact identity and untapped state (library count 2 after the token
ceases to exist). Both finish at life 40 with one shuffle, one recovery, exactly
one fired trigger and exactly one trigger resolution. Neither leaves the token on
the battlefield. One pending choice reloads/rotates in each run; opponent options
and private payment contexts remain hidden. All four seats reload before and after
trigger resolution without repeated events. No hub refusals.
Logs: library-token-recovery-live.log and library-token-retained-live.log. Captures
under tools/TableQa use matching prefixes. Inspected both landscape prompts and
resolved actor boards, plus the retained run's portrait rotation notice. Both
isolated hosts were stopped. No production/client source changed in this live
follow-up. Same-step creation/movement and earlier activation admission remain open.

Next concrete admission boundary: Token_created_before_a_library_pause_is_an_explicit_recovery_boundary
uses supported CreateToken and Move primitives in a sacrifice replacement. The
completed step creates a 1/1 token and moves the artifact to graveyard; the next
mill pauses, and a later mill is unaffordable. Public initiation refuses. Internal
initiation followed by a normal answer also refuses because Assess marks the
completed token-creating step unsupported. Replay preserves the choice, life 18,
one library card and exactly one token. No recovery event was emitted. The focused
characterization passes in library-token-boundary-tests.log (one test); this is
evidence of a missing recovery capability, not successful token recovery.

Source reason: PaymentPropertyValidation rejects created objects and ObjectCreated
events; PaymentMovementRecovery.Properties leaves that creation in the residual
step, so Describe also rejects it. This is not an escaping partial-operation
failure. Next implementation must journal and reverse independent creations plus
their entry history/associated triggers, while preserving creations consumed by
retained operations. Validate final identity/snapshot before removal and keep
unrelated creations intact. Reuse shared recovery/provenance rather than deleting
every token or dropping ObjectCreated events from validation. Add replay, dependent
creation, mixed movement/creation and live acceptance before claiming this boundary
closed. Earlier activation admission remains restricted.

Mixed-chain follow-up: a replacement moves two artifacts battlefield → exile →
graveyard, but only the noncreature artifact later enters the library and is
shuffled. Four variants cover both initial battlefield orders and an optional
serialized pause between the two movement batches. Initially all four refused
(library-mixed-chain-tests.log): the incidental second card had no ObjectLeftBy
cost marker, so Describe rejected its root movement despite recorded evidence.
Describe now accepts such nonlibrary roots only with exactly one actual departure
and validated root/object/zone/history/property snapshots. Intermediate hops retain
the same evidence checks. Missing incidental departure evidence refuses. The shared
partition retains the library-bound chain and reverses the independent chain,
preserving its original identity, tapped state, counters and battlefield position;
unrelated exile contents remain unchanged. Life is refunded and only the retained
root remains in departure history. Four cases pass in
library-mixed-chain-recovery-tests.log. Live mixed-chain acceptance remains
outstanding. No public admission change was made.
Full verification: 1,984 rules and 840 API tests pass in
library-mixed-chain-rules.log and library-mixed-chain-api.log, including the added
missing-incidental-departure refusal assertion.

Live mixed-chain follow-up: table c91903a8-1c4b-475d-8231-273bbfd13e33 passes
four browser seats on the admitted spell-resolution payment path. Both artifacts
are tapped and move battlefield → exile → graveyard. Only the noncreature artifact
enters the library. Recovery restores the independent artifact's original identity
and untapped state, refunds life to 40 and leaves library count 3. All seats agree.
Counts prove one shuffle, one recovery, one surviving tap trigger and exactly one
resolution of that trigger. The independent tap does not create a second trigger.
The pending choice reloads/rotates, private contexts and opponent choices stay
hidden, and all-seat reloads before/after trigger resolution repeat no events.
Evidence: library-mixed-chain-live.log and tools/TableQa/library-mixed-chain-*.png.
Inspected landscape/portrait choice captures and the final actor board. The first
fixture attempt selected spare exile setup cards and opened an extra ordering
prompt; the fixture now uses Move.RememberAs and Remembered to follow only the
two battlefield departures. No production change was needed for that correction.
The isolated host was stopped after verification. Earlier activation admission
and general partial-operation failure remain separate outstanding work.

Latest hand-entry follow-up: two new variants reproduced rejection of a terminal
exile → Command hop when the owner returns the commander instead of entering hand
(library-commander-hand-red.log: two fail, fifteen pass). Describe now permits that
terminal destination only when both departure and arrival identify a commander.
It still validates actual departure evidence, object/zone snapshots, properties,
history and candidate bookkeeping. Existing reverse-order restoration returns the
original battlefield identity with its tapped state and counters. Both variants
pass: an ordinary mill remains applied, or a later shuffle remains applied without
consuming this commander. Neither unrelated library operation keeps the commander
in Command. All seventeen chain cases pass in library-commander-hand-tests.log.
This path is internally initiated and answered through normal commands with replay;
live hand-entry acceptance remains outstanding and public admission is unchanged.
Full verification: 1,980 rules and 840 API tests pass in
library-commander-hand-rules.log and library-commander-hand-api.log.

Live hand-entry follow-up: table 795f1c3f-1bdb-42c8-9d26-57fa9da53f1a passes
four browser players on the admitted spell-resolution payment path. The commander
is tapped, moves to exile, and its proposed hand entry is replaced by Command.
The subsequent mill replacement shuffles without consuming that commander; a later
unaffordable cost triggers recovery. All seats see the original commander identity
back on the battlefield with its original untapped state, zero commanders in the
actor's Command zone, life 40, library count 2 and an empty stack. Event deltas are
one shuffle, one recovery, zero mills, zero fired triggers and one spell resolution.
Both prompts reload/rotate, opponent options remain hidden, private payment contexts
remain absent, and repeated all-seat reloads add no events. No hub refusals.
Evidence: library-commander-hand-live.log and tools/TableQa/library-commander-hand-*.png.
Inspected the first landscape prompt (Return to command zone / Move to Hand),
portrait rotation notice and final actor board. QA host stopped after verification.
This verifies cancellation of the undone tap alongside retained library effects;
it does not widen earlier-activation admission.

Implementation follow-up: the reproduced chain now has recovery support. The
historical characterization below has been replaced by
`Chained_nonlibrary_payment_movements_restore_after_library_payment_failure`.
Describe validates unique departure and arrival identities while allowing an
arrival to become a later departure. Intermediate hand/graveyard/exile hops require
actual departure evidence; root cost markers and property validation remain shared.
Each hop produces an existing PaymentMovementRestoration and is undone in reverse
order. Touched zones are captured even when their final contents equal their
initial contents, preserving unrelated cards in an intermediate zone.

Partition now follows a chain within the step before following later retained
library movements. Thus a consumed final identity retains the entire dependent
chain while independent life payments are refunded. Tests cover both reversal
and retention, serialization, original tapped state/counters, unrelated exile
contents, history, and missing/altered intermediate evidence refusal. The original
successful-recovery expectation failed in `library-chain-recovery-red.log`.
All 1,965 rules tests pass in `library-chain-recovery-rules.log`.
The 840 API tests also pass in `library-chain-recovery-api.log`.

Remaining proof includes mixed independent chains, earlier hand-entry commander return,
and live acceptance. This does not establish
support for arbitrary movement paths or justify lifting the public gate yet.
The following paragraphs record the pre-fix finding and motivation.

Paused-chain follow-up: twelve variants now pass in
`library-chain-paused-tests.log`: two- and three-hop paths (the latter through
hand), retained and reversed outcomes, and reloads between movements. Each
intermediate answer is replayed and duplicate answers refuse. Original tapped
state/counters, unrelated exile contents and final hand contents are preserved.
Commander variants explicitly choose keep at the additional hand/library
replacement prompts, reloading and checking duplicate refusal there too. They
verify commander identity and return-candidate membership at each pause and after
recovery. The first commander attempt incorrectly assumed no additional prompts;
the fixture was corrected to exercise those legitimate choices.

These are test-only additions after the 1,965-test full-suite baseline. They do
not verify choosing the command zone, mixed independent chains, or live chain
acceptance. No production code or public admission changed in this follow-up.

Command-zone reproduction: a thirteenth variant chooses return at the library-entry
replacement after the two-hop sacrifice. The successful-recovery expectation fails
with Refused (`library-chain-commander-return-tests.log`: twelve pass, one fails).
The regression now explicitly characterizes refusal, reloads the pending choice,
then chooses keep and verifies the existing successful recovery. This is retry
coverage, not command-zone support; production code remains unchanged.

Source trace: PaymentRecoveryPlan retains the entire mill step because it contains
a shuffle. PaymentMovementRecovery.Partition follows the preceding movement chain
only to an actual library arrival. Redirecting the commander to Command creates
no such arrival, but the retained step still touches the terminal identity. The
retained-object intersection therefore refuses recovery. The next implementation
must resolve this dependency at the correct operation boundary; blindly treating
every command-zone movement as a library arrival would conceal the distinction.
CR 733.1 in the repository rules text is the governing reversal constraint.

Implementation follow-up: dependency discovery now follows a reversible step's
movement chain to its final identity, then checks whether a later retained step
consumes that identity. Both the single-movement plan and mixed-step partition
use ReachesRetainedMovement. Retention is established by the existing plan (in
this case the shuffle), not inferred from a Command destination. Independent
life properties remain reversible. The regression again requires successful
recovery, the commander in Command, one shuffle, life restored, clean candidate
bookkeeping, serialized answers and duplicate-answer refusal. Added variants
cover an uninterrupted two-hop chain and a paused three-hop chain that keeps
the earlier hand destination before returning at library entry. The original
205 mill-payment cases pass in library-commander-dependency-tests.log; full-suite
verification includes the two added variants. Earlier hand-entry return and
live chain acceptance remain outside this proof. Public admission is unchanged.
Verification: all 1,978 rules tests and 840 API tests pass in
library-commander-dependency-rules.log and library-commander-dependency-api.log.

Live acceptance follow-up: table 4d711ac3-990a-4ad4-8618-dd3a2575a67a passes
four browser seats through the production session/hub and admitted spell-resolution
PayCost path. QA-only libraryCommanderChain uses the existing commander, moving it
battlefield → exile → graveyard before the mill replacement proposes library entry.
The owner chooses Return to command zone, then the replacement shuffles and the
later mill cost fails. Final life is 40, library count 2, commander count 1 in
Command, and the commander is absent from the battlefield for all four players.
The event counts prove one shuffle, one recovery, one retained tap trigger and
exactly one resolution of that trigger. Both prompts reload and rotate, private
payment contexts remain absent, opponent options are empty, and all seats reload
before and after trigger resolution without repeating events. No hub refusals.
Evidence: library-commander-chain-live.log; captures under tools/TableQa with the
library-commander-chain prefix. Inspected both landscape prompts, the second
portrait rotation notice, and the resolved actor board. The isolated host was
stopped after acceptance. This closes the selected live chain case, not public
earlier-activation admission or every Commander replacement path.

`Chained_nonlibrary_payment_movements_remain_an_explicit_recovery_boundary`
now records a supported replacement sequence: pay two life, sacrifice an artifact;
the sacrifice replacement gains one life and moves that artifact battlefield →
exile → graveyard. The next mill pauses for replacement ordering, and a later
mill is unaffordable. Both movement departures survive serialized replay, but
Describe rejects the completed sacrifice step because the two moves share the
intermediate identity and the global before/after changes omit that intermediate
object. The public activation is refused before beginning; internal initiation
followed by a normal answer also refuses recovery, preserving the pending state.

The characterization passes in `library-chain-admission-tests.log` (one test).
It proves an unsupported recovery boundary, **not** successful recovery. Initial
life remains 19 and the library remains at one card after the refused answer.
No typed partial-operation exception is involved: the unsupported step completed
before the library pause, so this is the concrete completed-step work called for
by the audit below.

The already-admitted effect-payment path calls the same PaymentRecovery assessor;
its successful live examples do not prove this chain is recoverable. Do not lift
the activation gate on the strength of those examples alone. Next implementation:
describe and validate multi-hop nonlibrary movement chains using their actual
departure evidence, restore them in reverse order, and partition dependencies if
a later retained library operation consumes the final identity. Include properties,
history, trigger ownership, replay and malformed-evidence refusal. Library-touching
operations remain retained; do not treat an entire mixed step as reversible merely
because its first movement is a sacrifice.

## Transitive operation audit — 2026-10-24

Inspected the current `Zones`, `Replacement`, `Library`, and `Choices` source
paths called by `ChoiceSystem.ResumeOperation`, beyond the direct interpreter
audit below. This changes the next investigation: a supported escaping partial
failure has still not been identified, so adding a generic failure wrapper is
not justified by the current evidence.

| Path | Observed refusal boundary | Classification |
| --- | --- | --- |
| `ZoneSystem.Entering` | Variable printed planeswalker loyalty cannot be carried to the battlefield. | Explicit implementation gap, not an unpaid cost. |
| `ReplacementSystem.Resuming` | Wrong choice kind or missing original event throws `InvalidOperationException`. | Malformed continuation/programming error. |
| `ReplacementSystem.Applying.Instead` | Delegates to the interpreter and returns its outcome and payment origins. | No independent unpaid-result contract; interpreter cases remain relevant. |
| Library draw/mill/shuffle continuations | No direct refusal sites; continuation composition delegates movement to zones. | No newly identified legal partial failure. |
| Library search/arrangement continuation adapters | Unknown memory representation throws `ArgumentOutOfRangeException`. | Invalid persisted representation, not a payment failure. |
| `ManualLibraryCoordinator` | Request IDs, availability, inspection and arrangement validation. | Separate command admission; not called by payment resumption. |
| Choice answer validation | Wrong owner/choice, invalid options/counts, stale card or commander. | Invalid answer/state; preserve the pending choice on atomic refusal. |

`CostPayment.Continue` catches a refusal from `One` before appending that step's
outcome. It returns preceding completed steps and the unpaid cost; it cannot
represent a partially committed failing step. In contrast, a paused step is
recorded with `Completed = false`. `ResumeActivationPaymentProgress` marks that
step complete only after the operation returns without another pending choice,
then evaluates the remaining costs. These are distinct failure boundaries.

The known public-admission obstacle is therefore broader than the speculative
escaping failure: `PaymentRecoveryPlan.Assess` must successfully classify every
completed step reachable before a later unpaid cost, and the restoration code
must handle dependencies among those steps. The next implementation investigation
should reproduce a supported **completed-step** combination rejected by recovery,
alongside its normal activation/answer behavior. The escaping-failure contract
remains conditional on a concrete supported reproduction; it must not become an
invented prerequisite that indefinitely postpones the actual recovery work.

This is a source audit, not runtime acceptance or proof that all transitive
operations are total. Public activation admission is unchanged.

## Current behavior

`ChoiceSystem.ResumeOperation` finishes interpreter resumption, resolution cleanup,
deferred draws, the remaining batch, and deferred shuffles in that order. Only its
returned outcome reaches `RetainPayment`. This preserves the pre-extraction behavior
and gives operation completion one explicit boundary. `CostPaymentProgress` already
distinguishes a later unpaid cost from a completed or paused payment, but it does not
represent failure inside the operation being resumed.

The existing Unsupported-effect diagnostics verify atomic refusal and retry with
another answer. They do not prove an ordinary legal payment needs automatic recovery.
Unsupported interpreter shapes, missing source definitions, malformed continuations,
and programming exceptions must not be silently converted into unpaid costs.

## Required result semantics

An operation result must distinguish completed, paused, and a specifically classified
payment failure. A payment failure needs the exact partial outcome and the failed
operation identity. A generic exception plus the last state visible to the caller
is insufficient: effects may already have shuffled or moved cards inside a child call.

The partial outcome must contain each event once, with its matching final state,
payment origins, and deterministic random state. Composition must offset provenance
when appending event streams. Nested interpreter frames already carry cumulative
events; blindly attaching each frame's history would duplicate occurrences.

The failure must identify the interrupted cost without marking its incomplete step
completed. Recovery needs an explicit assessment for that partial step. It must not
continue deferred draws, batches, shuffles, remaining costs, or the ability's body
after failure. Completed irreversible library operations remain retained according
to the recovery plan; recoverable independent payments and their triggers are undone.

Choice settlement must happen only after recovery succeeds. Unknown ownership,
unsupported partial steps, and authoring faults retain existing atomic refusal
behavior rather than publishing a guessed partial recovery. The original pending
choice remains available after refusal; an accepted recovery clears it and rejects
duplicate answers.

## Acceptance before widening activation admission

Admission audit follow-up: a proposed "library cost, then final sacrifice" case
does not reach that order. `CostPaymentOrder.Prepare` places ordinary costs before
library/random costs and rejects an explicit order that crosses those groups.
The test's refusal was therefore not evidence for widening final-cost admission.
A random discard can be ordered after a library cost, but `ObjectCosts.Discard`
explicitly refuses `Random: true` before any discard operation runs. The attempted
public-command fixture consequently recovered the completed library payment rather
than opening a discard choice. Both exploratory tests and the proposed admission
change were removed. Public admission remains unchanged. Do not use these failed
fixtures as evidence for final-cost continuation or invent support for random costs.
Future admission work must start with a supported earlier library pause and the
actual subsequent cost/recovery path already exercised internally.

1. Reproduce a failure using supported operation primitives and identify the typed
   unpaid condition, separately from the existing Unsupported-effect diagnostics.
2. Carry its partial outcome through interpreter and operation composition without
   duplicate events or lost nested ownership. Verify before/after-shuffle cases.
3. Assess the incomplete step explicitly, preserving retained library changes and
   reversing supported independent payments and only their associated triggers.
4. Verify normal activation/answer commands, every serialized pause, private player
   projections, duplicate refusal, and priority/trigger settlement.
5. Exercise the admitted path through the live multiplayer harness, including reload
   and final event counts. Only then broaden the public activation restriction.

Do not change admission solely because the helper extraction or existing full suites
pass. Do not catch every RefusedException and label it a legal unpaid-cost result.

## Supported nested inability checkpoint — 2026-10-24

Expanded `Unpayable_replacement_payment_finishes_final_library_cost_after_reload`
to cover a replacement that shuffles before attempting an unpayable nested green
mana cost. Both replacement choices pass, alongside the two existing no-shuffle
cases (`library-resumed-unpayable-tests.log`, four tests). The preceding shuffle
is retained exactly once, the unpayable cost's conditional shuffle does not run,
no mana/recovery event is emitted, and the outer final library cost finishes after
serialized replay. The activation reaches the stack and duplicate answers refuse.

This supported inability is handled inside PayCost; it is not a failure escaping
ResumeOperation. It therefore does not justify either a generic exception recovery
wrapper or lifting the earlier-cost admission restriction. A supported escaping
failure with partial outcome remains to be established before that contract can
be implemented correctly.

## Direct interpreter refusal audit — 2026-10-24

Inspected direct RefusedException sites rather than inferring their meaning from
the exception type. Current classifications:

| Source | Observed reason | Recovery implication |
| --- | --- | --- |
| `EffectInterpreter.Structure.cs` | Unsupported marker or choice with no defined options | Authoring/implementation boundary; not evidence of an unpaid outer cost. |
| `EffectInterpreter.Resume.cs` | Missing effect tree for an off-stack continuation | Missing persisted context; do not invent a partial outcome. |
| `EffectInterpreter.Actions.cs`, `.Faces.cs`, `.Backlog.cs` | Unimplemented duration return, meld components, or large counter distribution announcement | Capability gaps; no automatic payment recovery. |
| `EffectInterpreter.Wave24.cs`, `.Wave26.cs`, `.Wave27.cs` | Unknown amass type, no named defending player, unspecified counter allocation | Requires validation of the particular effect/reference; not a general payment-failure signal. |
| `EffectInterpreter.Retarget.cs` | Missing casting subsystem, unsupported copy/alternative-cost/lasting-permission channels | Capability/context gaps. |
| `EffectInterpreter.Retarget.cs: Cast / CostQuestion` | `CheckInstructed` refusal, relayed when not a supported object-selection request | Concrete supported nested-casting boundary requiring a focused test. An invalid cast answer may need refusal and retry/decline while the outer payment remains pending; do not automatically unwind it. |

This audit covers direct interpreter sites only, not every transitive call through
zones, casting or replacement systems. It does not establish that an escaping
partial failure is impossible. Next discriminator: a replacement containing a
shuffle followed by MayCast, choosing an unaffordable nested cast, then reloading
and declining. Verify retained prior events, unchanged pending choice on refusal,
no duplicate shuffle, and eventual outer-payment completion. Contrast this with a
failure after a nested cast has actually begun spending costs.

The proposed prepayment discriminator now passes:
`Unaffordable_nested_cast_can_be_declined_after_reload_without_repeating_prior_shuffle`.
A normal activation pays two life and starts its final mill cost. Its replacement
shuffles and offers an instant costing green mana. Answering yes refuses with
601.2h naming `{G}`. Replaying the committed events retains the same pending
choice; answering no completes the activation without another shuffle, mana
change or recovery event. The offered card remains in hand, library order and RNG
remain unchanged from the pause, and duplicate decline refuses. The full
LibraryMillCostTests class passes 179 tests in
`library-mill-cost-nested-cast-tests.log`. This confirms retry/decline behavior for
an invalid nested cast answer, not partial-cost recovery after a cast begins.

### Corrected nested-casting hypothesis

Source inspection of `CastingSystem.Instructed.cs: CheckInstructed` shows that
checking invokes the complete `CastInstructed` path on immutable state and catches
RefusedException. `EffectInterpreter.Retarget.cs: Cast` then calls that same path
with the same state and command after a successful check. The prior proposed
"passes initial checks, then fails later" scenario cannot be assumed from a later
cost alone: the check already evaluates that cost and its replacements.

The focused regression now also offers an instant with three life as an additional
cost before its unaffordable green mana cost. The relevant invariant is that this
provisional life payment does not escape the failed check into committed state.
This is a stronger invalid-answer boundary, not a fabricated example of partial
recovery. A possible difference between checking and execution would need actual
evidence of nondeterminism, mutable state, or a distinct continuation path.
