# E6g — the trigger system reads V12's draw exception, V11's declaration trigger, and the damage a firing carries

Built in `MtgEngine.Rules/Triggers/**` (a new partial, `TriggerMatching.Wave23.cs`, plus three small
edits: two lines in `TriggerMatching.Match`, two in its `Drew` arm, and one constant in
`TriggerSystem`), tested in `tests/MtgEngine.Rules.Tests/TriggeredAbilityTests.Wave23.cs` (8 tests
through the trigger board). 46 `TriggeredAbilityTests` green.

## Done here

1. **`Draws.ExceptFirstInDrawStep`** (CR 121.1 with CR 504.1). The trigger skips the first card a
   player draws while **their own** draw step is the step and fires on every other draw: a second
   draw taken in that same step, a draw in a main phase, a draw in someone else's draw step, and one
   more spared draw for each further draw step (per step, not per turn). The reading is
   `SparedByTheirDrawStep`, and the count of draws since the step began comes from the batch
   (`DrawsInThisStep`), because nothing in the state counts a draw per step — see the ask below.
2. **`AttacksWithCreatures`** (CR 508.1). The matcher had no arm at all, so every such card was
   silently never firing. It now fires **once per declaration** for each player `By` admits who
   declared at least one attacker, never once per creature (that is CR 508.2's `Attacks`), and the
   firing carries every creature of that declaration as its subjects, ordered by object id, with the
   declaring player as the subject player — so "those creatures" is the declaration and not whatever
   is still attacking at resolution.
3. **`TriggeringDamage`'s channel** (CR 603.10). Every firing on a `DamageDealt` event now carries the
   damage that event dealt, in the `TriggerSubject.Chosen` channel the tapped-for-mana colour already
   uses, under the new constant `TriggerSystem.TriggeringDamageAmount` (`"triggering-damage"`),
   decimal and culture-invariant. It travels through `ChosenFor` → `Carried` → `StackEntry.Chosen`,
   which is where the interpreter builds its `EffectContext.Chosen` from, so the value is readable at
   resolution. The **amount reader is not built** (ask 2).

## Asks

1. **State / events (`PlayerState`, `ObjectDrawn`, `StepBegan`) — a per-step draw count, CR 121.1.**
   `PlayerState.ObjectsDrawnThisTurn` is raised by a draw in the upkeep as well, so it cannot answer
   "the first card they draw in each of their draw steps". The batch answers it exactly for the draw
   the rule is written about — CR 504.1's turn-based draw is in the very batch that begins the draw
   step, before any player receives priority (CR 503.1, 117.3a), so a draw in a later batch of that
   step is never the first of it. **The one reading that is wrong**: a draw step whose own draw was
   skipped (a CR 614.5 replacement, or the starting player's first turn under CR 103.7a) — a draw
   taken later in that step is then the first of it and should be spared, and the batch reading says
   it is not. Closing it wants `PlayerState.ObjectsDrawnThisStep`, folded by `ObjectDrawn` and
   cleared by `StepBegan` beside the turn's counter; `SparedByTheirDrawStep` then reads that instead
   of `DrawsInThisStep` and the batch walk goes away. One line of state, one clear, one reader.
2. **Query (`Query.Amounts`) — the `TriggeringDamage` amount, CR 603.10.** Evaluate it by parsing
   `context.Chosen[TriggerSystem.TriggeringDamageAmount]` with
   `int.TryParse(..., CultureInfo.InvariantCulture, ...)`, and 0 when the key is absent (an ability
   L028 already guarantees triggers on damage always has it; a copy of such an ability made outside a
   firing does not). Nothing else in the engine knows what the firing event dealt, so no other
   reading of this amount is available. The trigger half is built and tested.
3. **Query (`Query.Amounts`) — the `AttackersAgainst(Who, IncludingPermanentsTheyControl)` amount,
   CR 508.1.** Count `state.Combat.Attackers` (the declaration stands for the step) whose
   `CombatTarget.Player` is a player `Who` admits, plus — while the flag stands — those whose
   `CombatTarget.Permanent` is a planeswalker or battle controlled by such a player. It is read when
   the ability's intervening `If` is checked, which is both as the trigger goes on the stack and again
   on resolution (CR 603.4), so a creature removed from combat between the two makes the second check
   fail, which is correct. The trigger that fires it exists now, so Mangara `cbcb6d9a` is one arm
   away.
4. **Interpreter and state — `DelayedTrigger.Until`, CR 603.7c.** Untouched here: nothing in the
   trigger system creates a delayed ability, and `DelayedTriggerState` (State/**) has no field for a
   duration. The ask is `DelayedTriggerState.Until: Duration?` written by the interpreter's
   `DelayedTrigger` arm, with the cleanup step dropping every delayed trigger whose duration has
   ended, fired or not — `Once: false` and `Until` answer different questions, so "until end of turn,
   whenever …" sets both and fires each time inside the window. Once the field exists the trigger
   system needs no change: `FromDelayed` already fires whatever the state holds, and
   `TriggerFired.Apply` already removes only a once-only one.
5. **Nothing for a `Searches` trigger.** There is no `Searches` record in the vocabulary today
   (`MtgEngine.Cards` has the word only in two doc comments about not writing two searches); V13 §1
   proposes `Searches(PlayerFilter Who)`. When it lands, the matcher wants an event that says a player
   searched a library — grep found none in `MtgEngine.Rules/Events` — so that is an events-and-search
   ask before it is a trigger ask: the search system must emit "player P searched library L"
   (CR 701.19) and only then can the matcher answer it. One arm in
   `TriggerMatching.Wave23.cs::Wave23` after that.

## Not done in this box

- **No test for the skipped draw step.** The reading is knowingly wrong there (ask 1) and a test that
  asserted the wrong answer would have to be deleted when the seam lands; the doc comment on
  `DrawsInThisStep` names the case and the rule instead.
- **No `AttacksWithCreatures` firing for a player who declared no attackers.** A declaration with an
  empty attacker set is not an attack (CR 508.1a leaves the step with nothing declared), so nothing
  fires; if a card ever wants "whenever an opponent doesn't attack", that is a different trigger.
- **`TriggeringDamage` is carried on every damage firing, not only where lint L028 says an ability
  reads it.** The matcher does not read the ability's effect tree, and one extra string on a firing
  that ignores it costs nothing; the alternative would have the trigger system inspecting effects to
  decide what to remember, which is the interpreter's job.
