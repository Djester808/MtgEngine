# E4b — combat damage is offered to the replacement system

Wave 7. `MtgEngine.Rules/Combat/` now proposes every event of the combat damage batch to
`IReplacementSystem` (CR 614.1a, 616.1) through a settable handle, exactly as `ZoneSystem` does
for zone changes. `CombatSystem.Replacements` is public and defaults to null; with it null the
batch is byte-for-byte what it was, which is what `Wave1()` wants. Two things are not in files
I own.

## 1. `Wave2()` must set the handle (CR 614.1a)

`MtgEngine.Rules/Engine/Game.cs` already has, in `Wave2()`:

```csharp
        zones.Replacements = replacement;
```

The combat system is built a few lines below it, after `interpreter.Casting = casting;`:

```csharp
        var combat = new Combat.CombatSystem(layers, query);
```

One line is needed, immediately after that construction:

```csharp
        // CR 614.1a and 616.1: combat damage is proposed, not performed, so a prevention shield,
        // a damage multiplier and every WouldBeDealtDamage replacement bite in combat too.
        combat.Replacements = replacement;
```

`Wave1()` stays as it is: its replacement system is `Stubs.Replacement()`, which throws, and
`zones.Replacements` is left null there for the same reason. Null is the honest wave-1 answer —
a game of lands and vanilla creatures has nothing to replace.

Until that line lands, everything below is reachable only from a test that sets the handle by
hand: `CombatReplacementTests` does, including the real `ReplacementSystem` with a
"prevent all damage that would be dealt to you this turn" shield, but no game does.

## 2. A halted combat damage step is not resumed (CR 616.1, 510.2)

When two replacements apply to one damage event the affected player is asked which goes first
(CR 616.1), the game stops, and nothing else happens until it is answered (CR 101.4). Combat now
does the right half of that: the question stands, `state.Pending` is set, and the rest of the
batch is **not** dealt.

**The gap.** Nothing deals the remainder afterwards. `ChoiceSystem.Resume` hands a
`replacement-order` answer to `IReplacementSystem.Resume`, which finishes the loop for *that one
event*; the combat damage step is never re-entered, so every assignment after the halted one is
lost. `CombatReplacementTests.A_halt_leaves_the_question_pending_and_the_rest_of_the_batch_undealt`
asserts the gap as it stands rather than pretending it is not there.

Fixing it needs something the choice system cannot express today, because the remainder of a
CR 510.2 batch is not an effect the interpreter can walk and not a stack object it can resolve.
What would be enough, cheapest first:

- **A progress marker on the pending choice.** The batch is deterministic given the state the
  step began on: `DealCombatDamage(state, firstStrike)` recomputes exactly the same assignments.
  So the question only needs to carry which of them had already been offered — an index into the
  batch, plus the step (`firstStrike`) — and the resumption re-runs the step from that index.
  That is the same shape E9b asked for in wave 3 for a halting verb's progress marker, and it
  wants two fields on `ChoiceContext.Marks` (`"combat-damage-step"`, `"combat-damage-index"`)
  and one seam member:

  ```csharp
  // MtgEngine.Rules/Engine/Seams.cs, on ICombatSystem
  /// <summary>CR 510.2 and 616.1: deal the rest of a combat damage batch a replacement-order
  /// question stopped, from the assignment the question was asked about.</summary>
  [RuleRef("510.2")]
  Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from) =>
      throw new NotImplementedException("Resuming a halted combat damage step is not built yet.");
  ```

  with `ChoiceSystem.Resume`, after the replacement system's own `Resume` has finished the
  halted event, calling it when those marks are present.

- **Or the batch as state**, a `CombatState.PendingDamage` list the reducer folds and the step
  drains. Larger, and it puts assignments in the log twice.

I have not built either: both are a seam member plus a choice-system call, and neither file is
mine. The recomputation form is the one I would take — the assignments are a pure function of
the opening state, and the marks are two strings.

## 3. Nothing else

`ICombatSystem` is unchanged: the handle is on the concrete `CombatSystem`, the way
`ZoneSystem.Replacements` is, so no fake in any test project has to grow a member.
