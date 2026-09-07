# E9l — two failures the pilot cards expose in the interpreter

Anchor `InterpreterTests`, files `tests/MtgEngine.Rules.Tests/InterpreterTests.Entering.cs` and
`InterpreterTests.Entering.Fixture.cs`. Both roads are played through `Game.Apply` on
`Subsystems.Wave2()` with invented cards; both were reproduced before anything was changed.

## Fixed in this folder — CR 608.2: a resolution that asks twice never finished

The second card's shape is "exile target land; its controller **may** search their library for a
basic land". The `May` asks, the answer resumes the resolution, the `Search` asks — and answering
*that* made the walk ask "you may" a **second** time, for ever, so `StackObjectResolved` was never
emitted and the spell stayed on the stack (`InZone was Stack`).

Cause: `Bind` in `EffectInterpreter.Resume.cs` bound only the answer *just* given, from the
question's `storeAs`. The context a resumption runs in is rebuilt from the stack entry each time, so
an answer given to an **earlier** question in the same resolution was nowhere — the `May` arm looked
up `may:0.1` in `Context.Chosen`, found nothing, and asked again.

Fix: answers now ride on the question's own `ChoiceContext.Extra` under `answered:<key>`, the way a
loop's marks already ride there (`Marks` → `Ask` → `Extra` → next `Resumption`). `Answers(resumption)`
collects them, `Bind` puts them all back into `Context.Chosen`, and `Continue`/`Elsewhere` seed the
run's `Marks` with them so the *next* question carries them too. No continuation is captured, so a
game rebuilt from its log still resumes the same way (CR 101.4). 796 Rules tests green.

## Owed by another folder — CR 614.1c: an entering replacement drops the entering

`cards/89ca686a-…` is a land with "as this land enters, choose a creature type": a
`ReplacementAbility{ Instead: WouldEnterBattlefield(IsThisObject), With: ChooseValue, SelfOnly: true }`.
Verify refuses the answer with *nothing is waiting on a "value" answer: the effect that asked cannot
be found again (101.4)*. The refusal is a symptom; the defect is upstream and is **not** in
`MtgEngine.Rules/Interpreter/**`:

1. `ReplacementSystem.Perform` (`Replacement/ReplacementSystem.Applying.cs`) has no arm for a `With`
   that only *chooses*, so it falls to `default: → Instead`, which is CR 614.1a's "the event does not
   happen". The `ObjectMoved` is dropped and **the land never enters at all**
   (`InterpreterTests.A_value_chosen_as_a_permanent_enters_is_asked_of_its_controller` asserts the
   empty battlefield today). By CR 614.1c the choice is made *as* the permanent enters: the event has
   to survive, with the walk's question raised against the object that arrives (CR 614.12).
2. `ReplacementSystem.Instead` computes the ability index with
   `state.Objects.TryGetValue(candidate.Source, …)`, but for an entering permanent the candidate's
   source is `moved.To.Id` — an id that exists only in `Probe`'s state (CR 400.7). The lookup misses,
   the index is `-1`, and the question is raised with `AbilityIndex = null`. It should be computed
   against the same probe state the candidate was gathered from, so the index travels with the
   question.

Until both land there is nothing `Resume`'s `Tree` can do: the source is absent from the state, so
neither the object's abilities nor an index are reachable from the interpreter, and inventing a
"which replacement probably asked" search would be a second spelling of the index. With (1) and (2)
in place `Tree`'s existing first branch finds `abilities[index] is ReplacementAbility → With`
unchanged, and CR 608.2d's `Kept` writes `ValueChosen` on the permanent. The two tests in this
anchor assert today's behaviour and are the ones to flip.

## Owed by another folder — CR 608.2 with 400.7: "its controller" is read as the caster

Same card as the fix above. `Search{ Who: ControllerOf(Remembered("k1")) }` and its `May` are both
asked of the **spell's controller**, never of the exiled permanent's controller, so the land is
found by the wrong player. Two halves, neither in `Interpreter/**`:

- Nothing recalls a target under the key its selector declared. `Query.Selectors.cs` *reads*
  `Target.Key` out of `context.Remembered`, but no arm ever writes it, so `Remembered("k1")` is
  empty and `FirstPlayer` falls back to `Context.Controller` (CR 109.5).
- Even once it is written, the id remembered is the permanent, and `Exile` gives the card a new
  object id (CR 400.7). "Its controller" is last-known information: `ControllerOf` needs the
  look-back road (`QueryTests.LookBack`) for an object that has left the battlefield.

`InterpreterTests.A_resolution_answered_yes_and_then_answered_again_finishes_and_leaves_the_stack`
asserts the wrong player today and names this section.
