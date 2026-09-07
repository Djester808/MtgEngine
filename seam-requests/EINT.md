# EINT — seam requests from the wave-1 exit test

Two joins the exit test found that cannot be repaired from a subsystem folder. Both are one
change each; everything else the exit test needed was fixed in the subsystems (see the report).

## 1. `Subsystems.Wave1()` builds the turn system without the combat system

`Engine/Game.cs`, `Subsystems.Wave1()`.

Combat damage is a turn-based action at the beginning of each combat damage step, CR 510.1 and
CR 703.4m: nothing else may call it, and no command exists for it, by design. The turn system is
where that moment lives — but `Wave1()` composes

```csharp
var turn = new Turn.TurnSystem(zones, casting);
...
var combat = new Combat.CombatSystem(layers, query);
```

so `ICombatSystem.DealCombatDamage` and `ICombatSystem.EndCombat` had no caller anywhere in the
engine, and a game composed through `Wave1()` walks both damage steps and deals nothing. No
creature can ever kill anything, so no game can end in a win.

`TurnSystem`'s constructor now takes the combat system as an **optional** third argument, so
`Wave1()` still compiles unchanged and `TurnStructureTests` still builds one without a combat
system. The change asked for is to build combat first and pass it:

```csharp
var combat = new Combat.CombatSystem(layers, query);
var turn = new Turn.TurnSystem(zones, casting, combat);
```

Until it lands, `IntegrationTable.Composed()` rewraps the turn system with
`wave1 with { Turn = new TurnSystem(wave1.Zones, wave1.Casting, wave1.Combat) }`, which is a
no-op once the wiring is right; delete it with the same commit.

Two things this deliberately leaves alone, both wave 2:

- **CR 510.4**: the first-strike combat damage step exists only when a creature with first or
  double strike is in combat. The turn system has no `ICharacteristics`, so it walks both steps
  always and the first one deals nothing. Players get one extra round of priority in a combat
  with no first strikers. Nothing observable but the step count is wrong.
- **CR 704.3**: the damage is dealt inside `Advance`, and state-based actions run in `Game.Settle`
  after the command returns, so the events show priority granted in the damage step before the
  creature that died left the battlefield. The state after `Game.Apply` is right; the order
  inside the log is not. Fixing it properly means the turn system knowing about SBA, which is
  `Game.Settle`'s job, not a subsystem's.

## 2. `EventLogSerializer` cannot write a log that contains combat

`Events/EventLogSerializer.cs`.

`AttackersDeclared.Attackers` is `ImmutableDictionary<ObjectId, CombatTarget>` and
`BlockersDeclared.Blockers` is `ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>`.
`System.Text.Json` has no dictionary-key converter for `ObjectId`, so `Write` throws

> The type 'MtgEngine.Rules.State.ObjectId' is not a supported dictionary key … Custom
> converters can add support for dictionary key serialization by overriding the
> 'ReadAsPropertyName' and 'WriteAsPropertyName' methods.

and, because one event fails, the **whole log** fails. Every game with a combat phase in it is
unstorable, which is the one thing "the log is the game" may not be.

Asked for: a `JsonConverter<ObjectId>` (and `PlayerId`, which is a dictionary key in
`PlayerState.CommanderDamage` and will meet the same wall the moment a state snapshot or a
future event carries one) overriding `WriteAsPropertyName`/`ReadAsPropertyName`, registered in
`EventLogSerializer.Options`. `#12` and back is the whole format.

Until it lands, `IntegrationTests.AssertTheLogIsTheGame` asserts the fold over the whole log —
`GameReducer.Replay(log)` is the live state, which is the property that matters most — and the
serialization round trip over the log up to the first declaration, which is still every zone
change, draw, mana ability, cast, resolution and turn in the game. Widen it to the whole log
with the converter.
