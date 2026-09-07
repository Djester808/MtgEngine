# E8d — an entering "as this enters, choose …" survives its own replacement

Anchor `ReplacementEffectTests`, files `tests/MtgEngine.Rules.Tests/ReplacementEffectTests.Entering.cs`
and `ReplacementEffectTests.Entering.Fixture.cs`. Both items E9l owed this folder are done, in
`MtgEngine.Rules/Replacement/ReplacementSystem.Applying.cs`. No seam changed: nothing outside
`Replacement/**` was touched and no interface moved.

## Done — CR 614.1c with 608.2d: a choose-only `With` no longer drops the entering

`Perform` gained an arm before the `Sequence` one: a `With` that only chooses — a `ChooseValue`, or a
`Sequence` of nothing else (`Chooses`) — applying to an object arriving on the battlefield
(`Arriving`) runs the walk and hands **the event back unchanged**. It is not CR 614.1a's "instead":
the choosing is part of the entering, so the move still happens and the event goes on to the effects
that have not applied yet (CR 616.1) — "enters tapped" on the same arrival still applies, which is
its own test. A `ChooseValue` on any other event, and a `Sequence` that does anything besides choose,
fall through to exactly the arms they hit before.

## Done — CR 101.4 with 400.7: the index is computed against the probe state

`Instead`'s inline index became `IndexOf(state, current, candidate)`, which looks the source up in
`Probe(state, current)` rather than `state.Objects`. An entering permanent's `moved.To.Id` exists
only in the probe (CR 614.12), so every enters-replacement question used to travel with
`AbilityIndex = null` and no answer could find the effect that asked. The index is the ability's
position on the entering object's own face, which is what `EffectInterpreter.Resume`'s `Tree` reads.
The fixture's land carries the replacement as its **second** ability on purpose, so the test would
fail if the index were defaulting to zero.

`Run(state, effect, context, int?)` is now implemented on `ReplacementEffectFakeInterpreter` (it had
been taking the seam's default forward to the three-argument overload, which threw the index away),
recording into `Indexes`.

816 Rules tests; 814 green, and **the two failures are the two tests E9l wrote to be flipped**.

## Owed by another folder — the two `InterpreterTests` tests that assert the old behaviour

`tests/MtgEngine.Rules.Tests/InterpreterTests.Entering.cs` is not in this folder's `Owns`, and both
its entering tests assert the defect by name and cite `seam-requests/E9l.md` as the thing that would
change them. They now fail, and each is a one-line flip:

- `A_value_chosen_as_a_permanent_enters_is_asked_of_its_controller`, line 45. Replace
  `Assert.Empty(table.Battlefield(E0, …HallName));` and
  `Assert.False(table.State.Objects.ContainsKey(pending.Context.Source!.Value));` with the rule as it
  now holds: the land **is** on the battlefield while the question stands, and the question's source
  is that land —
  `var land = Assert.Single(table.Battlefield(E0, InterpreterTestsEnteringTable.HallName));` then
  `Assert.Equal(land, pending.Context.Source);`. Delete the paragraph of comment that says CR 614.12
  is not held to yet.
- `The_answer_to_a_choice_made_while_entering_cannot_find_the_effect_that_asked`, line 58. The answer
  is no longer refused: `Refuse(new AnswerChoice(…, ["Goblin"]))` now returns `Applied`. The test
  should `Do` the answer and assert CR 608.2d's keeping —
  `Assert.Equal("Goblin", table.State.Objects[land].Chosen[InterpreterTestsEnteringTable.TypeKey]);`
  — and the method name should say the effect **is** found again. This folder's
  `A_land_that_chooses_a_type_as_it_enters_arrives_and_keeps_the_answer` already asserts exactly that
  through the same front door, so the flip is a rename and two assertions, not new coverage.

## Owed by another folder — the card still fails, on a different gap

`dotnet run --project MtgEngine.Cards.Pipeline -- verify cards --only 89ca686a-…` now gets **past**
the choice (scenario 1 verifies, and the refusal moved from *"nothing is waiting on a value answer:
the effect that asked cannot be found again (101.4)"* to scenario 2 of 3) and fails on
**`No interpreter arm for this shape of ManaProduction` (CR 608.2)** — the same card's third ability,
`AddMana{ anyColor: true, SpendOnlyOn: { Types: Creature, Spell: true, Also: IsChosenType("k1") },
Key: "k2" }` inside a `Sequence` with a `ManaSpent` delayed trigger. That is the interpreter's mana
arm, not `Replacement/**`: an any-colour production that also carries a `SpendOnlyOn` restriction
appears to reach no arm (wave 10 gave the plain any-colour road its `ManaColors`, and wave 11 gave
`SpendOnlyOn` its restricted block; the two together are the shape that refuses). It is also the one
reader of the choice this folder's tests could not exercise end to end — `IsChosenType("k1")` is read
off the permanent's `Chosen`, which the front-door test proves is written — so closing it finishes
the card. Suggested for the **E9 or E3 series, next wave**; `cards/` was left untouched
(`git checkout HEAD -- cards`).
