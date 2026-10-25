# Library payment recovery across simultaneous movement

Status: recovery evidence implemented for the reproduced batch; broader movement
and trigger acceptance remains incomplete. Traced and updated 2026-10-24.

## Implemented recovery evidence

The mixed-batch regression failed before the change (`library-batch-property-red.log`,
one failure, twelve passing cases). The shared payment journal now records
MovementDepartures by replaying each segment's events from its actual starting
state and capturing each object immediately before ObjectMoved. WithChanges
appends the evidence when a paused payment resumes. Object identities avoid
event-index renumbering when control events are removed from payment history.

This records the distinction at the payment journal boundary without changing
replacement evaluation, Departed, or existing turn-history semantics. The earlier
proposal to change the movement event itself is not necessary for this recovery
case; it would also affect consumers outside this task.

DepartureMatches uses the existing property validator to check that the actual
departure follows from the original object and property occurrences. It separately
requires the evaluation snapshot to equal either that actual departure or the
original batch object. Duplicate evidence refuses. Older records without evidence
use the previous strict property-to-Departed comparison; a mismatched legacy batch
is still unsupported rather than reconstructed by assumption.

The replay regression verifies the independent land is actually tapped before
movement although its simultaneous evaluation snapshot is untapped, then verifies
restoration, life refund, unchanged retained library order, and tap-event ownership.
Deleting, altering, or duplicating the land's evidence refuses assessment. All 184
LibraryMillCostTests pass in `library-batch-property-tests.log`. This does not prove
the separate missing-trigger case, reverse-selection variant, pause inside the
sacrifice batch, or live multiplayer acceptance. Public admission is unchanged.

Full backend validation: 1,955 rules tests in `library-batch-property-rules.log`
and 840 API tests in `library-batch-property-api.log`, all passing. No client code
changed and npm verification was not rerun for this backend change.

### Pauses inside the sacrifice replacement

Added one- and two-pause variants using optional empty effects between the taps
and movement. Each pause is serialized and reloaded; normal AnswerChoice commands
continue it, and replaying the prior answer refuses. The interrupted payment step
has no movement departures before it moves. After its final continuation the step
is complete, with exactly one captured departure per moved object. The subsequent
mill replacement and unpaid cost exercise the same recovery, exact event-origin,
restored land, life refund, retained library order and malformed-evidence checks.

All fifteen variants pass in `library-batch-paused-tests.log`. These are test-only
additions after the full-suite baseline above; no new production behavior or live
multiplayer acceptance is claimed. This closes the specifically tested forward
selection pause/resume gap, not reverse selection or the missing tap trigger.

### Reverse selection and post-departure property events

The reverse-selection regression reproduced an unsupported sacrifice step in
`library-batch-reverse-red.log` (one failure, fifteen passing variants). The land
had already departed when the artifact replacement emitted its tap event against
the original evaluation board. ObjectChanged.Apply ignores that removed identity;
the occurrence-time departure is correctly untapped. Recovery had nevertheless
required all source property events to precede the movement.

For journals with validated actual departure evidence, DepartureMatches now
validates only the events before that movement. Later properties on the removed
identity are no-ops: Describe requires disjoint movement identities and rejects
creation events, so that identity cannot reappear within an admitted step. Older
journals retain the explicit post-movement property refusal. The destination's
pre-arrival guard is unchanged. This changes recovery assessment, not movement,
replacement evaluation, or reducer behavior.

The regression checks an untapped departure, complete land restoration, retained
artifact/library state, life refund and the exact reversed tap occurrence. Missing,
altered and duplicate evidence still refuse. All 187 library mill-cost tests passed
in `library-batch-reverse-tests.log` before adding reverse-order one- and two-pause
variants to the full-suite run. The separate missing tap-trigger case and live
multiplayer acceptance remain outstanding.

Final validation includes both selection orders with zero, one and two pauses:
1,960 rules tests passed in `library-batch-reverse-rules.log` and 840 API tests
passed in `library-batch-reverse-api.log`. The API and client interfaces and public
activation admission were not changed. No client verification or live capture was
performed for this backend validation change.

### Tap-trigger observation at occurrence time

The retained-artifact tap regression is now executable rather than an exploratory
fixture. It failed with zero triggers instead of one in
`library-tap-observation-red.log` (one failure, eighteen passing variants).
Game.Apply now supplies the command's starting state to Settle when its event
stream does not include legacy deferred occurrences. TapTriggerObservation
replays that stream and delegates tap/untap detection to the existing trigger
system at the occurrence's state, before a later movement removes the object.
Detected triggers carry their original payment origins. Remaining event origins
are reindexed for normal final-state detection. Tap occurrences are removed from
that detection input, preventing duplicate detection; committed events are unchanged.

Recovery still uses PaymentTriggerRecovery to cancel reversed occurrences. The
existing independent-land tap cancellation and retained-tap cases pass. The new
artifact case checks one deferred trigger with exact source/payment ownership,
serialization, zero/one/two replacement pauses, and one fired trigger after the
dependent library movement remains retained. All 1,963 rules tests pass in
`library-tap-observation-rules.log`.

This addresses tap observation in the normal command path and callers supplying
Settle's before state. Legacy deferred event streams and direct Settle calls
without a starting state keep their previous behavior; no historical board is
invented. Other event kinds still use the existing observation rules. This is not
a claim that all trigger timing or every legacy log is solved. Live acceptance
and public activation admission remain outstanding.

API validation also passes: 840 tests in `library-tap-observation-api.log`.
No client code changed; no new npm verification or live capture is claimed.

### Four-player effect-path acceptance

Ran the isolated QA host with `--libraryPayment=true --libraryTapRecovery=true
--librarySeats=4 --port=4340` and the browser harness with `--tap-recovery`.
Table `e240698f-ad33-499f-8324-5c749db94708` completed successfully (browser
session 46629). All four authenticated seats received updates; three prompts
were answered through the UI after actor reloads. Every seat's private contexts
were checked, and opponents received no choice options.

The admitted spell-resolution PayCost path retained exactly one tap trigger,
one recovery and one shuffle, with no duplicate events after reload. Final life
was 40, library size 3, and the retained trigger resolved exactly once. All-seat
reloads before and after that resolution preserved views. No refusals occurred.
Inspected choice-2 landscape/portrait and resolved-seat-0 captures under
`tools/TableQa/library-tap-recovery-*.png`. The QA host was stopped afterward.

This fixture taps before a nested payment pause. It verifies live retention and
recovery across pauses; it is not a live reproduction of tap-and-move in one
uninterrupted batch. That case has rules-regression evidence above. The publicly
gated earlier activation path is still unverified live and was not bypassed.
Only the QA fixture/harness changed for this run; no application card definitions,
client code, or public admission rules changed.

### Live uninterrupted tap-and-move batch

The follow-up `--libraryTapBatch=true` host and `--tap-batch` browser run passed
on table `51390545-ad5d-42b6-a611-d5d023606dd5` (browser session 2660). This
fixture gains one life, taps the artifact and moves it to the graveyard in the
same replacement, without a choice between tap and movement. Its only prompt is
the subsequent mill replacement. The spell pays two life first, so the prompt
shows 39; recovery refunds the independent payments to restore 40. The artifact
remains in the three-card library after one shuffle.

All four seats confirmed one recovery, one retained tap trigger and one eventual
trigger resolution. Actor reload at the prompt and all-seat reloads before/after
trigger resolution preserved views and event counts. Private contexts and hidden
opponent options passed; refusals were empty. Inspected
`library-tap-batch-choice-1-landscape.png`, `library-tap-batch-choice-1-portrait.png`
and `library-tap-batch-final-seat-0.png` in tools/TableQa. The desktop capture shows
the retained Recovery watcher ability on the stack after recovery. Host stopped.

This closes live effect-path acceptance of uninterrupted tap-and-move observation.
It does not lift or verify the earlier interrupted activation admission gate.
Only QA fixture/harness code changed in this follow-up; the production test
baseline remains 1,963 rules and 840 API tests.

## Reproduction

Extend `Earlier_payment_pause_retains_dependent_sacrifice_and_refunds_independent_life`
with mixedBatch=true, sacrificeLife=true and sacrificeSourceTap=true. In the
sacrifice replacement, use two separate Tap instructions: one for lands and one
for artifacts (a combined Types mask requires both types and does not reproduce
this case). The selected artifact and independent land are sacrificed together.
The artifact replacement gains life, taps both objects and moves the artifact.
A later mill replacement returns the artifact to the library and shuffles; the
next mill cost fails. Before the change, recovery assessment marked the sacrifice
step unsupported.

The single-artifact and reproduced mixed variants are now supported by recovery.
The mixed variant needs distinct evidence:
its land movement was proposed from the original simultaneous board, whereas
the artifact's replacement Move was generated after its tap.

## Current source contract

- `ZoneSystem.MoveAll` passes its original state and running tape state separately
  to Proposal, then to ApplyMove. Proposal reads the departing object from the
  original board; allocation IDs come from the running state.
- `ApplyMove` evaluates replacement effects through
  `ZoneMoveSnapshot.WithCurrentAllocation`. That helper preserves the original
  board and replaces only allocation counters and RNG. Simply changing Departed
  would therefore not change the replacement evaluation board.
- If a batch pauses, its pending context records DeferredZoneMoves and a serialized
  ZoneMoveSnapshot. Resume evaluates replacements on that snapshot, then applies
  their emitted events to the actual current state. Any correction must cover
  both immediate and resumed movement, including serialization.
- `ObjectMoved.Apply` removes the actual current object, but records Departed
  preferentially in LeftBattlefield and Died history. The distinction therefore
  affects subsequent trigger/history consumers, not only recovery validation.
- `PaymentMovementRecovery.Describe` uses PropertiesMatch to apply all source
  property events to the payment's original object and compare the result with
  Departed. In this case it computes a tapped land and compares it with the
  original untapped batch snapshot, so it rejects the step before Partition runs.

## Required implementation decision and proof

Keep replacement eligibility evaluation and actual departure evidence distinct.
Do not relax PropertiesMatch to accept either snapshot indiscriminately: that
would also accept inconsistent event logs without establishing their batch origin.
Do not replace the entire evaluation board with the running board: that would
change simultaneous-movement behavior and which replacement effects are offered.

Determine which occurrence-time state must drive departure history and recovery,
then carry that evidence explicitly at the movement boundary if the existing
Departed contract cannot represent both responsibilities. Existing serialized
events without new evidence need an explicit supported/refused compatibility path.

Acceptance must include: immediate and paused/replayed batches; both selection
orders; independent land restoration; retained artifact/library order and RNG;
life refund; departure/arrival property event ownership; trigger cancellation and
retention; unchanged simultaneous replacement eligibility; malformed evidence
refusal; and normal public commands once admission is widened. The tap-trigger
creation gap recorded in LIBRARY_OWNERSHIP_AUDIT.md is a separate prerequisite
for claiming complete trigger behavior.

The source trace below the implementation checkpoint remains relevant to the
broader movement/evaluation distinction. The implemented journal evidence resolves
the reproduced recovery assessment, not every acceptance requirement above.
