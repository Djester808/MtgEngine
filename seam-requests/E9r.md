# E9r — the monarch has a state field and no way to write it

Wave 23. Interpreter side is `MtgEngine.Rules/Interpreter/**`; the tests are
`tests/MtgEngine.Rules.Tests/InterpreterTests.Wave23.cs`. Items 2 and 3 of the brief needed no
seam: `StackTargetsChanged` already carries a `TargetPlayers` list, so the retarget arm's new player
half rides on the event as it stands, and the excess-damage and `PayCost.Then` readings are both
inside the interpreter. **One ask**, § 1.

## 1. `BecomeMonarch` — a `MonarchChanged` event (Events/**, State/**)

`cards/f63c2438-27d4-449a-828f-f0a2ea86ff16.json` fails with "There is no interpreter arm for
BecomeMonarch". `GameState.Monarch` exists (`State/GameState.cs`, `[RuleRef("724")]`) and two
readers already ask it — `Query.Conditions.cs`'s `YouAreMonarch` (CR 724.2) and
`Query.Selectors.cs`'s `Monarch` player reference — but **nothing in the engine writes it**: there is
no event whose `Apply` sets it, so the designation cannot exist in a log, and the log is the game.

The interpreter cannot add the event itself. An event needs three things outside
`MtgEngine.Rules/Interpreter/**`:

- the record, `MonarchChanged(PlayerId Player)`, with `Apply(state) => state with { Monarch = Player }`
  and `[RuleRef("724.1")]`. It belongs beside the other whole-game designations rather than in
  `Interpreter/InterpreterEvents.cs`, because CR 724.4 moves the monarchy on combat damage and CR
  724.3 draws a card at the monarch's end step, so the combat and turn systems emit it too;
- the line in `tests/MtgEngine.Rules.Tests/events.txt` (`EventLogTests` compares that file to
  `EventLogSerializer.KnownEventNames`, so the event does not exist until both name it), and the
  serializer's registration;
- `Engine/Stubs.cs`/`Seams.cs`: nothing new, the event needs no seam method.

Exactly one arm reads it, and it is written and waiting behind the refusal:
`case BecomeMonarch` in `Interpreter/EffectInterpreter.Backlog.cs` throws a `RefusedException` citing
CR 724.1 today; it becomes
`Emit(run, new MonarchChanged(FirstPlayer(run, monarch.Who)))`, skipped when that player already
holds it (CR 724.2: there is at most one monarch, and re-designating the same player changes
nothing, so no event and nothing triggers).

Two more halves of CR 724 are other boxes' and are **not** asked for here, so the card above verifies
on the designation alone:

- **CR 724.3** — the monarch draws a card at the beginning of their end step. That is a turn-based
  action the turn system takes, reading `state.Monarch` at `Step.End`.
- **CR 724.4** — a creature dealing combat damage to the monarch makes its controller the monarch.
  That is the combat damage system emitting `MonarchChanged`, and it is the only mover other than a
  card that says so.
- **The `BecomesMonarch` trigger** (`MtgEngine.Cards/Triggers.cs`) has no reader either: the trigger
  system matches events, so it wants the same event before it can fire. Worth pairing with the ask,
  because a court's second ability triggers on it.

## 2. Not a seam — recorded so the next reader does not go looking

- **`StackTargetsChanged` already carries the players.** P22 § 3 read the arm as needing an
  `Events/**` change; it does not. The event's third field is `ImmutableList<PlayerId> TargetPlayers`
  and the arm was simply passing `entry.TargetPlayers` straight through. It now writes the chosen
  seats, and re-adds a gift's promised player (CR 115.10), who rides on that list and is not a target.
- **The any-target slot's object filter is `Query.AnyTargetFilter`.** The retarget arm reaches the
  concrete query layer for that one static (aliased `QueryLayer`) rather than spelling CR 115.4's
  three card types a second time. If that reference is unwanted, the seam ask is one member on
  `IQuery` — `ObjectFilter AnyTarget { get; }` — not a copy of the filter in the interpreter.
- **The excess of a planeswalker's or a battle's damage** is read from the `loyalty` and `defense`
  counters (CR 120.4a), which the state already keeps; no seam.
