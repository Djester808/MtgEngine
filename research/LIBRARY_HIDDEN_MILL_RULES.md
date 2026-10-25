# Hidden-destination mill trigger investigation

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

## Web follow-up — 2026-10-24

Rechecked the [official September 25 rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf):
701.17c permits following a milled card into a public destination; 603.10a uses
pre-event information for publicly visible objects entering hand/library.
Neither statement alone settles characteristic matching for the synthetic
unrevealed library-to-hand arrival. Search results offered community opinions,
but no new primary ruling for that discriminator. Do not change trigger policy
on that evidence or describe the entire library as blocked by this question.
The unresolved case remains explicitly tracked; this is not a passing test or
a claim of full rules coverage.

Status: partially implemented; revealed-top characteristic look-back has a focused
fix. General hidden-destination semantics remain incomplete. Updated 2026-10-24.

## Reachability check after resume — 2026-10-24

A structured recursive scan of the definition objects in all 634 top-level
cards/*.json files found no ReplacementAbility with a WouldMill event. The
replacement event kinds present were WouldEnterBattlefield, WouldCreateTokens,
WouldPutCounters, WouldDraw, WouldGainLife, WouldLoseLife, WouldDie,
WouldBeDealtDamage, WouldAddMana and PlayerWouldBeDealtDamage. The only card file
with a replacement body containing a direct Move to Hand or Library was Abundance
(eccc9a54-2b56-4a02-9926-258d5b2e25fb.json); its replacement is for drawing and
reveals cards before moving them. Definitions may repeat abilities at multiple
levels, so this scan is not a count of unique printed abilities.

Consequently the unresolved unrevealed mill-to-hand discriminator is currently
a synthetic generic-engine scenario, not a reproduced interaction from this
catalog. This does not settle its rules or certify every possible combination
of static effects, dynamically created abilities and future definitions. It does
prevent describing it as a known failure in an existing catalog deck. The shared
library contract still needs an explicit decision on that generic boundary;
no matching behavior was changed and no card definitions were added.

The official Eldritch Moon release notes describe Wheel of Sun and Moon's
replacement as revealing the card before putting it on the library bottom.
That actual-card example therefore cannot prove the unrevealed-hand case:
https://magic.wizards.com/en/news/feature/eldritch-moon-release-notes-2016-07-08

## Characteristic-free filter boundary — 2026-10-24

Four added cost/effect cases extend the existing redirected-to-hand replay test
with ObjectFilter.Any and an owner-only filter. All 38 HiddenMillDestinationTests
pass in library-hidden-known-filter.log. A matching trigger still occurs, but its
body cannot select/exile the hidden hand arrival. Owner hand access remains valid;
opponent projections and the pending choice contain no private oracle identifier.
This closes the known-facts/filter distinction for these two predicates without
changing matching or treating every non-null filter as private information.

The official Baldur's Gate release notes were searched for a Zellix ruling but
contain no Zellix entry; they do not resolve the remaining characteristic-sensitive
hand-arrival question. No secondary-source ruling was used to change behavior.
The broader case remains open, separate from this verified privacy boundary.

## Same-library replacement boundary — 2026-10-24

Four new public-command cases in LibraryMillObserverTests replace milling one
card with putting that top card on the bottom of the same library. Cost/effect
variants, with and without a serialized replacement pause, pass in
library-mill-reposition.log. They assert preserved card identities and reversed
two-card order, no CardMilled or TriggerFired, successful following instructions,
duplicate-answer refusal and no later settling trigger. No production change
was needed: LibraryMillContinuation requires an actual departure from library.

Rule rationale: the September 25 official PDF was rechecked for 701.17a and
603.2g. The replacement performs only an internal rearrangement; the original
mill event does not occur. This is an inference from those rules for this exact
replacement, not a blanket conclusion about replacements that move a card out
and subsequently return it. Source:
[Wizards Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf).
Unrevealed characteristic-sensitive arrivals remain a separate unresolved case.

## Public departure global-state regression — 2026-10-24

Paused-continuation follow-up: all twelve global-condition variants pass in
library-mill-global-condition-pauses.log. Eight added cases pause immediately
before or immediately after the replacement movement, for cost/effect milling
and revealed/unrevealed cards. Each committed batch is serialized and replayed;
normal AnswerChoice resumes the operation. Both viewer projections withhold
deferred triggers, a repeated answer is refused, and settling again emits no
duplicate firing. This closes the precise pause gap described below without a
production change. It does not certify unrelated hidden-characteristic policies.

The official September 25 PDF linked from Wizards' rules page confirms CR
603.10/603.10a's pre-event exception for a publicly visible object entering hand
or library. A new public-command regression gives a mill trigger an intervening
condition requiring one card in the library, then redirects that last card to
hand. Revealed effect/cost variants both failed while unrevealed controls passed
(library-mill-global-condition-red.log). An earlier SharesTypeWith test was
invalid because that predicate excludes the candidate itself; its failing log
is not evidence of this defect.

TriggerObservation now retains the local replay state immediately before each
event and supplies it to mill detection when the occurrence has bounded public
departure evidence and a hand/library destination. Other mills still use the
post-movement state. No additional state is serialized. This fixes the broader
query context at that shared observation boundary rather than adding a second
query implementation. Full rules suite: 2,065 passed in
library-mill-global-condition-rules.log.
The API suite also passes all 840 tests in library-mill-global-condition-api.log.

The four cases serialize committed command batches. A mid-replacement pause for
this precise global condition still needs verification; existing pause tests
cover source lifetime and card appearance separately. General unrevealed hidden
characteristic matching and library-to-library replacement remain open.

## Next implementation discriminator: trigger-source lifetime

Replacement/replay follow-up: all ten LibraryMillObserverTests pass in
`library-mill-observer-replay.log`. Eight new cases run normal priority and answer
commands, serialize every committed batch, and vary observer-removal order,
replacement pause and graveyard/hand destination. They check exact trigger count,
final stack count, duplicate-answer rejection, no repeated settling trigger and
redaction of deferred triggers from both players' pending views. These use an
unfiltered Mills trigger; they do not settle characteristic-sensitive hidden-card
matching or all pre-event global predicates. No production change was needed.
Live multiplayer acceptance of observer lifetime remains outstanding.

Reproduction and fix: LibraryMillObserverTests confirms that mill → observer
departure loses its trigger before the fix, while departure → mill correctly
does not trigger (`library-mill-observer-red.log`: one failed, one passed).
TriggerObservation now associates CardMilled notifications with their matching
library ObjectMoved arrival and detects at that movement's state. It retains the
notification's cause index and payment origin, then excludes the notification
from final-state detection. No game-state snapshot is persisted. Full suites pass
2,051 rules and 840 API tests (`library-mill-observer-rules.log`,
`library-mill-observer-api.log`). Replacement-internal ordering, serialized pause
and hidden-destination observer variants still need focused evidence; live
acceptance of this particular fix is unverified. The audit below is the pre-fix
source finding retained for traceability.

Current source inspection (2026-10-24) establishes a missing observation path:
`TriggerObservation.Capture` observes taps and token creation at occurrence time,
but leaves `CardMilled` for final-state detection. `TriggerSystem.FromObjects`
enumerates the final state's objects and checks each ability's operating zone.
Its `LookBack` only restores ObjectMoved battlefield departures, not a mill
observer that left after the mill. Therefore the next regression should use a
normal resolution that mills, then moves the observing permanent away, and
compare it with the reversed sequence. Exercise ordinary public milling first,
then the existing public-library-to-hand observation case, and serialized pauses.
This is a source-derived hypothesis, not yet a reproduced failing test.

The existing shared observation coordinator is the integration candidate; do not
add a parallel library trigger detector or persist whole GameState snapshots.
Simply adding CardMilled to its switch is not yet justified:
LibraryMillContinuation.Complete appends mill events after movement events, so
the exact event ordering must be established before relying on replay-time state.
Retain payment-origin mapping and prevent duplicate detection when integrating.

The current official September 25, 2026 rules were rechecked through
[Wizards' rules page](https://magic.wizards.com/en/rules) and its
[text rulebook](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt).
CR 603.2f concerns visibility of the ability's source. It does not itself justify
suppressing every characteristic-sensitive mill trigger involving a hidden
subject. CR 603.10a's public-departure exception and CR 701.17c's limitation on
finding the milled card must still be evaluated separately. No runtime rulebook
or hidden-subject matching policy was changed during this audit.

## Revealed-top characteristic fix

The proposed discriminator reproduced two failures (cost/effect with public top)
and two passing unrevealed controls in `library-public-mill-lookback-red.log`.
An attempted full-state observation snapshot was rejected by automatic review for
privacy risk and was not applied. The implemented alternative stores only the
public departing card and its evaluated Characteristics in LibraryDepartureObservation.
It contains no board, library order, opponent hand, or serialized GameState.

ZoneSystem's existing movement offer captures this evidence only for a tracked,
currently revealed top moving to hand/library, with active reveal permission and
the activation disclosure policy checked. The shared LibraryMillContinuation
copies it to CardMilled. TriggerMatching uses Query.MatchesObserved, which shares
the existing filter implementation, while still omitting a hidden arrival from
TriggerSubject.Objects. ZoneSystem receives the existing characteristics service
from composition; no new layer implementation is introduced.

The focused pre-pause suite passes ten tests in library-public-mill-lookback-tests.log.
The expanded regression covers cost/effect, revealed/unrevealed and serialized
replacement pauses. It checks that unrelated private card data is absent from
the observation. Full-suite evidence is recorded in the implementation register.

Compound-filter follow-up: four cost/effect and pause variants remove the
characteristic-granting permanent after the mill, then test SharesTypeWith inside
the filter's Also predicate. All four initially failed while the eight earlier
cases passed (`library-public-mill-compound-red.log`). Query.MatchesObserved now
uses a scoped ICharacteristics adapter: the departing card reads its saved
appearance throughout normal and nested query evaluation; all other objects and
player restrictions delegate to the existing layer service. This reuses the same
filter/predicate implementation and introduces no additional serialized data.

This does not close explicit replacement reveals/lifetime, all unobserved hidden
characteristic filters, disappearance of the trigger source during the event, or
conditions that need pre-event global state. Other objects and global predicates
still read the supplied current state. The bounded appearance is not a claim of
complete state look-back.
Legacy events without this optional observation keep their prior matching path.

API privacy follow-up: the existing two direct-choice privacy variants now inject
a distinctive observation marker into both ObjectMoved and CardMilled inside the
private activation history. After persistence and service reconstruction, both
observations retain the marker server-side. Neither player's serialized view nor
the recipient's direct ChoiceRequested payload contains it. These tests deliberately
construct private continuation evidence to check redaction; they do not claim the
fixture's synthetic observation was captured from a real public reveal. All 840 API
tests pass in `library-mill-observation-privacy-api.log`. No production change was
needed for this redaction boundary.

## Explicit disclosure provenance implementation — 2026-10-24

`LibraryInspection.PublicReveal` now distinguishes explicit Reveal from Look,
including Look directed to every player. Reveal sets the marker through the
existing `InspectDisclosedLibraries` path; legacy records default to false.
Existing resolution cleanup and identity-refresh record copies retain ownership
of expiration and reordering. No additional disclosure registry was introduced.

Six disclosure lifecycle/identity tests pass in
`library-disclosure-provenance-tests.log`. The expanded lifecycle theory checks
Reveal, private Look and Look-to-all after serialized pause/replay, then verifies
inspection removal on completion while historical display snapshots remain.
Full verification passes: 2,007 rules tests in
`library-disclosure-provenance-rules.log` and 840 API tests in
`library-disclosure-provenance-api.log`.
Mill departure capture now consumes this marker only on an active, resolution-owned
inspection containing the departing identity. Historical `LastReveal` is not
consulted, and Look-to-all records do not qualify. The same bounded card/appearance
observation feeds cost and effect milling; no extra state snapshot is persisted.

The initial expanded regression reproduced four failures for active explicit
reveals while twenty controls passed (`library-explicit-reveal-red.log`). With
capture connected, all thirty hidden-destination tests passed in
`library-explicit-reveal-tests.log`, including active Reveal, Look-to-all and
expired Reveal for cost/effect and serialized replacement pauses. Four additional
cases subsequently cover Reveal inside the replacement before movement; their
result belongs to the full verification run, not that thirty-test checkpoint.
Full verification passes all 2,023 rules tests (`library-explicit-reveal-rules.log`)
and 840 API tests (`library-explicit-reveal-api.log`).
Lifecycle follow-up passes ten tests in
`library-disclosure-identity-legacy-final-tests.log`. Reordering replaces the old
top identity while preserving active public provenance on refreshed inspection
records through replay and a second choice. Hand departures receive explicit
reveal evidence; private-Look controls receive evidence only for the continuously
revealed current top. The original exile destination remains covered, and all
inspection records expire on completion. The initial control incorrectly expected
no evidence for the continuously revealed top; correcting that assertion required
no production change (`library-disclosure-identity-legacy-tests.log`).

A legacy regression removes `publicReveal` from serialized inspection events,
then replays and resumes the choice. Access remains available, the missing marker
defaults to false, no explicit departure evidence is inferred, and cleanup still
finishes. This verifies conservative compatibility of inspection events only;
it does not reconstruct historical public-reveal provenance or establish general
compatibility of old paused payment logs. The follow-up changed tests only; the
full production baseline remains the 2,023 rules / 840 API run above.

## Shuffle invalidation follow-up — 2026-10-24

Two new regressions reproduced active Reveal and Look-to-all inspections surviving
a shuffle into a subsequent choice (`library-shuffle-disclosure-red.log`). This
left previously identified cards in the projected inspection list and could make
the new public-reveal marker qualify a later hidden departure incorrectly.

`ZoneReordered.Apply` now revokes inspections owned by the shuffled library when
the actual shuffle event is applied. Pending randomization still returns before
mutation. Other libraries' inspections and historical display snapshots remain.
The regressions replay the full sequence, check each player's inspection view,
retain an unrelated inspection and verify that a later hand departure has no
public observation. Browser presentation of this transition remains unverified.
Full verification passes 2,029 rules tests (`library-shuffle-disclosure-rules.log`)
and 840 API tests (`library-shuffle-disclosure-api.log`).

Four-player browser acceptance now passes on table
`c5c09390-5038-42f1-a171-31de7315f3df`
(`library-disclosure-shuffle-live-final.log`). All seats inspect two public cards
at the first pause; after shuffle all active inspections disappear while LastReveal
retains two historical cards. Both pauses and final completion survive all-seat
reloads, with exactly one reveal/one shuffle and no refusals. Inspected captures
under `tools/TableQa/library-disclosure-shuffle-*`: revealed actor landscape and
opponent desktop, shuffled actor landscape and opponent desktop, and shuffled
actor portrait rotation notice. Fixture definitions have no production artwork.
The first run hit a covered choice button because the harness left the inspection
expanded; the final run collapses it through the normal summary control before
answering. No client change was needed. The isolated host was stopped.

## Historical explicit disclosure lifecycle audit (source details)

Current source provides an operation-scoped lifetime, but does not distinguish
public reveal from private look in its active inspection records:

- `EffectInterpreter.Reveal.cs:RevealCards` calls `InspectDisclosedLibraries`
  for all players before emitting the display snapshot.
- `EffectInterpreter.Verbs.cs` calls that same inspection method for a Look's
  selected viewers. `LibraryInspection` stores viewer, owner, identities and
  resolution; it has no disclosure-kind field.
- `FinishDisclosures` preserves these records while halted and removes records
  belonging to the completed root resolution. `RefreshDisclosedLibraryIdentities`
  updates the same resolution's records after reordering.
- `CardsRevealed.Apply` only replaces `LastReveal`, whose display cards contain
  no object identity. It is historical presentation data, not an active visibility
  authority. `LibraryDepartureObservation.Capture` currently consults neither
  inspection records nor `LastReveal`; only continuous revealed-top tracking
  qualifies for capture.

Consequently, counting inspection viewers cannot safely establish an explicit
public reveal: a Look directed to all players uses the same records. The next
implementation needs explicit public-disclosure provenance on the existing
resolution-scoped lifecycle, with legacy records conservatively unclassified.
Reuse that lifecycle rather than adding another expiration mechanism or inferring
identity from card names. Before wiring capture, regressions must distinguish
Reveal from Look-to-all, active from completed disclosure, and uninterrupted from
serialized paused replacement movement. Reordering and a later movement must
not revive an expired disclosure. This source audit establishes the missing
representation; it does not claim those behaviors are implemented or tested.

## Historical shared occurrence audit — 2026-10-24

Cross-checked the current official rulebook linked by Wizards' rules page:
[September 25, 2026 Comprehensive Rules](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.txt).
The relevant 603.10a look-back exception and 701.17c public-destination restriction
remain present. The local runtime rulebook was not replaced.

The missing evidence is narrower than a missing departure snapshot:
`ZoneSystem.Proposal` already puts the departing object on `ObjectMoved.Departed`
for library moves. `LibraryMillContinuation.Complete` discards that snapshot when
constructing `CardMilled(player, move.To)`. `CardMilled` currently has only the
player and arrival. `TriggerSystem.LookBack` only handles ObjectMoved occurrences
whose source appears in LeftBattlefield history; it cannot recover a library
departure when processing CardMilled. `TriggerMatching.Milled` therefore always
installs the arrival snapshot and evaluates the filter there.

Concrete discriminator for implementation: a publicly revealed library top has
a characteristic supplied only while in the library, then a mill replacement
moves it to hand. A corresponding filtered mill trigger must be evaluated against
the appropriate pre-move appearance under the look-back exception, while its
follow-up cannot select the hidden hand arrival. Pair with an unrevealed control
and cost/effect serialized replacement choices. This is a proposed regression,
not a passing result or proof of every hidden-information case.

Persisting Departed alone is insufficient: the occurrence also needs evidence of
public visibility immediately before movement, and evaluation must preserve the
applicable pre-event characteristics. Final-state reveal tracking or historical
reveals cannot substitute for that evidence. Reuse movement/continuation ownership;
do not implement separate policies for cost milling and effect milling. Explicit
replacement reveals and reveal lifetime across pauses remain part of this work.

## Evidence

The repository's comprehensive rules distinguish the following:

- 400.2: hand and library remain hidden zones even when their cards happen to be revealed.
- 701.17c: a follow-up reference to a milled card can find it in its destination
  only if that destination is public.
- 603.10: trigger matching normally checks objects immediately after the event.
- 603.10a: a zone-change trigger involving a publicly visible object entering a
  hand or library can use the object's appearance before the event.
- 603.6: finding the object when the trigger resolves is separate from determining
  whether the ability triggered.

Primary external cross-check for the mill rules:
[Wizards comprehensive rules, June 19, 2026](https://media.wizards.com/2026/downloads/MagicCompRules%2020260619.pdf).
The local rulebook remains the implementation's recorded rules source; it was not
modified. Search results discussing Zellix and hidden destinations are community
interpretations, not sufficient primary evidence for every case below.

## Current source boundary

Additional discriminator checked: CR 701.9c expressly makes characteristics
undefined for an unrevealed discard redirected into a hidden zone. That rule is
specific to discard. The local 701.17 mill section contains no corresponding
undefined-characteristics sentence. Therefore importing the discard rule into
TriggerMatching.Milled would be a new interpretation, not an implementation of
an express mill rule. Conversely, the absence of that sentence does not by itself
prove that a mill trigger may inspect private characteristics; 701.17c's finding
restriction still needs to be reconciled with 603.10's trigger-matching rule.

The existing public-departure regression does not discriminate these interpretations:
it grants Artifact only in the library, redirects to hand, and expects no Artifact
match for an unrevealed departure. Both a privacy-restricted matcher and the current
arrival matcher produce that result because the hand card is no longer an Artifact.
The decisive case must instead use a printed Creature card and a Creature mill
filter, with no zone-dependent type modification. Current source would match that
unrevealed hand arrival. Until the rule is settled, the existing green test must
not be cited as proof that this private-characteristic behavior is correct.

Rechecked 2026-10-24 against TriggerMatching.Milling.cs and GameEvent.Milling.cs:
`CardMilled.LibraryDeparture` now carries bounded public departure evidence.
For a hand/library arrival with that evidence, `TriggerMatching.Milled` uses
MatchesObserved with the departure card and appearance. Otherwise it uses Matches
against the arrival snapshot. Both branches remove the hidden object reference
from the trigger subject, retaining the milling player. The older assertion that
CardMilled has no observation record is superseded.

The unresolved policy is specifically characteristic-sensitive matching against
an unrevealed hidden arrival in the second branch. The known-facts tests establish
Any and owner-only matching, not all private characteristics. Public look-back,
explicit reveal provenance and shuffle invalidation have separate evidence above.
A blanket suppression of all filtered hidden arrivals would discard those proven
distinctions. Historical card knowledge alone does not establish current public
visibility.

Primary-source refresh: on 2026-10-24 the official
[rules page](https://magic.wizards.com/en/rules) still links the
[September 25 PDF](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf).
The [October 19 update bulletin](https://magic.wizards.com/en/news/announcements/mystery-booster-commander-edition-update-bulletin)
describes planned changes, including 614.13a's broader zone-change application;
it does not settle the characteristic-sensitive hidden-arrival question here.
The web research tool returned HTTP 403 for Gatherer's Zellix page. A subsequent
read-only in-app browser visit successfully loaded the official
[Zellix page](https://gatherer.wizards.com/CLB/en-us/652/zellix-sanity-flayer).
Its displayed rulings (June 10, 2022) concern Choose a Background and Commander
handling; none address hidden-destination mill matching. The card's displayed
Oracle trigger does require milling creature cards. The source is now inspected,
so unavailable Gatherer access is no longer an outstanding research dependency.
The specific rules interpretation remains unresolved. No runtime rulebook or
trigger policy was changed on this basis.

## Cases that must be distinguished

| Case | Current decision and evidence |
| --- | --- |
| Unrevealed mill redirected to hand | Default post-event matching, per the rules-derived decision above. HiddenMillDestinationTests covers Creature matching, Artifact rejection, cost/effect, paused replay and private projections. |
| Public top redirected to hand | Public_library_departure_uses_pre_move_characteristics_without_exposing_hand_identity covers departure appearance, compound predicates and replay. |
| Replacement-local reveal | The same parameterized test distinguishes active public reveal from expired disclosure and private Look; provenance evidence is recorded above. |
| Reposition within library | Mill_replaced_by_repositioning_inside_library_has_no_mill_occurrence verifies preserved identities, changed order and no mill or trigger, including pauses. |
| Departure followed by return | Mill_departure_followed_by_return_to_library_is_recorded_once verifies one graveyard mill/trigger, distinct returned identity, replay and duplicate-answer refusal; cost/effect and paused/unpaused variants pass. |
| Characteristic-free filter | HiddenMillDestinationTests covers unfiltered, Any and owner-only matching without providing a hidden card subject. |
| Trigger body refers to card | Hidden-hand tests keep the arrival in hand when the body attempts to exile Triggering, and check opponent privacy at every replay event. |

The new departure/return variants and existing observer cases pass: 33 tests,
zero failures/skips, library-mill-leave-return.log. No production change was needed.
This case table has bounded decisions and regression evidence; it does not certify
all possible replacement instruction combinations.

Before changing matching, derive the expected result for each applicable case and
add cost/effect replay tests, including revealed and unrevealed controls. Any new
observation metadata belongs to the shared mill occurrence/continuation path, not
separate implementations in costs, effects, and views. Do not infer public visibility
from the engine's ability to read a card definition.
