# E4f — one spelling of "the active player receives priority"

`ITurnSystem.OpenStep` (wave-11 seam, default `Unchanged`) is implemented by `TurnSystem` as the
body of the private `GrantPriority`, has-lost walk included, behind three refusals: nothing is
opened while another question stands (CR 101.4), once the game has ended (CR 104.1), or while a
player already holds priority (CR 117.3 — a question asked during a cast is answered by the player
who has it, and the answer does not take it off them). Those refusals are what let the choice
system ask after *every* card's answer, so the road no longer has to be enumerated.

`ChoiceSystem.OpenTheDamageStep` is deleted; `Resume`'s default arm — every kind the interpreter
resumes, which is the search, scry, trigger-order, replacement-order, targets, cost and may roads —
ends `.Then(turn.OpenStep)` after `AndTheRest`, so the combat damage batch (CR 510.3) and the
answer-finished resolution (CR 117.3b with 608.2) reach the same grant by the same call. The
mulligan, discard and legend-rule arms are untouched: a keep opens the game through
`AfterMulligans`, a discard finishes the cleanup step through `Advance`, and a legend-rule question
is answered by a player who still holds priority.

**No seam is asked for.** Two things for the coordinator.

## 1. EINT4 § 3's gap assertion now fails, and the file is not mine

`WaveNineIntegrationTests.cs:182` is `Assert.Null(table.State.Priority.Holder)` in
`A_may_pay_inside_a_resolution_spends_the_pool_when_it_is_answered_yes`, written by EINT4 to assert
the gap. It fails now — **722 of 723 Rules tests pass, and this is the one**. The fix is that line
becoming `Assert.Equal(P0, table.State.Priority.Holder)`; the comment above it (lines 176–180) goes
with it, and the note at lines 188–190 that the "answered no" half had to be its own game because
the gap stopped the first one dead is no longer true either. The same reproduction with the
assertion flipped is
`ChoiceResumptionTests.A_resolution_finished_by_an_answer_leaves_the_active_player_with_priority`,
which also walks the game on with a round of passes.

## 2. Not a seam: a fake turn system stopped granting priority

`CombatReplacementTests.Answer` built its choice system with `ChoiceResumptionFakeTurn`, and the
CR 510.3 grant used to come from the choice system itself, so the fake did not matter. It does now,
so that helper builds the real `TurnSystem` (over the fake zones and `TurnFixture.FakeCasting`)
instead. Any other test that answers a question through a `ChoiceSystem` with a stub turn system
will see the step stay shut — which is honest, but worth knowing before it is read as a defect.
`ChoiceResumptionFakeTurn` itself now records the state it was asked to open in, and the decision
it stands in for is tested against the real turn system in `TurnStructureTests.OpenStep.cs`.
