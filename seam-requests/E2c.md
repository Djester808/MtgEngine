# E2c — the monarch's draw, a per-step draw record, a dated delayed trigger, the two turn counts

Wave 24. Built in `MtgEngine.Rules/Turn/**` and `MtgEngine.Rules/State/**`, tested in
`tests/MtgEngine.Rules.Tests/TurnStructureTests.Wave24.cs` (13 tests; 43 in the anchor, 986+ in the
Rules project). Two of the four items are complete inside the box; the third needs one field written
by the interpreter (E9s); the fourth needs two one-line folds in `Events/**`, which is E1e's box this
wave — the shape and both call sites are spelled out below so it is a copy, not a design.

## Done here

1. **CR 724.3 — the monarch draws at the beginning of their end step.** `TurnSystem.Advance` now calls
   `EndStep` as the end step begins, beside the draw step's `DrawStep`
   (`Turn/TurnSystem.Wave24.cs`). It draws for `state.Monarch` when the monarch is the active player —
   CR 724.3 gives the *player* the ability "at the beginning of **your** end step, draw a card", so the
   monarch draws at most once a round and nothing in anyone else's end step — and nothing for a player
   who has lost (CR 104.3). **The divergence, recorded on purpose**: the rule is a triggered ability
   and this is a turn-based action, because nothing in the engine can give a player an ability (a card
   is data; the monarch is not a card). That was the decision in `seam-requests/E9r.md`. The one
   observable difference is that the draw does not use the stack and so cannot be responded to. If a
   later wave wants the stack for it, the road is a trigger the trigger system fires on
   `StepBegan(End)` with `state.Monarch` as its controller, and `EndStep` is deleted in the same
   change — not both.
2. **CR 603.7c with CR 514.2 — cleanup drops a dated delayed trigger.** `DelayedTriggerState` gained
   `Duration? Until = null` (defaulted last, so every existing construction still compiles and every
   stored log still reads), and `FinishCleanup` drops every delayed trigger whose `Until is
   UntilEndOfTurn`, fired or not. It travels on the event that already ends this turn's continuous
   effects: `EndOfTurnEffectsEnded(ImmutableList<int> Effects, ImmutableList<int>? DelayedTriggers = null)`
   in `Turn/TurnEvents.cs`. **No new event type**, so `tests/MtgEngine.Rules.Tests/events.txt` and
   `EventLogSerializer.KnownEventNames` are untouched — which is why the second list rides on this
   event rather than getting one of its own. A trigger with no duration, or one dated to a window this
   turn does not close (`UntilYourNextTurn`, `UntilEndOfCombat`, `Permanently`), is left alone: the
   step that ends *its* duration drops it, and dropping it here deletes an ability that can still fire.
3. **CR 121.1 — the two per-turn counts V13 § 3 asks for** are readings on `GameState`, needing no new
   fold and no new field:
   - `int CardsDrawnThisTurn(PlayerId who)` — `PlayerState.ObjectsDrawnThisTurn`, which `ObjectDrawn`
     already raises and `TurnBegan` already clears. One draw event per card, so a card drawn and then
     discarded still counts, which is the reading these amounts exist to protect.
   - `int LandsEnteredThisTurn(PlayerId who)` — walks `TurnHistory.EnteredBattlefield`, resolving each
     id against `Objects` and, when it has since left, against `TurnHistory.LeftBattlefield`, so a land
     that has left still counts; plus the land tokens in `TurnHistory.TokensCreated` that were created
     on the battlefield (see ask 3). Each entry counts once, and the controller is the one it entered
     under (`InitialController`, CR 110.2a), so a later change of control does not move the count.
4. **CR 121.1 — the per-step draw record.** `PlayerState.ObjectsDrawnThisStep` exists, with the two
   folds said once as methods so no call site can raise one counter and forget the other:
   `PlayerState.Drew()` (both counters up) and `PlayerState.StepBegins()` (the step's count to zero,
   the turn's untouched). **Nothing calls them yet** — the two call sites are in `Events/**`, ask 1.

## Asks

### 1. `Events/**` (E1e this wave) — two lines, and the per-step draw record is live

In `MtgEngine.Rules/Events/GameEvent.cs`:

```csharp
// ObjectDrawn.Apply — CR 121.1: both counters, the turn's and the step's.
public override GameState Apply(GameState state)
{
    var p = state.Player(Player);
    return state with { Players = state.Players.SetItem(Player.Value, p.Drew()) };
}

// StepBegan.Apply — CR 500.1: what was drawn in the step that ended is not drawn in this one.
public override GameState Apply(GameState state) => state with
{
    Turn = state.Turn with { Step = Step },
    Players = [.. state.Players.Select(p => p.StepBegins())],
};
```

`Drew()` is exactly the increment `ObjectDrawn.Apply` writes today plus the step's, so the turn's count
does not change behaviour. `StepBegins()` returns the same instance when the count is already zero, so
a step with no draws allocates nothing new.

### 2. `Triggers/**` — how `SparedByTheirDrawStep` should read it (CR 121.1 with CR 504.1)

Once ask 1 lands, `TriggerMatching.Wave23.cs`'s `DrawsInThisStep(window, player)` and its batch walk go
away and the reading becomes the state's:

```csharp
private static bool SparedByTheirDrawStep(PlayerId player, TriggerWindow window) =>
    window.Step == Step.Draw
    && window.State.Turn.ActivePlayer == player
    && window.State.Player(player).ObjectsDrawnThisStep == 1;
```

`== 1` because the count includes the draw being matched, which is what `DrawsInThisStep` counts today
— **if** `window.State` is the state with this `ObjectDrawn` folded in. If the window carries the state
as of before the event, it is `== 0`; the trigger box knows which and one of the two is a copy of the
existing comparison. This is what closes the case E6g knowingly got wrong: a draw step whose own draw
was skipped (CR 614.5's replacement, or the starting player's first turn under CR 103.7a) leaves the
count at 0, so a draw taken later in that step is the first of it and is spared. That case wants a test
in the trigger box; it cannot be written here, because with the folds unwired the count is always 0.

### 3. `Events/**` (E1e) — a token created on the battlefield has not "entered" it (CR 111.1)

`ObjectCreated.Apply` records a token in `TurnHistory.TokensCreated` but never in
`TurnHistory.EnteredBattlefield`; only `ObjectMoved` adds to that list. A created token **does** enter
the battlefield (CR 111.1), so every reading of "entered this turn" is short by every token until:

```csharp
// ObjectCreated.Apply, beside the TokensCreated line — CR 111.1: a created token enters the battlefield.
if (Object.Zone.Zone == Zone.Battlefield)
    history = history with { EnteredBattlefield = history.EnteredBattlefield.Add(Object.Id) };
```

`LandsEnteredThisTurn` counts land tokens through `TokensCreated` meanwhile and de-duplicates by object
id, so it stays correct when this lands; it is filed because the gap is not only this amount's.

### 4. E5-series (query) — where the two counts live

The `CardsDrawnThisTurn(Who)` and `LandsEnteredThisTurn(Who)` amounts of V13 § 3 evaluate their
`PlayerFilter`/`PlayerRef` to seats and sum `state.CardsDrawnThisTurn(seat)` and
`state.LandsEnteredThisTurn(seat)` — both on `GameState` (`State/GameState.cs`), both `[RuleRef("121.1")]`.
Smuggler's Share's "any opponent drew two or more cards this turn" is a max over the seats, not a sum:
the printed line is about one opponent. `LandsEnteredThisTurn` reads the **printed** face of the object
that entered, because a `GameState` method has no layer system to ask; if an arm needs the computed type
(a land only an effect made one), the reading it wants is its own `ICharacteristics` over the same two
history lists, and the seam is one more overload here — say so rather than counting the board, which is
the reading the amount exists to refuse.

### 5. E9s (interpreter) — `DelayedTriggerState.Until` is added here, so write it

The field exists now (item 2 above): `DelayedTriggerState(..., ImmutableDictionary<...> Remembered,
Duration? Until = null)`. The interpreter's `DelayedTrigger` arm sets it from the ability's printed
duration; `Once` is unchanged and answers a different question, so "until end of turn, whenever a
creature dies …" sets `Once: false` **and** `Until: new UntilEndOfTurn()` and fires each time inside the
window. Nothing else needs to change: `TriggerFired.Apply` still removes only a once-only one, and the
cleanup step now removes the dated ones.

## Not done in this box

- **No test that a draw raises the step's count through a game.** The fold is ask 1; a test here would
  assert the state the engine does not yet reach. The two `PlayerState` methods are tested directly.
- **No `UntilEndOfCombat` or `UntilYourNextTurn` drop.** Those durations end in steps the turn system
  passes through without a "what ends here" pass; CR 514.2 is the cleanup's, and each other duration
  wants its own moment rather than a second list in this one.
- **Nothing for the monarch's other two halves.** CR 724.4 (combat damage to the monarch moves the
  monarchy) is the combat system's and is untouched; the `BecomesMonarch` trigger reader is the trigger
  system's. Both were named in `seam-requests/E9r.md` and neither is in this box.
