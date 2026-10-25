# Test scope

Hand acceptance is separate: `./tools/verify-test-scope.ps1 -Scope Hand`.
Its `HandContractTests` cover owner-specific membership, count, lookup, shared
draw behavior, four-seat privacy, control versus ownership, same-hand no-op,
front-face entry, departure, printing/finish identity and serialized replay.
It adds no hand-size, discard, casting or mulligan behavior. `EngineRegression`
excludes Hand acceptance as well as the other explicitly separated scopes.

Graveyard acceptance is separate: `./tools/verify-test-scope.ps1 -Scope Graveyard`.
Its focused contract tests cover an empty owner-specific pile, public ordered
inspection, lookup, owner routing, normal top insertion, same-zone no-op,
face-up/front-face entry, departure identity and privacy, caller-supplied batch
arrival order, and serialized replay. It adds no permission to reorder, cast or
activate cards. `EngineRegression` excludes both Deck and Graveyard acceptance.

Push checkpoint (2026-10-25): regenerated the two stale scenario schema snapshots
after the earlier payment-order removal. The full solution now passes 1,645 rules,
835 API, 326 Cards and 322 pipeline tests (`push-backend-tests.log`). The card/deck
acceptance subset remains 66 rules + 80 API; the totals above are broader regressions.

Card-contract checkpoint (2026-10-25): 11 new `CardContractTests` pass, and the Deck
group now passes 66 rules + 80 API tests. Separate existing regressions pass
1,579 rules + 755 API tests. Logs: `card-contract-deck.log` and
`card-contract-regressions.log`. The extra Cards project run passes 324 tests and
fails two existing schema snapshots: both still contain the removed `paymentOrder`
scenario field. Generated-schema comparisons show that as the sole difference;
the snapshots were not changed by this card task. The definition schema passes.
One pre-existing turn-order fixture now uses the Oracle ID its lookup promises;
all of its behavioral assertions are unchanged. No client source changed.

The deck work does not implement or certify the whole rules engine. Report these
groups separately; a total from the entire suite is not a deck-test count.

Verified on 2026-10-25: the Deck group contains 55 rules tests and 80 API tests
(135 total). The separately run existing-regression group contains 1,579 rules
tests and 755 API tests. All four groups passed. Counts are checkpoint results,
not a target to inflate or a measure of complete rules implementation.

From the repository root:

```powershell
./tools/verify-test-scope.ps1
./tools/verify-test-scope.ps1 -Scope EngineRegression
./tools/verify-test-scope.ps1 -Scope All
```

The default runs only `[Trait("Scope", "Deck")]` acceptance tests in the rules
and API projects. It covers saved-deck expansion and admission, Commander
construction, selected printing/finish identity, initial libraries, drawing,
shuffling, manual inspection/reordering/search, multiplayer search, and mulligans.
Some acceptance cases deliberately exercise replacement choices or replay at the
boundary; they protect the library interaction, not a claim of complete card rules.

The remaining tests are existing engine/API regressions outside the deck scope.
They cover live dependencies such as casting, combat, triggers, accounts, and
collections. Production GameSessionService still uses Subsystems.Wave2, which
composes those rules systems. Removing them would disable working application
paths. Their tests remain useful even though those features are not the current
deck deliverable. Passing tests do not establish complete Magic rules coverage.

Seven historical tests that only exercised unimplemented placeholder defaults
have been removed. The unused AllStubs factory and its eight otherwise unreferenced
stub classes have also been removed. Concession and post-game refusal tests now
exercise the actual production composition, with their behavior assertions intact.
The reduced fixture and its remaining doubles now live only in this test project
as LegacySubsystems and Stubs. They are not shipped in the production engine.

Add a Deck trait only when the test protects one of the stated deck interactions.
Do not tag every Library-named test: many exercise unrelated card rules. Retain
meaningful privacy, replay, and behavior assertions. Delete tests only when their
subject is removed or they merely check obsolete scaffolding, not to make a failing
feature appear complete.
