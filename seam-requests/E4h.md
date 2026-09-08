# E4h — the choice system keeps a secret and takes a number

Built in `MtgEngine.Rules/Choices/ChoiceSecrets.cs` (new), `MtgEngine.Rules/Choices/ChoiceNumbers.cs`
(new) and `ChoiceSystem.Validate`; tested in
`tests/MtgEngine.Rules.Tests/ChoiceResumptionTests.Wave25.cs`. Answers `seam-requests/E9s.md` asks 3
and 4.

## Done here

1. **The secret answer no longer rides where a player can read it (E9s ask 4, CR 101.4b).**
   Reproduced first: `EffectInterpreter.Ask` copies the resumed walk's marks onto the next question,
   and those marks are every answer already given (`answered:` + the arm's `storeAs`), so the second
   player's pending question literally holds `answered:secret-number:<key>:<first seat>` = the first
   player's number. `ChoiceResumptionTests.Wave25` drives that exact sequence through
   `ChoiceSystem.Answer` with a fake resumable standing in for the interpreter.

   **Stripping them was the wrong fix and is not what landed.** The resumed walk finds the players
   who have already answered by looking those very entries up (`ChoosingSecretly` skips a player whose
   key is in `Context.Chosen`, and `Bind` fills `Chosen` from the marks), so a question that dropped
   them would send the walk back to the first player and the resolution would never finish. The
   number has to stay in the state and be withheld from every view of the question — the line the
   hand and the library already sit on.

   `ChoiceSecrets.Withheld(PendingChoice?)` is that removal, and `ChoiceSecrets.SecretsOf` is the
   engine-side read that says out loud the numbers are kept. Secrecy is read from the key the asking
   effect already wrote — `EffectInterpreter.SecretNumberPrefix`, public for exactly this reason —
   rather than from a second flag a raiser has to remember to set, so no arm outside `Choices/**`
   changes and there is one spelling of the marker.

2. **A numeric answer channel (E9s ask 3, CR 601.2b with 101.4).** `ChoiceNumbers`: a question whose
   `ChoiceContext.Extra` carries the `numeric` mark is answered with a number instead of an option id,
   and `ChoiceSystem.Validate` checks it against `NumberRange(Low, High)` — either end open — where it
   would have checked the option list. The number comes back as the chosen option's id, which is the
   same string the arm that asked already reads back, so an arm that stores its answer by key needs no
   new road. `ChoiceNumbers.Question(...)` builds a whole numeric question for a raiser with no `Ask`
   of its own.

   The range rides in `Extra` under three constants here rather than in two more fields on
   `PendingChoice`, so nothing in `State/**` had to move for the channel to exist. If a later wave
   wants them first-class, it is `int? Low = null, int? High = null` appended to the record and
   `ChoiceNumbers` reading those instead — a `State/**` change, and none of the callers move.

## Asks

1. **Views (`ViewProjector.Redact`) — a player's own question is shown withheld, CR 101.4b.** One
   line, and the other half of ask 4 above. `Redact` today blanks the whole context for a viewer who
   is not the asked player (right, and unchanged); the arm that hands the asked player their own
   question returns `pending` as it stands, and that is where the leak reaches a client:

   ```csharp
   pending is null || pending.Player == viewer
       ? ChoiceSecrets.Withheld(pending)
       : new PendingChoice(/* unchanged */);
   ```

   `Withheld` returns the same instance when the question carries no secret, so every other question
   is untouched and no view allocates. A test: two players, a secret-number choice answered once, and
   the second player's own `GameView.Pending.Context.Extra` carries no key `ChoiceSecrets.IsSecret`
   admits.

2. **Interpreter (`EffectInterpreter.ChoosingSecretly`) — ask the number instead of refusing it, CR
   601.2b.** One line, E9-series: where the arm today throws for `choice.Max is null` and for a span
   `>= SecretNumberSpan`, ask with no options and `ChoiceNumbers.Ranged(context, min, max)` on the
   question's context instead — the answer arrives under the same `storeAs` key, so nothing else in
   the arm changes and the two refusals and `NumberOptions`/`SecretNumberSpan` go away. That is what
   Wheel of Misfortune (`10aff3f6`) needs.

## Divergence from the brief, deliberate

- **The numeric channel cites CR 601.2b, not 700.2.** E9s and the wave-25 brief both say "CR 700.2
  with 101.4", but the shipped rules text at 700.2 is the *modal* rule ("a spell or ability is modal
  if it has two or more options in a bulleted list"), which is not what a chosen number is. 601.2b is
  the rule for announcing the value of a variable, and it is already what
  `ChoiceSystem.RuleFor(ChoiceKinds.Value)` cites, so a refused number is refused with the rule its
  own kind carries. The secret half keeps CR 101.4, and 101.4b is the sentence it turns on: a player
  ordinarily knows the choices made before theirs, and a secret choice is the exception.

## Not done in this box

- **No card verifies through this yet.** Both halves are one line away in folders this box does not
  own (asks 1 and 2), and until ask 2 lands `ChooseNumberSecretly` still refuses the unbounded choice
  by name, so no card reaches the channel. The tests drive `ChoiceSystem` directly.
- **Nothing marks a secret other than the secret-number key.** A later arm that wants a different
  answer kept secret adds its prefix to `ChoiceSecrets.IsSecret`, or the marker becomes a flag on
  `PendingChoice` (a `State/**` change) — deliberately not invented ahead of a second caller.
