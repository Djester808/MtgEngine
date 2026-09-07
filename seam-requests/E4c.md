# E4c — a halted combat damage batch resumes

Wave 8. The gap E4b recorded is closed: a replacement-order question raised inside the combat
damage step now marks itself with the step and the index of the hit it stopped on, and
`CombatSystem.ResumeCombatDamage` deals the rest once the answer has finished that one event.
One line is not in a file I own.

## 1. `Wave2()` must hand the combat system to the choice system (CR 510.2)

`MtgEngine.Rules/Engine/Game.cs`, in `Subsystems.Wave2()`, builds the choice system as:

```csharp
        var choices = new MtgEngine.Rules.Choices.ChoiceSystem(zones, turn, interpreter, casting);
```

`ChoiceSystem` gained a fifth optional parameter, `ICombatSystem? combat = null`, and that line
needs it — the combat system is already in scope, built on line 85:

```csharp
        // CR 510.2 and 616.1: a replacement-order question raised inside the combat damage step
        // halts the batch; answering it deals the rest, so the choice system needs the combat system.
        var choices = new MtgEngine.Rules.Choices.ChoiceSystem(zones, turn, interpreter, casting, combat);
```

`Wave1()` stays as it is: it composes `new Stubs.Choices()`, and its combat system has no
replacement system, so no wave-1 batch can halt. Null is the honest answer there, and with it null
the choice system answers a replacement-order question exactly as it did before.

Until that line lands the resumption is reachable only from a test that passes the combat system by
hand, which `CombatReplacementTests` does — through the real `ReplacementSystem` with two shields on
one player, so both halts and both resumptions are the real route.

## 2. Nothing else

`ICombatSystem.ResumeCombatDamage(state, firstStrike, from)` was already on the seam with a throwing
default (wave 7's decision); `CombatSystem` implements it now and `Stubs.Combat` still throws, which
is what `WaveEightSeamTests.A_combat_system_without_resumption_says_so` asserts. No event, no state
record and no interface member changed.

## 3. What the marks are, for whoever reads a log

On the pending choice's `ChoiceContext.Extra`, written by combat when the batch halts and read by
`ChoiceSystem.RestOfTheBatch`:

- `"combat-damage-step"` — `"first"` or `"regular"` (`ChoiceKinds.FirstStrikeDamage` /
  `RegularDamage`), which combat damage step the batch belongs to, CR 510.4.
- `"combat-damage-index"` — the index, in that step's batch, of the hit the question was asked
  about. The batch is a pure function of the board, so `ResumeCombatDamage` recomputes it and skips
  everything up to and including that index. Nothing is stored between the question and its answer
  (CR 101.4), and the marks travel on the `ChoiceRequested` event as well as on the state, so a
  replayed log stops on the same marked question.

## 4. One gap left, smaller than the one that closed (CR 702.15b)

A creature whose hit raised the order question does not gain its lifelink for **that** hit. The
amount that hit finally dealt is decided inside `IReplacementSystem.Resume`, and
`ResumeCombatDamage(state, firstStrike, from)` is not told it — the seam carries the index and
nothing else, and the state does not record damage per step. Every other hit of the batch, including
later hits by the same creature, gains normally, and a halt on a *life gain* resumes exactly (the
gain is then the event the replacement system finishes).

Cheapest fix, if a card ever makes it matter: the choice system already holds the resumed outcome, so
`ResumeCombatDamage` could take the events that finished the halted hit
(`Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from, ImmutableList<GameEvent> dealt)`)
and run the lifelink for index `from` from them. That is one seam signature, and it is a wave-9 line
rather than something to guess at now.

## 5. A behaviour change worth knowing about

Lifelink is now offered per hit rather than per creature. A creature that hits twice in one step
(lethal to a blocker and the rest to the next one, CR 510.1c with 702.2b, or trample's excess, CR
702.19b) used to gain once for the total and now gains once per hit. The total is identical and the
life gain still follows the damage actually dealt (CR 702.15b); what changed is that the gain belongs
to the hit it came from, which is what makes a halt between two hits of one creature resume exactly.
