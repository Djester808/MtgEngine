# E4e — priority after a resumed combat damage batch

CR 510.3 with 117.3b is closed: when the answer that finishes a halted batch leaves no further
question, the active player receives priority in the combat damage step exactly as after an
unhalted `DealCombatDamage`, and the game walks on with `PassPriority`. A batch that halts a
second time opens nothing until that answer too.

Two things for the coordinator.

## 1. `ITurnSystem` has no "the turn-based action is finished, open the step" — one duplication

The unhalted batch is dealt inside `TurnSystem.Advance`, which grants priority itself at the end
of the same call (`TurnSystem.cs:171`, CR 117.3a) through the private `GrantPriority`. A batch a
CR 616.1 question halted comes back through `ChoiceSystem.Answer` instead, and nothing on
`ITurnSystem` — `Begin`, `OpenHands`, `AfterMulligans`, `Pass`, `Advance` — can be called to open
the step: `Pass` refuses (nobody holds priority to pass), and `Advance` would end the step rather
than open it.

So `ChoiceSystem.Resume.cs` now writes the grant out itself, in `OpenTheDamageStep`, with a
comment naming the duplication and pointing here. It is three lines and one `PriorityGranted`,
and it deliberately does **not** copy `GrantPriority`'s `HasLost` walk: state-based actions run
after the command in `Game.Apply`, so nobody can have lost inside the resumption itself.

**Asked for:** the smallest member that would delete the copy, e.g.

```csharp
/// <summary>CR 117.3a: a turn-based action that finished out of band — a combat damage batch a
/// CR 616.1 question halted — opens its step, and the active player receives priority.</summary>
[RuleRef("117.3a")]
Outcome OpenStep(GameState state) => Outcome.Unchanged(state);
```

with `TurnSystem` implementing it as `GrantPriority` and `RestOfTheBatch` calling `turn.OpenStep`
in place of `OpenTheDamageStep`. Not urgent — the behaviour is right either way; this only stops
the two spellings of "the active player receives priority" drifting apart.

## 2. EINT3's gap assertion now fails, and the file is not mine

`WaveEightIntegrationTests.cs:301` is `Assert.Null(table.State.Priority.Holder)`, written by EINT3
to assert the gap with the note "the day it is fixed that line fails and says so". It fails now,
and it is E3h's file this wave, so it is left alone: **657 of 658 Rules tests pass, and this is the
one**. The fix is that line becoming `Assert.Equal(P0, table.State.Priority.Holder)`; the comment
above it (lines 295–299) should go with it. Nothing else in the suite moved.

## 3. Not a seam: the marks now travel onto a second question

Found while building the twice-halted test, fixed in `Choices/` which is mine, reported because it
changes what a log holds. `ReplacementSystem.Ask` builds each question's `ChoiceContext.Extra`
fresh, so a question raised by *answering* a question carried none of the halt's marks
(`CombatDamageStep`, `CombatDamageIndex`) that `CombatSystem.Halted` writes. With three prevention
shields on one hit that is reachable in a real game: the second answer resumed the replacement
chain and then `RestOfTheBatch` found no marks, so the rest of the batch was never dealt and the
step never opened — the game stranded in the combat damage step with its damage half done.
`ChoiceSystem.StillTheBatch` now re-raises the still-pending question with the same two marks, so
however many times one hit stops the game the last answer finishes the batch. The re-raise is a
second `ChoiceRequested` for the same choice id, the way `CombatSystem.Halted` re-raises its own,
so a replayed log ends on the marked question.
