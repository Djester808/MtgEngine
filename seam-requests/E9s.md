# E9s — amass, the delayed trigger's duration, and the secret simultaneous number choice

Built in `MtgEngine.Rules/Interpreter/EffectInterpreter.Wave24.cs` (a new partial, plus two cases in
`EffectInterpreter.Backlog.cs`'s switch), tested in
`tests/MtgEngine.Rules.Tests/InterpreterTests.Wave24.cs`.

## Done here

1. **`Amass` (CR 701.47a).** The arm the wave-23 gate's one failure named
   (`cards/ea5103f5-…json`, "Then amass Orcs 1"). A player who controls no Army creature gets a
   0/0 black token of the named type and Army, created through the zone system's `CreateToken` like
   every other token; then N +1/+1 counters go on **one** Army they control, proposed through the
   replacement system (CR 614.1). The token's spec is the rule's, and the token's name is its
   subtypes (CR 111.4), so "Orc" amasses an Orc Army. Printed amass text is plural and CR 205.3m
   names types in the singular, so a trailing `s` is dropped when that is what makes it a type; a
   word that is no creature type either way is **refused** (CR 205.3m) rather than made into a token
   no filter could find. Amass of 0 still creates the token and puts no counters on. A player who
   controls more than one Army is asked which — `ChoiceKinds.Choose`, the same kind and road the
   `Choose` selector uses — and the two halves can never both need a question, because a player who
   controlled none has exactly the one token afterwards.
2. **`ChooseNumberSecretly` (CR 700.2 with 101.4)**, the bounded half. Every player the reference
   admits is asked for a number in **turn order** (CR 101.4), one question at a time, and the walk
   halts on each, so **no step of the effect can read any number until the last player has
   answered** — that halt is the reveal, and there is nothing for a later step to reveal. Each
   number is remembered in the resolution's context under
   `EffectInterpreter.SecretNumberKey(key, player)` = `secret-number:<Key>:<seat>`; the prefix a
   whole choice shares is `EffectInterpreter.SecretNumbersUnder(key)`. Both are `public` on purpose:
   the three readers live in the query layer and must not spell the key a second time. An omitted
   `Who` is the controller alone (CR 109.5), as everywhere else in this vocabulary.

## Asks

1. **State (`DelayedTriggerState`) — the delayed trigger's duration, CR 603.7c.** E6g asked for this
   and it is still not there, so the interpreter's `DelayedTrigger` arm has nowhere to write
   `Until` and drops it silently. The exact one line, in `MtgEngine.Rules/State/GameState.cs`:

   ```csharp
   public sealed record DelayedTriggerState(int Id, Trigger When, Effect Do, ObjectId? Source, PlayerId Controller, bool Once, ImmutableDictionary<string, ImmutableList<ObjectId>> Remembered, Duration? Until = null);
   ```

   Appended with a default, so every existing construction site still compiles. Once it exists the
   interpreter's `Delaying` passes `delayed.Until` as the last argument — one argument, in
   `EffectInterpreter.Structure.cs`, and this box will take it the moment the field lands. E2c drops
   the trigger at cleanup whose duration has ended, fired or not (`Once` and `Until` answer different
   questions: "until end of turn, whenever …" sets both). Nothing else changes: `FromDelayed` already
   fires whatever the state holds and `TriggerFired.Apply` already removes only a once-only one.
2. **Query (`Query.Amounts`, `Query.Selectors.Players`) — the three readers of a secret choice,
   CR 700.2.** The numbers are in `EffectContext.Chosen`, one entry per player, under
   `EffectInterpreter.SecretNumbersUnder(key) + seat` (decimal, culture-invariant), written only
   once every player has answered.
   - `HighestChosen(Key)` / `LowestChosen(Key)`: parse every entry whose key starts with that
     prefix and take the greatest / least. No entry at all is 0 — an ability copied outside a
     resolution that asked, which L035 forbids on a card.
   - `PlayerWhoChose(Key, Lowest)`: **every** seat whose number is that extreme, in turn order, so a
     tie names them all ("each player who chose the lowest number").
   Do not re-key any of this: the interpreter is the only writer and the constant is public for
   exactly this reason.
3. **Choices (`PendingChoice`, `ChoiceSystem.Validate`) — an answer that is a number and not one of
   a list, CR 700.2 with 101.4.** Every answer is checked against the options the question carries,
   so an **unbounded** secret choice ("a number 0 or greater" — Wheel of Misfortune `10aff3f6`, the
   card the record exists for) is a question nothing can answer, and this box refuses it by name
   rather than offering an invented ceiling. The ask is a numeric answer channel: a `PendingChoice`
   that carries a range instead of an option list (`Low`/`High`, either open), with `Validate`
   parsing the answer and checking the bounds, and the answer arriving as the same string the
   arm already reads back. It also closes the second refusal here — a bounded span wider than 256,
   which would otherwise carry an option per number.
4. **Choices (`PendingChoice`) — the answers already given must not ride on the next question,
   CR 101.4.** This is the one thing the secret choice does not yet get right, and it is not fixable
   inside the interpreter. A resolution that asks twice carries its earlier answers forward in the
   question's `ChoiceContext.Extra` (`answered:…`, put there by `Ask` from `EffectRun.Marks`), which
   is how a resumed walk keeps them — so the **second** player's pending question literally holds the
   first player's number in the state, and a client that renders a question renders the leak. The
   numbers are chosen simultaneously and none may be visible before the reveal (CR 101.4), so the
   ask is either
   - a `Secret: true` flag on `PendingChoice` whose `Extra` is withheld from the asked player's view
     (the hidden-information line the hand and the library already have), or
   - more than one pending question at a time — `GameState.Pending` is a single choice, so the N
     questions cannot be raised together, which is what CR 101.4's simultaneity actually describes.
   The first is one field and a view rule; the second is a state change with a much longer reach.
   Either way the interpreter's arm does not change: it asks per player and reads the numbers back
   under its own keys.

## Not done in this box

- **No `DelayedTrigger.Until` behaviour and no test for it.** There is nowhere to write the duration
  (ask 1). A test asserting the duration is dropped would have to be deleted when the field lands,
  so the arm's doc comment names the gap instead.
- **No end-to-end card test for the secret choice.** `HighestChosen`, `LowestChosen` and
  `PlayerWhoChose` are read by the query layer, which this box does not own (ask 2); the tests read
  the numbers back with `ChosenNumber` under the interpreter's own keys, which proves they are stored
  where those readers will look.
- **The unbounded and the very wide secret choice are refused, not asked.** Ask 3.
- **Amass does not ask which Army when N is 0.** CR 101.4: a question whose answer could change
  nothing is not asked, and 0 counters on either Army is the same board.
