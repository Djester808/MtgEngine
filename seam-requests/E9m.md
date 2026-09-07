# E9m — the interpreter reads V9 (wave 16)

Owns `MtgEngine.Rules/Interpreter/**` and `tests/MtgEngine.Rules.Tests/InterpreterTests*.cs`.
Everything below the first heading landed in that folder; the asks are the two things that did not.

## Done here

- **`Choose.Among` (CR 700.2).** A `Choose` selector had no road at all before this wave: nothing
  in the interpreter asked the question, so `Objects` reached the query's arm and it threw
  "the choice has not been made" on every card that used one. Now `Execute` asks every `Choose`
  the node it is reaching reads (`EffectWalk.ChoicesIn`, which stops at a child `Effect` so
  "reveal, then choose from among them" asks after the reveal has been remembered), one question
  at a time, before the node runs. With `Among` the candidates are `Objects(among)` matched
  against the filter **in each candidate's own zone** (`filter with { In = obj.Zone.Zone }`), so
  `Filter.In` is never read and the query is untouched; without it they are `Each(filter)`.
  `Count` / `UpTo` give min and max ("any number" is none to all); an empty set remembers an
  empty choice and asks nothing (EINT4's precedent). The answer is remembered under `Key` and a
  keyless one under `choose:<path>`; the keyed read stays the query's, the keyless one is
  `Objects`' own arm.
- **A remembered set now survives a question.** `Ask` writes every `Remembered` entry of the walk
  into the question's `Extra` under `remembered:<key>` and `Bind` puts them back on resume when
  the stack entry does not already carry the key. Without this the third step of the Genesis
  Wave shape (`Move(Remembered(k1), Graveyard)`) moved nothing after the choice, because the
  reveal step is skipped on the resumed walk and its memory was only ever in `run.Context`.
- **`TotalAmong` / `GreatestAmong` binding confirmed** by two interpreter tests: the query's
  `Among` evaluates `Of` with `ctx with { Source = id }` and `Self` reads `context.Source`, so the
  candidate is `Self`, the source is not counted N times, and `Triggering` inside the same
  reading stays the trigger's object. No fix was needed.

## Asks (outside `Interpreter/**`)

1. **Choices (`ChoiceKinds`)**: a constant for the plain choice, `Choose = "choose"` (CR 700.2,
   "raised and finished by the interpreter"). The kinds sweep (`ChoiceResumptionTests.Kinds`)
   refuses any literal that is not a declared constant, so the question is asked as
   `ChoiceKinds.Search` for now — same shape, same road back, wrong name. It is one private
   constant in `EffectInterpreter.Choose.cs` (`ChooseKind`) to flip when the constant exists.
2. **Pipeline / verify (for the coordinator)**: `cards/e2487868-f386-438e-a73f-b494f6d35fac.json`
   still spells the choice as `IsRemembered(k1)` inside a filter with `In: Library`; it needs the
   redo V9 named (item 5 of V9's asks) before the interpreter road above plays it. The smoke
   answerer will see the choice under the search kind until ask 1 lands.

## Not done in this box

- A keyed `Choose` inside a `ForEach` body is asked once and its key is remembered for every
  later iteration; a keyless one is asked per iteration (its key is its path). Whether a card
  needs per-iteration keyed choices is a vocabulary question.
- A keyless `Choose` nested inside another selector (`Union`, `ControllerOf(...)`) is still
  refused by the query's arm, which needs a key to read; the interpreter's `Objects` handles only
  the top-level keyless read.
