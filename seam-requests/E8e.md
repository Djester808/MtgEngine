# E8e — a may-pay asked by an entering "instead" can be answered

Wave 27. Anchor `ReplacementEffectTests`: **26 green** (22 before), four new tests in
`tests/MtgEngine.Rules.Tests/ReplacementEffectTests.Wave27.cs`. One engine file changed,
`MtgEngine.Rules/Replacement/ReplacementSystem.Applying.cs`. No seam moved: nothing outside
`Replacement/**` was touched and no interface changed. `InterpreterTests`, `ZoneReplacementTests`,
`CombatReplacementTests` and `PlayerLimitReplacementTests` re-run together: 160 green.

## Done — CR 614.1a with 400.7 and 101.4: the question travels under the card that is leaving

E9u ask 2. `Instead` now hands the interpreter `Leaving(state, current, candidate)` as the source
instead of `candidate.Source`, and computes the ability index against **that** object:

- `candidate.Source` for an enters-replacement is `moved.To.Id`, the object as it would exist on the
  battlefield. That id lives in `Probe(state, current)` alone (CR 614.12 with 400.7).
- A CR 614.1a "instead" means the move never happens, so nothing in the state the answer comes back
  to ever carries that id. `EffectInterpreter.Resume`'s `Tree` looked it up in `state.Objects`, found
  nothing, fell through to the delayed triggers, found none, and refused the answer.
- `Leaving` returns the id the card still has — on the stack, in a hand, in a library — whenever the
  candidate's source is absent from `state.Objects` and the event is that object arriving. It is the
  same card, so it carries the same `AbilityList` and `Tree` finds `AbilityList[AbilityIndex]`, and
  it is what the walk's `Self` then names, which is what makes `Move(Self → Battlefield)` and
  `Move(Self → Graveyard)` mean the right card. Every other event hands its own source back
  untouched.

The CR 614.1c road (`Choosing`, E8d) is deliberately **not** changed: there the object does arrive,
so by the time the answer comes it exists under the probed id, and that id is what CR 608.2d writes
the chosen value on. Swapping it for the leaving card would break that resumption instead — a test
asserts each road keeps its own source.

`IndexOf` gained the source as a parameter rather than reading `candidate.Source`, so the index is
always counted on the face of the object the question will be found under.

**Measured on the card.** `verify cards --only f3c5978a-…` (Mox Diamond) no longer refuses: the
message *"nothing is waiting on a may-pay answer: the effect that asked cannot be found again
(101.4)"* is gone, the walk resumes, and the card reaches a zone. Scenario 2 (the activated ability)
verifies. Scenario 1 now fails on `InZone was Graveyard` — see below.

## Ask 1 (`MtgEngine.Rules/Scenarios/**`) — a smoke scenario should pay a may-pay it can afford

`ScenarioAnswers.FirstLegal` has an arm for `ChoiceKinds.May` — `[Prefer(choice, "yes")]`, with the
comment "saying yes is what makes the effect happen, and a smoke scenario asserts the effect" — and
none for `ChoiceKinds.MayPay`, which falls to the default `choice.Options.Take(Count(choice))` and
takes whichever option is first. On Mox Diamond that is the decline, so the engine correctly runs the
`Else` and puts the artifact into its owner's graveyard, while the generated smoke scenario asserts
`InZone Battlefield`. The engine and the scenario disagree about the *answer*, not about the rule.

One line, the same shape as the `May` arm and for the same reason (CR 601.2b: a smoke scenario
asserts the effect, so it pays when it can afford to):

```csharp
ChoiceKinds.MayPay => [Prefer(choice, "yes")],
```

`Scenarios/**` is not in this folder's `Owns`. Mox Diamond's smoke scenario is the reader.

## Ask 2 (`MtgEngine.Rules/Interpreter/**` with `State/**`) — CR 614.5 across a halt

**Unverified, and it only bites the branch ask 1 unlocks.** When the may-pay *is* paid, the `Then` of
Mox Diamond's `With` is `Move(Self → Battlefield)`. That goes through `Relocate` →
`ZoneSystem.Replacing` → `ReplacementSystem.Replace` with a **fresh** `used` set, and the same
`ReplacementAbility` matches the new `ObjectMoved` (same card, `Where: Battlefield`,
`WouldEnterBattlefield(IsThisObject)`), so it applies a second time and asks again. CR 614.5 says a
replacement effect gets one opportunity to affect an event *or any modified events resulting from
that event*; `used` is scoped to one `Replace` call and cannot see across an interpreter run, let
alone across the halt the question causes — the answer arrives through
`EffectInterpreter.Resume` → `Elsewhere`, which knows nothing about the replacement loop that
started it.

I could not close this inside `Replacement/**`: the fact "this ability already applied to this event
chain" has nowhere replay-safe to live. An instance field dies at the halt; the `ChoiceContext` the
question carries is the interpreter's, not the replacement system's, and the nested `Replace` is not
handed it. What would close it: a per-event memory the interpreter carries into the events it
proposes — the `used` keys travelling on the `EffectRun`/`EffectContext` and reaching
`ReplacementSystem.Replace` as a seed for its `used` set, restored on resumption the way
`RememberedPrefix` restores CR 608.2h's memory. That is `Interpreter/**` plus whatever record the
keys ride on.

The decline branch, which is what the harness exercises today, does not loop: the `Else` moves the
card to a graveyard and `WouldEnterBattlefield` does not match that.

## Not done in this box

- **The falsification of the new tests was reasoned, not run.** The box ended before a revert-and-
  re-run cycle (two ~70 s builds) would fit. The load-bearing assertion is
  `Assert.Equal(card, context.Source)` in
  `An_instead_on_an_entering_permanent_runs_under_the_card_that_is_leaving`: before the change the
  source was `moved.To.Id` (`state.NextObjectId`), which the same test asserts it is not and asserts
  is absent from `state.Objects`. The card-level evidence is stronger anyway — the refusal the task
  named is gone from the verify output.
- **`The_index_an_instead_travels_with_is_read_on_the_leaving_card` is a guard, not a falsifier.**
  Both objects carry the same printed face, so the index is 1 either way; the test exists so that a
  later change to `Leaving` cannot leave `IndexOf` reading a different object than the question will
  be found under.
