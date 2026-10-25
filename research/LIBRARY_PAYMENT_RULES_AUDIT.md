# Library payment rules audit

Inspected 2026-10-23. Scope: payments involving the library, not expansion of
card definitions or the casting UI. This is a source/rules audit, not a passing
runtime acceptance result.

## Current source checkpoint — 2026-10-23

Source-lifetime evidence: the supported final-library-cost path now tests a source
sacrificed as an earlier cost. Four normal-command cases (mill/library exile,
one/two cards, revealed-top variants) replay replacement choices and resolve the
ability after priority passes without restoring or resacrificing the source.
Rules 1,780 pass in library-source-sacrifice-payment-rules.log. This confirms
CostPaymentContext's saved source/characteristics for this continuation boundary.

Multi-card continuation evidence: four two-card retained-spending cases rebuild
from the full event log before each choice and alternate offered replacements.
They assert no premature recovery, exact retained occurrence counts and final
refund/source values after the next cost fails. Rules 1,776 pass in
library-retained-spend-batch-rules.log. ObjectCosts.Mill checks full initial count
before invoking MoveAll; these cases do not model a failure halfway through that
same batch. General partial-failure/dependency handling remains open.

Paused integration follow-up: four additional engine-generated cases exercise
BeginActivationPayment, settle, event serialization/rebuild, AnswerChoice and
recovery after a later failed mill. They preserve retained spending with ordinary
or restricted mana and one or two other payments. Full rules 1,772 pass in
library-retained-spend-paused-rules.log. Each case also verifies the production
ActivateAbility gate still refuses the earlier pause. This establishes internal
continuation behavior only, not permission to open all normal activation pauses.

Normal ActivateAbility integration now verifies retained mana spending without a
constructed journal: a replacement pays G and shuffles, then a later mill cost
fails. Four cases cover ordinary/restricted pools and one/two reversible ordinary
payments. Engine-generated events and replay retain only the replacement spend,
refund other payments, preserve library membership and restore priority without
an activation on the stack. Full rules 1,768 pass in
library-retained-spend-activation-rules.log. This is synchronous failure recovery;
the earlier-choice pause gate and optional mana-ability reversal remain separate.

Spending integration evidence now covers serialized ActivationPayment assessment,
PaymentRecovery.Apply and event-log replay for ordinary/restricted mana, both with
and without a later reversible payment. All four cases retain the library-step
spend, restore other payments and priority, preserve source amounts and avoid new
ManaChanged occurrences. Full rules 1,764 pass (library-mana-spending-replay-rules.log).
The earlier statement that spending-journal integration is outstanding is superseded
for these four cases. Normal-command earlier pauses and mana-ability dependency
reversal still require separate implementation and acceptance.

Retained-spending follow-up: PaymentManaRecovery now validates and folds recorded
negative ManaChanged deltas instead of rejecting all spending. It checks each
recorded before/after fold, the expected current pool and the restored pool. Before
the shared fold prunes exhausted blocks, it rejects negative totals for each exact
source/key/color or restriction/color block; ordinary color underflow also refuses.
Ordinary/restricted tests preserve source amounts and reject bucket/color overdraw.
The ordinary case failed before implementation (library-mana-spending-red.log).
Rules 1,762 pass (library-mana-spending-rules-final.log). This does not reverse mana
production abilities or establish the broader CR 733.1 mana-ability dependency path.
Normal earlier-payment pauses remain gated, and full journal integration for these
new spending cases is still outstanding. Earlier blanket-refusal notes below are
historical and superseded for validated retained deltas only.

Recovery integration follow-up: both retained-emptying chronology cases now pass
through PaymentRecoveryPlan.Assess and PaymentRecovery.Apply with a serialized
activation journal, then replay the emitted events. Assertions cover classification,
single restoration, no new mana-production event, exact player pools, priority,
pending choice, library membership, RNG and choice ID. Full rules 1,760 pass in
library-mana-empty-replay-rules-final.log. Journals are constructed in the test;
normal-command acceptance is not established by this evidence.

Retained-emptying follow-up: PaymentManaRecovery now folds ManaPoolEmptied and
nonnegative ManaChanged events in their recorded order for both expected and
restored pools. It still rejects retained spending. Three focused cases cover
production before/after emptying across serialization, missing-event/current-pool
refusal, and an already-empty pool that must not revive a refunded payment.
CostPaymentStep.Capture records pool-emptying even when the pool stays empty.
The two chronology cases failed before the change (library-mana-empty-red.log).
Rules 1,760 pass (library-mana-empty-rules-final.log). This verifies the recovery
fold, not a general normal-activation pause or mana-source dependency solution.
The production-only description below records the pre-change audit.

The paragraphs below this checkpoint are historical implementation increments,
not an outstanding-work list. In particular, client descriptor consumption,
ordering and selection are implemented: LibraryPaymentService.confirm constructs
CostObjects using ObjectOffset and returns the ordered expanded cost indices.
It does not infer costs from card text or expose unprojected library identities.

CastingSystem.ActivateCore permits a normal payment pause when the final completed
step is MillCost or library ExileFromZoneCost and RemainingCostPayment.Costs is
empty. Earlier pauses still refuse. The internal BeginActivationPayment seam used
by recovery tests does not establish normal-command support for those earlier pauses.

PaymentManaRecovery.Restore accepts retained mana production only. It rejects a
retained ManaPoolEmptied or any negative component in ManaChanged (including
restricted/provenance components), checks each recorded before/after fold, and
checks the expected final pool. Removing that rejection without dependency analysis
would broaden accepted recovery beyond the demonstrated capability. No such change
was made in this audit.

Next recovery requirement: establish how retained mana consumption/emptying depends
on earlier reversible payments, including restricted/provenance identity and later
payments, then cover exact serialized recovery outcomes before broadening the gate.
General activation pauses, hidden-library payment selection without an authorized
inspection, and alternative-cost UI remain unfinished. This checkpoint is source
evidence, not a new rules or browser acceptance result.

## Historical increments

Current preview boundary: SelectableObjects is now the intersection of the cost's
internal eligible objects and the actor's current projected objects. It contains
IDs the actor already receives, no additional card text or hidden candidate counts.
Historical reveal snapshots do not grant selection access. Candidate enumeration
uses expanded payment indices; selection offsets remain expanded written offsets.
Validation, discovery and projection use the same captured immutable game state.
The client still needs to consume this field and send selected CostObjects; hidden
library selections without visibility are not implemented by this change.

Selection metadata follow-up: PaymentChoice.ObjectCount is the number of slots
consumed by that cost, and ObjectOffset is its starting index in CostObjects in
expanded written order. Both are generated by the same binding pass as execution;
payment sorting does not change those offsets. These are not candidate counts or
permission to inspect a library. A future picker must honor existing disclosure
permissions and cannot turn the engine's internal CostChoice.Legal list into an
unrestricted HTTP card inventory. No such endpoint was added here.

## Payment ordering must precede further recovery expansion

Client follow-up: the board now renders the server's ordered descriptors with
earlier/later buttons restricted to adjacent entries in the same group. A
server-projected HasLibraryPayment marker routes only library-cost activations
through preview. Single-cost activations need no ordering prompt. The local
decision does not pay anything; confirmation sends descriptor indices through
the existing transport. Stale snapshots/connections cancel or discard the preview.
The public labels/groups are not reconstructed from card text in the client.

Controlled captures are reviewed at desktop/landscape, with the existing portrait
guard intact. Live multiplayer ordering and selection of cards to pay costs remain
unverified/unfinished. This does not open normal replacement-payment pauses.

Descriptor follow-up: POST /api/games/{id}/activation-payments accepts the
ActivateAbilityDto shape and returns PaymentChoice entries (index, label, deferred)
in the default or requested legal order. Authentication supplies the seat. Missing
tables/nonseated callers receive 404; refused preview requests receive 400.
The endpoint is read-only and returns no card ids, oracle ids, candidates or card
text. A descriptor is not a promise that the eventual payment will succeed.

CastingSystem.ActivationPayments shares ActivationSource validation with execution
(ability, zone, controller, priority and sorcery timing), rejects pending/finished
states, and uses CostPaymentOrder.Prepare. Labels describe public costs without
evaluating hidden card data. Expanded indices and deferred classification come
from the same bound entries used for execution. Execution always revalidates the
command against the then-current state; preview does not reserve anything.

Client work still required: consume descriptors for library-related activations,
offer ordering within groups, preserve selections and send PaymentOrder. No client
source or UI was changed in this increment. Rules suite: 1,706 passing
(library-payment-descriptors-rules-final.log).

Discovery prerequisite: the board currently receives ability text, tap/mana flags,
target count and variable-mana flag, but no command-specific expanded payment
descriptors. An order control must receive those indices/groups from the server;
it cannot safely infer them from ability text. Existing CostChoices is used by
the interpreter and returns object-selection questions, not payment-order metadata.

Inspection found a divergence: CastingSystem.Choices expanded only one ChooseCost
level, whereas execution recursively expanded nested Costs and ChooseCost. The
new nested library/discard regression returned zero questions instead of two
(library-choice-expansion-red.log). Discovery now calls CostPaymentOrder.Expand
and advances the selected-object cursor using the shared ObjectCosts count. Both
activation and casting discovery carry the command's selected objects so automatic
alternative probes match execution. This removes the separate expansion loop;
it does not add a client-facing endpoint or reveal hidden library identities.
Verification: 1,705 rules / 814 API tests pass in
library-choice-expansion-rules-final.log and library-choice-expansion-api.log.

Explicit-order backend follow-up: ActivateAbility.PaymentOrder is an optional
permutation of zero-based indices after Costs/ChooseCost expansion, before default
grouping. CostObjects still binds in expanded written order. CostPaymentOrder
validates exact length, uniqueness, range and the ordinary-before-deferred boundary
before execution; it reorders bound entries together. Omission retains stable
default grouping. Alternative probes clear the outer order because they test only
one candidate cost. Resume executes the already ordered queue rather than applying
the original indices to a shorter remainder.

ActivateAbilityDto now carries PaymentOrder and CostObjects (each bounded to 128
entries) to the authenticated engine command. This is backend capability only:
the client does not yet offer payment-order discovery or a selection control.
Spell ordering is also outside this increment; no general casting UI was added.

One normal-command regression reproduced ignored order before implementation
(library-explicit-order-red.log). It now exiles the selected library card before
milling the top card, following the life payment. Five invalid-order cases refuse,
and the saved-pause test now uses an explicit nondefault order. All 1,703 rules
tests pass (library-explicit-order-rules-final.log). API evidence follows in the
acceptance ledger. General activation replacement pauses remain gated.

Compatibility follow-up: the serialized pause test now runs both with bindings
and with the ObjectSelections property physically omitted from JSON. The latter
deserializes to null and resumes with the flat selected-object list. A separate
legacy queue verifies library exile/discard regrouping and replay. Two malformed
slot-count cases verify refusal before execution. All 1,697 rules tests pass
(library-payment-binding-compatibility-rules.log); no runtime changes were needed.
This supersedes the omitted-field test gap in the historical checkpoint below.

Selected-library follow-up: CostPaymentOrder now owns expansion, selection
binding, and stable grouping. It slices announced objects in written cost order
using ObjectCosts.SelectionCount (the existing Choice descriptor plus the
selected-counter case), then groups mill and library exile after ordinary costs.
Random discard is classified in the deferred group but remains unsupported by
payment execution. Alternative probes see the selection cursor after preceding
costs; they do not reuse the first cost's selected objects.

RemainingCostPayment.ObjectSelections stores each unpaid cost's slice, including
empty/partial slices. Resume uses these bindings directly and rejects a mismatched
cost/selection count. Older journals without bindings use the existing flat list
through preparation; a dedicated omitted-field compatibility test is still due.

Five new cases cover direct/alternative library exile after discard (both failed
before implementation in library-selected-order-red.log), partial selection
isolation, bound selections through a serialized replacement pause, and automatic
alternative selection after a preceding chosen-object payment. The existing
selected-exile continuation fixture now expects life and discard before its pause.
All 1,693 rules tests pass (library-selected-order-rules-final.log). No normal
activation-pause gate was opened. All 813 API tests also pass
(library-selected-order-api.log). Explicit player-selected order remains pending.

The following mill-only checkpoint is historical; selected-exile grouping and
binding limitations mentioned there are superseded by this follow-up.

Implementation follow-up: CostPayment.Begin now expands alternatives before a
stable mill-last partition. Resume uses the same path. Four new regressions cover
direct/alternative mill preceding life in written text, relative order of direct
and nested-alternative mill, and unpaid mana preventing any library events.
The first two failed before the change (library-payment-order-red.log).
The full rules suite passes 1,688 tests (library-payment-order-rules-final.log).
All 813 API tests pass (library-payment-order-api.log).

Older tests now expect all ordinary payments before the mill pause. The
mill-before-sacrifice fixture now proves sacrifice removes the source's own
replacement before milling, then recovery restores the source while retaining
the mill. Mana/counter fixtures provide enough resources for both ordinary
payments up front; they no longer claim resources generated during a later mill
can fund those earlier payments.

This is not complete CR 601.2h support: selected library exile costs still need
per-cost object binding before they can be reordered; random costs and explicit
player ordering within groups remain unfinished. The mill partition does not
reassign named objects because mill consumes no named-object selections. The
existing selected-exile continuation fixture remains compatibility evidence,
not proof of correct grouping. No runtime prompt gate was opened.

Primary source: [Wizards Comprehensive Rules, September 25, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt),
linked from the official rules page on the inspection date.

CR 601.2h requires nonrandom costs that do not move library objects to public
zones to be paid before the remaining costs. Players choose order within each
group. CR 602.2b applies that procedure to activated abilities.

At the initial audit, source contradicted that requirement: CostPayment.Begin flattened costs
and consumed index zero in written order. ChooseCost expanded an alternative at
that same position. Resume reused Begin. No payment-group ordering appeared in
this path. The Pay summary explicitly promised written order.

Consequently, the latest test named
Recovery_restores_movement_after_an_earlier_retained_property_change, whose
costs are Mill(1), SacrificeSelf, Mill(2), is evidence of restoration mechanics
under the current queue, not evidence that this is a legal payment sequence.
Its normal-command acceptance must not be cited as rules compliance. The two
mill costs require the sacrifice to precede them under the payment grouping.

Next implementation requirements:

- Reproduce the ordering defect through a normal activation with a mill cost
  written before an ordinary cost; assert actual emitted payment event order.
- Resolve announced alternatives before classifying their constituent costs.
  Do not classify an entire ChooseCost by its unselected alternatives.
- Preserve player-selected order within each legal group, and preserve the
  resulting queue across replacement pauses and serialized resume.
- Keep chosen-object assignments attached to their costs when ordering changes;
  do not silently reassign the shared chosen-object queue.
- Apply the shared ordering mechanism to relevant existing callers, checking
  which calls represent activation/casting payments versus effect instructions.
- Reclassify synthetic recovery fixtures separately from legal command tests.
  Passing recovery fixtures cannot waive the payment-order requirement.

## Retained dependency policy remains unproven

CR 733.1 cancels payments when an action cannot be completed, with explicit
library-operation exceptions and a dependency exception for legal mana abilities.
It does not explicitly establish a general rule to retain every earlier payment
that touched an object affected by a later protected library operation.

PaymentRecoveryPlan currently retains a whole CostPaymentStep if any event is a
protected library operation. PaymentRecovery refuses movement restoration when
a later retained step changed either movement identity. These are implementation
boundaries; they do not prove that whole-step retention is the correct action
granularity for every compound replacement.

Do not replace the refusal with automatic transitive retention without resolving
that distinction. Audit legal payment order first, then build remaining dependency
cases from legal sequences. CR 400.7 also requires distinguishing the departed
object from the arrival; transferring arrival properties to a restored original
identity cannot be justified merely because both represent the same card.

Normal activation replacement prompts remain gated. The initial audit changed
no runtime behavior. Follow-up changes and verification are recorded above;
neither establishes MTGO server behavior.

## Earlier payment-pause gate revalidated (2026-10-23)

Current normal command boundary remains CastingSystem.ActivateCore: permit a pause
only after a final MillCost/library ExileFromZoneCost with no unpaid costs, unless
using the internal test seam. CostPayment.Continue records each paused step as
Completed=false; ResumeActivationPaymentProgress appends operation events and marks
it completed only after the pending operation ends, then attempts remaining costs.

PaymentRecovery.Apply rejects missing journals, unsupported steps, retained changes
touching objects being restored, failed movement restoration, and unsupported mana
or object-property folds. LibraryMillCostTests explicitly contains conflict refusal
and unsupported-journal checks (including lines 1763–1773 and 1879–1886). Therefore
successful internal recovery examples do not establish that all later failures can
terminate a normal activation safely. Simply changing allowPause would expose these
known refusal paths after earlier decisions have already been committed.

Next implementation evidence needed before broadening this gate: an engine-generated
replacement sequence that creates a retained/reversible object dependency, followed
by a failed later cost; serialize at the earlier pause, answer through Game.Apply,
and demonstrate a defined terminal recovery state (no stuck payment, no duplicated
retained library operation). Existing constructed-conflict refusal tests are an
oracle for the limitation, not completion evidence. The eventual implementation must
also account for incomplete cost steps rather than treating a partial step as a
completed reversible payment. This pass changed no gate or production code and
claims no new test run.

## Engine-generated recovery dependency reproduced (2026-10-23)

LibraryMillCostTests.Earlier_payment_pause_refuses_recovery_when_replacement_moves_sacrificed_source_into_library
uses only test definitions: sacrifice the activation source, pause at a mill replacement,
move the sacrificed artifact from graveyard into library, shuffle, then fail a later
mill-three cost. Event serialization/rebuild occurs at the pause. The detached
operation and payment progress prove the retained library move consumes the exact
object created by the earlier sacrifice; there is no hand-edited dependency journal.
Assessment has no unsupported steps, but PaymentRecovery.Apply refuses the object
dependency. AnswerChoice through Game.Apply also refuses. Normal activation continues
to reject the earlier pause before committing it.

This is a passing characterization of an unresolved limitation, not successful
recovery or authorization to open the gate. The required terminal recovery policy
for a reversible action whose result was consumed by an irreversible library step
remains to be implemented and rules-validated. Focused test passes in
library-payment-dependency-live-events.log; full rules 1,791 pass in
library-payment-dependency-rules.log. No production changes, API rerun or commit.

## Rules basis for dependent payment recovery (2026-10-23)

Checked Wizards' current rules download, effective September 25, 2026:
https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt
(linked by https://magic.wizards.com/en/rules).
CR 118.11 treats a replacement-modified payment as paid. CR 733.1 requires reversal
of an incomplete action subject to its library-operation exceptions; CR 733.2 restores
priority. The local rules asset agrees on these points and was not edited.

For the reproduced dependency, restoring the sacrificed source would require undoing
a later retained library move or duplicating the physical card. Neither is an acceptable
recovery result. Retaining the dependent sacrifice is a candidate implementation
inference, not an explicit dependency algorithm stated by those rules. Before changing
classification, test the dependency chain and independent refundable costs separately:
retain the actual library card/order, never create a second source, refund unrelated
payments, clear the failed activation and pending prompt, restore priority, and filter
triggers consistently with whichever actions were reversed. Partial multi-object costs
need separate treatment; retaining a whole step merely because one object depends on
it could incorrectly retain unrelated payments. The normal pause gate stays unchanged.

## Single-object retained dependency recovery (2026-10-23)

PaymentRecoveryPlan now retains a previously reversible pure single-object movement
when its arrival object is consumed by a later retained move into the library.
It reuses PaymentMovementRecovery.Describe/Properties; steps with player changes,
residual object properties or multiple movements are not promoted wholesale. This
implements the dependency interpretation described above without broadening normal
activation pauses. Multi-object and transitive dependency recovery remain open.

The engine-generated regression now includes an independent life payment and expects
successful recovery through AnswerChoice after serialized pause: life refunded,
priority restored, no pending choice/activation stack object, original source absent,
exactly one returned source ID in the unchanged two-card library, and only the life
step reversed. It failed with Refused before production changes
(library-dependent-sacrifice-red-final.log). A later test assertion incorrectly used
the fixture's shared Oracle ID; it was corrected to the actual physical arrival ID.
Full rules 1,791 and API 837 pass in library-dependent-sacrifice-rules-final.log and
library-dependent-sacrifice-api.log. This supersedes the earlier refusal characterization
for this specific pure single-object dependency. No general pause support or commit.

## Retained movement identity chains (2026-10-23)

The dependent-sacrifice regression now runs direct graveyard-to-library and
graveyard-to-exile-to-library variants. The latter failed before implementation
(library-dependency-chain-red.log). PaymentRecoveryPlan follows successive ObjectMoved
IDs through chronologically ordered retained events to detect the library dependency.
This preserves the single-pure-movement restriction and does not generalize to partial
multi-object reversal or dependency chains through separately reversible payments.
Both variants replay the pause, refund independent life, retain exactly one source,
clear the failed activation and preserve the resulting library order.
Full rules 1,792 and API 837 pass in library-dependency-chain-rules.log and
library-dependency-chain-api.log. Normal earlier pauses remain gated. No commit.

## Mixed-object dependency boundary (2026-10-23)

Two additional engine-generated/serialized cases sacrifice the artifact source and
an independent land in one cost. Only the artifact returns to the library (directly
or via exile). Both verify the whole sacrifice step is not promoted to Retain, the
independent land remains in graveyard in the unrecovered intermediate state, and
recovery/AnswerChoice explicitly refuse. These are boundary characterizations,
not successful partial recovery. Full rules 1,794 pass in
library-mixed-dependency-boundary-rules.log; no production changes or API rerun.

The implementation dependency is now concrete: Game.Settle filters immediate and
deferred triggers by PaymentEventOrigin (journal, step), and PaymentRecoveryApplied
lists reversed step origins. Restoring only the independent land while marking the
whole step reversed would also suppress the retained source's sacrifice triggers;
marking no step reversed would preserve triggers of the undone land sacrifice.
Partial movement restoration must therefore include event-level reversal provenance,
its serialized representation, and both trigger-filter paths. Merely relaxing the
Count:1 guard is incorrect. The normal earlier-payment pause gate stays closed.

## Event-specific reversal contract (2026-10-23)

PaymentEventOrigin now has optional EventIndex; null retains legacy whole-step
meaning. PaymentTriggerRecovery.IsReversed is shared by immediate occurrence and
deferred-trigger filtering. An exact event reversal leaves other events and unknown
legacy origins intact; a whole-step reversal also matches event-specific origins.
Serialized pending-trigger tests cover those distinctions; settlement tests exercise
both immediate and deferred paths without creating life-gain triggers for refunds.
The new whole-step compatibility test failed under the former exact-equality matcher
(library-event-provenance-red.log). Full rules 1,796 and API 837 pass in
library-event-provenance-rules.log and library-event-provenance-api.log.

This supplies the filtering/serialization contract only. Production payment emission
still uses whole-step origins, and recovery does not yet emit partial event reversals.
Stable event numbering across resumed segments and partial restoration must be wired
and verified before mixed-object recovery or broader pause support. No commit.

## Production payment event numbering (2026-10-23)

CastingSystem now emits EventIndex for each non-control payment occurrence, numbered
within the compact step journal. Outcome dictionary keys still use raw event positions;
ChoiceRequested/ChoiceAnswered do not consume journal indices. Resumed segments start
at the saved interrupted step's event count. Four engine-generated dependency cases
check initial and resumed origin indices against exact journal events after pause
serialization. All four failed before implementation (library-event-numbering-red.log).
Full rules 1,796 and API 837 pass in library-event-numbering-rules.log and
library-event-numbering-api.log. Whole-step reversal remains compatible through the
shared matcher. Partial movement restoration/reversal emission is still unfinished;
normal earlier pauses remain gated. No commit.

## Partial pure-movement batch recovery (2026-10-23)

PaymentRecovery can now separate retained library-dependent movements from independent
movements in a pure movement cost. It restores only independent objects, removes retained
source IDs from positional anchors, and emits exact journal-event reversals for the
restored ObjectMoved/ObjectLeftBy occurrences. Unrelated life payments still refund.
ActivationPayment.HasEventOrigins distinguishes newly indexed journals; partial recovery
refuses older unindexed journals rather than retaining unknown deferred triggers silently.
Mixed player/property steps and non-movement occurrences remain outside this path.

The two previously refusing mixed-batch cases failed with expected Applied/actual Refused
before implementation (library-partial-movement-red.log). They now pass, along with two
reversed-selection-order cases. Serialized tests verify independent land restoration,
retained source identity/library order, correct movement history, exactly the retained
source's leave-battlefield trigger, and legacy refusal. Full rules 1,798 / API 837 pass
in library-partial-movement-rules-final.log and library-partial-movement-api.log.
Normal earlier pauses remain gated; this is internal continuation/recovery acceptance,
not a claim that every interrupted cost is supported. No commit.

## Three consecutive serialized payment pauses (2026-10-23)

Extended the engine-generated replacement-mana recovery theory to three library
cards, each requiring a separate replacement choice. Four cases cover ordinary
and restricted mana, with and without a second independent mana payment. Every
choice resumes from serialized/replayed events. Assertions verify three retained
shuffles and replacement spends, the correct refunded pool and provenance, the
unchanged library count, no pending choice or stack object, and restored priority.
All 1,802 rules tests pass in library-three-pause-rules.log. This is internal
continuation coverage; the normal earlier-payment pause gate remains unchanged.
No production code changes, frontend changes, or commit in this increment.

## Deterministic dependency propagation across payment steps (2026-10-23)

A new recovery-boundary regression constructs three recorded movement steps:
battlefield to graveyard, graveyard to exile, and exile to library. Across 32
journal identities, the original hash-set traversal left the earliest movement
classified as reversible for journal 3 (library-dependency-order-red-final.log).
PaymentRecoveryPlan now visits candidate steps in descending payment order, so
promotion of the middle dependency is visible when examining the earliest one.
The test verifies all three retained steps, no reversed origins, and serialized
recovery replay preserving the sole library object without restoring the source.
This is a constructed journal/reducer test, not normal activation or browser
coverage. General mixed-step dependencies and earlier-pause admission remain open.
Full rules: 1,803 passed in library-dependency-order-rules.log. No commit.
API compatibility: 837 passed in library-dependency-order-api.log.

## Separate-cost dependencies through the payment engine (2026-10-23)

Two additional cases now exercise actual internal activation payment, rather than
constructed movement journals: sacrifice, exile the sacrificed artifact from the
graveyard as a separate cost, then return it to library in a paused mill replacement.
One case sacrifices only the source; the other also sacrifices an independent land.
Both serialize the paused events, answer through Game.Apply, and replay recovery.
Assertions verify the separate exile identity chain, retained library order, life
refund, event-origin indices and cleared prompt. The mixed case additionally proves
independent land restoration, retained source trigger and refusal of legacy journals.
Initial fixture failures were caused by ambiguous exile selection and a filter with
the wrong default zone; these were corrected to an explicit graveyard artifact filter.
Full rules: 1,805 passed (library-separate-cost-rules-final.log). Production behavior
and earlier-pause admission are unchanged; no API/client changes or commit.

## Interrupted-operation failure boundary review (2026-10-23)

Source inspection distinguishes two failure locations. CostPayment.Continue catches
RefusedException from a new One call and returns the state/events accumulated from
previous completed costs; the failing call contributes no outcome. In contrast,
ChoiceSystem.Resume computes the resumed effect, deferred draw/zone/shuffle work,
and only then invokes RetainPayment. A refusal thrown before that invocation does
not reach ResumeActivationPaymentProgress or PaymentRecovery through this path.
Current continuation tests chiefly prove later-cost failure after successful return
of the interrupted operation. They do not establish recovery of failures inside it.

This is a source-level coverage gap, not a reproduced stuck-game defect. Next proof:
construct an admitted final-library-cost choice whose selected replacement cannot
finish, then inspect refusal, pending state, committed effects, retry and reload.
Do not broaden the normal activation gate on the strength of later-cost tests.
The main implementation document now separates current readiness from historical
checkpoints to avoid treating stale notes or suite totals as current requirements.
No production code changed in this audit; tests were not rerun for documentation.

## Unpayable replacement payment is handled (2026-10-23)

The first candidate for an inner-operation refusal does not reproduce a defect.
EffectInterpreter.Pay catches RefusedException and returns unpaid; Paid skips Then.
Two new normal-command tests activate a final MillCost with two competing replacement
sources, each offering an unaffordable green payment followed conditionally by shuffle.
Each replacement option completes after serialization/reload: library unchanged,
graveyard empty, exactly one activation on stack, no pending choice, priority returned,
no mana-change/shuffle/recovery event. Repeating the answer is refused.
Full rules 1,807 pass in library-unpaid-replacement-rules.log. No production change.
The static throw boundary remains a question for genuinely throwing operations, not
an established bug or reason to rewrite this working unpaid-effect behavior.

### Diagnostic refusal inside a resumed library payment — 2026-10-23

Added two normal-command tests in LibraryMillCostTests using an explicit Unsupported
effect solely as diagnostic fault injection. Both begin a supported final mill cost
after paying two life, serialize the replacement prompt, and answer with the failing
replacement. One replacement attempts a shuffle before throwing; the other throws
immediately. Game.Apply returns Refused with no event stream. Replaying the committed
prompt and choosing the supported alternative completes the activation, preserves
original library order/RNG, charges earlier life only once and emits no recovery event.

This reproduces the exception boundary without demonstrating a lost committed event
or an ordinary legal-card failure. A supported alternative remains available. It does
not prove automatic recovery when every offered alternative fails, nor authorize
broader cost-pause admission. No production behavior changed. Full rules suite: 1,811
passed, zero failures (`library-refused-resume-rules.log`). Initial compilation found
one required initializer-formatting correction, fixed before the passing run.

### Independent life reversal inside a retained movement step — 2026-10-23

PaymentMovementRecovery.Partition previously refused any dependent movement step
with player changes. The direct chain test reproduced that refusal for a completed
sacrifice/life step followed by exile and retained library return
(`library-mixed-life-dependency-red.log`). Partition now permits validated LifeChanged
occurrences alongside movement/leave markers. It retains dependent movement origins
and reverses only life occurrence origins plus any independently restored movement.
Other property kinds remain gated. Describe still validates complete snapshots, and
partial partition still requires event-origin provenance.

PaymentDependencyRecoveryTests covers both pure movement and mixed life across 32
journal identities. The engine-generated LibraryMillCostTests variant has a conditional
sacrifice replacement gain three life and move the artifact, followed by a library
return/shuffle and a failing later mill cost. Serialized recovery retains the library
card and reverses independent life, leaving no activation on the stack. Full rules
1,813 pass (`library-mixed-life-dependency-rules.log`). Normal activation admission
has not been broadened.

Separate issue discovered during fixture development: without its explicit life
condition, the sacrifice replacement's nested Move repeatedly reapplied the same
replacement and aborted the test host through recursion. The bounded condition isolates
recovery here; it is not a fix for replacement-history propagation on nested movement.
That recursion needs its own bounded regression and production-path investigation.

### Direct nested movement history regression and fix — 2026-10-23

Changed the conditional sacrifice replacement fixture to gain one life while its
condition allows up to three applications. The new Assert.Single on its life events
failed with three occurrences (`library-nested-move-red.log`), reproducing the same
lost-history path without stack overflow. The interpreter's direct Relocate path now
forwards Context.UsedReplacements through the full zone Move overload, ApplyMove and
Offer to the existing replacement system. The face-down recording test adapter follows
the updated optional argument. No new replacement registry or movement algorithm.

All 1,813 rules tests pass (`library-nested-move-rules.log`), including the bounded
single-occurrence check and serialized mixed-life recovery. This fixes the reproduced
direct nested Move path. SearchRuntime's batchBoard path still invokes MoveInBatch
without history and needs separate evidence/forwarding; deferred movement history
must also be examined before claiming all movement replacement recursion is resolved.

### Search batch inherits replacement history across reload — 2026-10-23

LibraryNestedMovementTests reproduces a library-to-hand movement replaced by gain-one
life plus search-to-hand. A life threshold bounds repetition. Before the fix the
serialized search choice returned three times instead of once
(`library-nested-search-red.log`). Relocate now supplies inherited history to
MoveInBatch. ZoneSystem implements that seam through its existing MoveAll execution
with the original evaluation snapshot and replacement history. Public multi-move
execution retains its existing default; the history-bearing private call currently
serves a single coordinated search member. The interface fallback explicitly refuses
nonempty history instead of silently discarding it in an unsupported implementation.

The regression replays accumulated events before each answer and checks one search
answer, one life occurrence, one card in hand, empty library and expired inspection.
Full rules: 1,814 passing (`library-nested-search-rules.log`). Search participants
continue through the interpreter rather than an independently reconstructed batch.
Standalone deferred batches and replacement-created cost movements remain separate
paths to audit; do not copy consumed per-card history indiscriminately onto later
independent movements.

### Replacement-created mill cost inherits parent history — 2026-10-23

A bounded normal zone operation reproduced three gain-life occurrences when its
replacement executes PayCost(MillCost(1)); expected one
(`library-replacement-cost-red.log`). Interpreter Pay now supplies its inherited
replacement set to CostContext. Mill and selected-object movement forward that set
to the shared zone batch operation; source movement forwards it to the existing
full Move overload. CostPaymentContext capture/restore includes the optional set.
Null history retains existing behavior, including interface fallbacks.

The new LibraryNestedMovementTests case verifies one life occurrence, one movement,
empty library, one graveyard card and serialized replay. Full rules suite: 1,817
passed (`library-replacement-cost-rules.log`). No second replacement algorithm or
per-card definition was added. Replacement-created PayCost still treats a pending
cost continuation as unsupported/unpaid; that existing restriction has not been
removed. General history-bearing multi-move batches that themselves pause require
separate continuation verification before broader admission.

### Interpreter PayCost continuation design boundary — 2026-10-23

Read Paid, Pay, MayPaid, Otherwise, Unpaid, ProposeThrough and
ChoiceSystem.FinishZoneBatch together. Pay currently invokes CostPayment.Pay, which
throws for RemainingCostPayment; Pay catches RefusedException and returns null. Thus
a replacement-choice pause is treated as unpaid, alongside an actually unpayable cost.
This is source evidence, not yet a new runtime regression.

Simply replacing CostPayment.Pay with Begin is insufficient:

1. ProposeThrough wraps a pending proposal into an interpreter-coordinate choice,
   but does not currently copy DeferredZoneMoves, DeferredMoveHistory or unpaid-cost
   continuation into its outer choice contract.
2. On answer, ProposeThrough resumes the selected replacement and proceeds with the
   interpreter's following child. ChoiceSystem finishes deferred zone batches only
   after resumable.Resume returns. PayCost.Then could therefore run before the full
   cost batch finishes if pending data alone were copied through.
3. Pay checks that its context source still exists before resuming. A source consumed
   by an earlier cost segment must not make a committed continuation disappear.
4. MayPaid and Paid share Pay. A paused result must remain distinct from unpaid so
   optional Else/Unpaid bookkeeping cannot run while payment is unresolved.

Implementation path: extract the existing deferred-batch completion/retention into a
shared coordinator with no turn-advance responsibility. ChoiceSystem keeps ownership
of AdvanceAfterZoneBatch. Interpreter payment resumption calls the same coordinator
before deciding whether payment completed and before entering Then. Capture the cost
announcement once; preserve its snapshot through reload rather than re-evaluating a
consumed source. Use CostPayment.Begin/Resume for unpaid steps instead of introducing
another payment loop. Keep completed, paused and refused outcomes distinguishable.

Required next regression: a library mill payment of two cards with two replacement
candidates per card, a visible Then effect, and serialized replay before every answer.
Then must not occur after the first card, both cards must move once, inherited history
must remain private and correct, and Then must occur exactly once only on completion.
Also cover truly unpayable cost (Else/Unpaid), a second pause and a consumed source.
No production code or tests changed in this source investigation; current passing
baseline remains 1,819 rules and 840 API tests.

### Effect mill payment resumes before Then — 2026-10-23

LibraryEffectPaymentTests reproduced both one- and two-card payments resolving as
unpaid without offering replacement choices (`library-effect-payment-red.log`).
Interpreter Pay now begins through CostPayment.Begin inside ProposeThrough. Initial
refusals remain unpaid; resumed-operation failures propagate rather than being
silently converted into an unpaid original instruction. Source lookup occurs only
when starting payment, not when resuming the committed proposal.

A private EffectPaymentContinuation carries the raw operation context separately
from the outer choice's deferred work. On answer, CompleteEffectPayment invokes the
shared ZoneBatchContinuation before returning to Paid/MayPaid. A subsequent pause
retains the payment announcement. Halted payment does not enter Then. Keeping the
batch inside the interpreter continuation prevents ChoiceSystem from running the
same deferred cards again after the interpreter already completed them. ChoiceSecrets
removes this private context from all projected choices.

Tests replay before each card's replacement answer, verify life/Then and stack
completion are withheld until all cards finish, assert exactly one Then and one
movement per card, and check both viewers' private context redaction. A separate
unpayable mill-two from a one-card library test verifies no partial movement or Then.
Full rules: 1,822 passed (`library-effect-payment-rules.log`).

This admits a paused cost whose flattened unpaid-cost list is empty; multiple later
unpaid steps are still rejected. Optional MayPay, replacement-created PayCost and
consumed-source continuation need dedicated follow-up evidence. No normal activation
admission gate was removed; arbitrary partial-failure recovery remains unfinished.

### Later effect-payment steps resume through the shared owner — 2026-10-23

Two regressions reproduced PayCost/MayPay treating a payable pair of MillCost(1)
steps as unpaid when the first operation paused (library-later-effect-cost-red.log).
Pay now retains that pause even when unpaid steps remain. CompleteEffectPayment
finishes the current movement batch, then delegates the stored announcement and
remaining costs to CostPayment.Resume. A second pause carries its own remaining
payment; the interpreter does not introduce another cost loop or recalculate the
source. Then remains withheld until payment finishes.

Mandatory and optional two-step payments pass serialized replay/privacy checks.
A three-step case also removes the ability source after the first pause and passes
all later choices with fresh subsystems. A diagnostic unpayable-second-mill case
proves the answer throws without returning partial events, life gain or resolution.

IMPORTANT: that diagnostic is a known incomplete behavior, not correct final
recovery. With one card and two separate mill costs, the current implementation can
leave the payment prompt pending indefinitely when no replacement answer can make
the later cost payable. Recovery/Else requires an effect-payment journal for prior
committed steps. This change is work in progress and is not ready for commitment or
completion acceptance. The normal activation admission gate remains unchanged.

Rules suite: 1,833 passed (library-later-effect-cost-rules.log).

### Effect-payment journal capture — 2026-10-23

RemainingCostPayment now has a private EffectJournal, using PaymentRecoveryJournal.
The interpreter captures initial CostPaymentProgress steps when a payment pauses.
On each answer it completes the entire deferred movement batch, merges the resumed
operation's before/after changes into the interrupted step, and appends later cost
steps from CostPayment.Resume. Every new pause carries the updated journal inside
the existing private EffectPaymentContinuation/RemainingPayment structure.

ChoiceRequested and ChoiceAnswered events are excluded from journal history to
avoid recursive continuation growth. Existing choice redaction removes the parent
continuation and remaining-payment objects, including the journal. Replay tests
assert step counts, completed-step counts, exact movement counts, journal identity,
absence of control events and privacy across single/multiple mills, optional payment
and source-removal variants. Full rules: 1,835 passed (library-effect-journal-rules.log).

HasEventOrigins is explicitly false: interpreter outcomes still need provenance
propagation before event-level recovery/trigger cancellation can rely on it. Recovery
is not invoked yet; the known unpayable-later-cost pending prompt remains unresolved.
Legacy continuations with no EffectJournal retain their previous behavior.

### Shared payment-step origin mapping — 2026-10-23

PaymentOrigins now owns event/step indexing formerly implemented in activation
payment helpers. Activations delegate to it. Effect payment capture uses the same
mapper for initial paused steps and subsequent costs. Resumed operations continue
the interrupted step's occurrence numbering from its recorded history; later steps
start at the journal's existing step count. Existing nested origins take precedence
over outer annotations rather than being overwritten.

Expanded LibraryEffectPaymentTests assert library movements carry the same journal
identity and expected step after each replayed answer. Follow-up life gain, stack
completion and choice control events remain untagged. All mandatory, optional,
multiple-cost and source-removal variants pass. Rules: 1,843 passed
(library-effect-step-origins-rules.log).

Effect journals still declare HasEventOrigins=false: overlapping nested-payment
ownership needs explicit recovery assessment before claiming complete event coverage.
Recovery invocation/unpaid branching remains unfinished, so the pending-prompt
failure is not fixed by origin tagging alone.

### Interrupted effect-payment failure now reaches unpaid branch — 2026-10-23

CompleteEffectPayment now passes a failed continuation and its completed journal
to the shared PaymentRecovery owner, preserving resolution priority. ProposeThrough
reports recovered unpaid status to Pay. Pay returns the resulting run and payment
status together; Paid/MayPaid retain recovery events/state while skipping Then or
executing Else. Previously a null unpaid result would have discarded recovery.

The old stuck-prompt diagnostic is replaced by four acceptance cases: one/two
library cards, mandatory/optional payment, life paid before repeated MillCost(1),
and an additional mill that cannot be paid. Each answer uses serialized event
replay and fresh subsystems. The final prompt is clear, paid life is refunded,
completed library movements are retained, the spell resolves once, Then never runs,
and optional Else gains seven life exactly once. Initial assertion failures counted
only milled cards in the graveyard; the resolved spell is now explicitly included.

PaymentRecoveryJournal tracks origins belonging to another journal. Effect recovery
refuses overlapping nested journals rather than applying a parent-only reversal.
Unsupported recovery plans and legacy continuations without a journal also retain
the existing refusal boundary. These cases still need support/acceptance. Initial
unpaused partial-payment failure is separate and remains to be audited. Normal
activation admission gates are unchanged; this is not full payment completion.
Rules suite: 1,846 pass (library-effect-recovery-rules.log).

### Immediate effect-payment failure shares recovery — 2026-10-23

Four new cases reproduced the initial Pay failure branch discarding completed mills
when the following mill failed without a pause (library-immediate-recovery-red.log).
The branch now uses the same RecoverEffectPayment helper as resumed failure. It
reserves a journal from NextChoiceId, annotates completed cost events and invokes
shared recovery. A failure with no completed steps still returns unchanged/unpaid.

Eight immediate/paused mandatory/optional one/two-card cases pass. Replayed results
retain completed library movements, refund prior life, leave no pending choice,
skip Then, execute optional Else once, and resolve the spell once. The test accounts
for direct interpreter resolution versus ChoiceSystem finishing the spell itself.
Rules suite: 1,850 passed (library-immediate-recovery-rules.log).

This closes the reproduced initial partial-mill failure, not every possible cost
combination. Overlapping nested journals and unsupported recovery plans remain
refused. Trigger cancellation across recovery still needs full engine acceptance.
