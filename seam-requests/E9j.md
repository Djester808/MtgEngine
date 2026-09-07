# E9j — the empty colour question, the delayed ability's instruction, priority after a resumption

Wave 11. Three of the four items in the brief are done inside `MtgEngine.Rules/Interpreter/`; one
needs a seam and one needs nothing from me. Rules suite: 723 tests, 722 green, and the one failure
is the assertion EINT4 § 2 wrote down as the bug — see § 1 below.

## 1. Done: a `ChooseValue` carries the set it is chosen from (CR 601.2b) — one assertion flips

`EffectInterpreter.Structure.cs`. The `ChooseValue` arm now fills the question's options from the
rules data the rest of the engine already reads, so an answer among them is a legal answer:

- `ChooseWhat.Color` → the five colours of CR 105.1, taken from the `Color` enum with `Colorless`
  removed, so the option ids are exactly the words `Query.ColorNamed`'s `Enum.TryParse<Color>`
  parses back. Ids and labels are `White, Blue, Black, Red, Green`, in that order.
- `CreatureType` → `CreatureTypes.All` (CR 205.3m), `BasicLandType` → `LandTypes.Basic`
  (CR 205.3i), `CardType` → the `CardType` names less `None`, `Keyword` → the `KeywordKind` names,
  `OddOrEven` → `Odd`/`Even`, `Player` → the seats, id = seat number, label = the player's name.
- A card that narrows the set writes `ChooseValue.Options` and that still wins over all of it —
  a number offers the range the card gave.
- A kind with no closed set and no options (`CardName`, `Word`, a `Number` with no range) is now
  **refused** by `RefusedException` naming CR 601.2b, rather than asked as an empty question. An
  empty option list can never be answered, and CR 101.4 would hold the game there for ever
  (`MEMORY: unanswered question stalls the game`), so a definition that reaches that arm fails and
  a runner reports it.

**The assertion that flips**, for whoever owns `WaveNineIntegrationTests*.cs` this wave (EINT5):
`tests/MtgEngine.Rules.Tests/WaveNineIntegrationTests.cs:128`, in
`A_tapped_for_mana_trigger_reads_the_colour_the_permanent_chose` —
`Assert.Empty(table.Pending.Options)` now fails with the five colours in the collection. It should
become the five colours, the answer `"Green"` should be accepted, and the rest of EINT4 § 2's
comment (and the second half of that test, which had to stop at the stall) can go: the chosen
colour now reaches `GameObject.Chosen`, so `TappedForMana(OfChosenColorKey:)` is reachable end to
end. **That failure is the only one in the suite and it is the intended flip, not a regression.**

## 2. Seam asked for: a delayed ability's instruction must reach its stack object (CR 603.7)

EINT4 § 1 read the symptom as "the ability is dropped with the pool". It is not. The diagnosis:

- `TriggerMatching.SpentMana` **already matches** the negative provenance block, so the spend does
  fire the delayed ability. The only code in `MtgEngine.Rules` that removes a `DelayedTriggerState`
  is `TriggerFired.Apply` (CR 603.7b, `Once`), and it removed this one — which is why
  `GameState.DelayedTriggers` is empty afterwards. That half of CR 106.6a is correct and E6e's
  matching work may already be done.
- What is lost is the **instruction**. `TriggerSystem.Create` puts the firing on the stack with
  `StackEntry.AbilityIndex = -1`, because a delayed ability is not an ability of its source's card.
  `EffectInterpreter.EffectOf` had no arm for a negative index and returned `null`, so the object
  resolved and did nothing: the card's second sentence was silently dropped at resolution, not at
  the spend.

Done in my folder: `EffectOf` now falls back to `state.DelayedTriggers` for a negative index, the
same reading `Resume.Tree` already used for a question asked inside a delayed ability. That closes
the case for a **repeating** delayed ability (`Once: false`) — tested in
`InterpreterTests.A_delayed_abilitys_instruction_is_read_off_the_state_and_not_off_a_card`.

It cannot close the **once-only** case, which is every card that says "when that mana is spent",
because CR 603.7b removes the state as the ability fires and the stack object then resolves against
nothing. That needs one of, and both are outside `Interpreter/`:

- **preferred** — `StackEntry` gains `Effect? DelayedDo`, written by `TriggerSystem.Create` from
  `Firing.Ability.Do` when `AbilityIndex < 0`; `EffectOf` and `Tree` read it before falling back to
  the state. The instruction then travels with the object the way every other choice on the entry
  does, and CR 603.7b can keep removing the state at fire time; or
- `TriggerFired.Apply` marks the delayed state spent instead of removing it, and something prunes
  it after the object resolves — more state, and a "spent" flag every reader has to honour.

**The assertion that flips** when it lands:
`WaveNineIntegrationTests.cs:72`, `Assert.Equal(libraryBefore, …)` in
`Mana_with_a_key_fires_its_delayed_trigger_when_it_pays_for_a_spell`, becomes one card drawn.
Line 78's `Assert.Empty(table.State.DelayedTriggers)` stays true and correct — the comment above it
is what is wrong: the ability is gone because it fired (CR 603.7b), not because the pool took it.

## 3. Done: "you lose the game" asks `CantLose`, "you win the game" asks `CantWin` (CR 101.2)

`EffectInterpreter.Verbs.cs`. `LoseGame` skips a player whose
`ICharacteristics.PlayerRestrictions(state, player).CantLose` is set and emits no `PlayerLost` for
them (CR 104.3b with 101.2 and 704.5a–c); `WinGame` drops a player whose `CantWin` is set and emits
no `GameEnded` at all when that leaves no winner (CR 104.2a with 101.2). Nothing is emitted and then
undone: the prohibition has the last word, so no event is written for a loss that did not happen.
E0e's "computed and read by nobody" is answered for both flags on the interpreter's side.

## 4. Nothing needed from me: priority after a question-resumed resolution (CR 117.3b)

E4f's side, and the choice system is the right place — I am **not** doing it in `FinishResolving`'s
caller. The exact call: in `MtgEngine.Rules/Choices/ChoiceSystem.Resume.cs`, method `Resumed`, after
the `foreach` that calls `casting.FinishResolving` for each `StackObjectResolved`, do what
`OpenTheDamageStep` in the same file already does for the combat road — but through the new
`ITurnSystem.OpenStep(state)` (`Seams.cs:122`, default `Unchanged`) rather than a written-out
`PriorityGranted`, and only when `state.Pending is null` (a second question means the resolution is
waiting again, CR 101.4). `OpenTheDamageStep`'s own duplicated grant should become the same call in
the same commit, which is what E4e asked for.

EINT4 § 3's assertion then flips: `WaveNineIntegrationTests.cs`, the closing
`Assert.Null(State.Priority.Holder)` of
`A_may_pay_inside_a_resolution_spends_the_pool_when_it_is_answered_yes` becomes the active player.

## 5. Recorded, not asked for

`ChoiceKinds` still has no constant for `"value"`, `"may-pay"`, `"may-cast"` or `"cast-cost"`, so
the interpreter and its tests spell them as literals in two places — EINT4 said the same. It is a
one-line addition in someone else's folder and nothing turns on it this wave.
