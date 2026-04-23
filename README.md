# MtgEngine - Phase 1: Domain & Rules Engine

A pragmatic Magic: The Gathering rules engine built in C# / .NET 8.

## Solution Structure

```
MtgEngine.sln
├── MtgEngine.Domain/          # Pure domain models, enums, value objects, interfaces
│   ├── Enums/Enums.cs         # CardType, KeywordAbility, ManaColor, Phase, Step, Zone, etc.
│   ├── ValueObjects/
│   │   └── ManaCost.cs        # Immutable ManaCost + ManaPool value objects
│   ├── Models/
│   │   ├── Card.cs            # CardDefinition, Card (instance), Permanent
│   │   ├── GameState.cs       # PlayerState, GameState, CombatState (all immutable records)
│   │   └── StackObjects.cs    # IStackObject, SpellOnStack, ActivatedAbilityOnStack, TriggeredAbilityOnStack
│   └── Interfaces/
│       └── IAbility.cs        # IAbility, IActivatedAbility, ITriggeredAbility, IStaticAbility, GameEvents
│
├── MtgEngine.Rules/           # Rules engine -- zero ASP.NET dependencies
│   ├── GameEngine.cs          # Top-level orchestrator (pure functions)
│   ├── ZoneManager.cs         # Zone transitions: casting, playing lands, resolving, removal
│   ├── SBA/
│   │   └── StateBasedActions.cs  # CR 704 SBAs: lethal damage, 0 toughness, life loss, legend rule
│   ├── Turn/
│   │   └── TurnStateMachine.cs   # Phase/step transitions, untap, draw, cleanup
│   └── Combat/
│       └── CombatEngine.cs    # Declare attackers/blockers, damage assignment, trample, deathtouch
│
└── MtgEngine.Rules.Tests/     # xUnit + FluentAssertions test suite
    ├── TestFactory.cs          # Test data builders
    ├── ManaCostTests.cs
    ├── StateBasedActionTests.cs
    ├── CombatEngineTests.cs
    ├── ZoneManagerTests.cs
    ├── TurnStateMachineTests.cs
    └── GameEngineIntegrationTests.cs
```

## Key Design Decisions

### Immutable GameState
`GameState` is a C# `record` type. Every action returns a new `GameState` rather
than mutating the existing one. This makes replaying, debugging, and testing trivial.

### Pure Functions
`GameEngine`, `ZoneManager`, `CombatEngine`, `TurnStateMachine`, and `StateBasedActions`
are all static classes with pure function signatures:
```csharp
GameState SomeAction(GameState state, ...args) => newState;
```

No side effects, no DI required. The `MtgEngine.Rules` project has zero dependencies
on ASP.NET or any infrastructure concerns.

### SBAs Run After Every Action
`GameEngine` calls `StateBasedActions.Apply()` after every player action. SBAs loop
until stable (no more apply), matching CR 704 behavior.

## Running Tests

```bash
cd MtgEngine
dotnet test
```

## Building

```bash
cd MtgEngine
dotnet build
```

## What's Implemented (Phase 1)

- [x] Card definition and instance model
- [x] Permanent with counters, tapped state, summoning sickness
- [x] ManaCost and ManaPool value objects with color-aware payment
- [x] Immutable GameState and PlayerState records
- [x] Full zone model (library, hand, battlefield, graveyard, exile, stack)
- [x] Turn structure: all phases and steps with correct sequencing
- [x] Untap step (untaps active player permanents)
- [x] Draw step (skips first player turn 1)
- [x] Cleanup (discard to 7, clear damage, clear mana pools)
- [x] Playing lands (one per turn, main phase only)
- [x] Tapping basic lands for mana
- [x] Casting spells (sorcery speed enforcement, mana payment, Flash)
- [x] Stack with LIFO resolution
- [x] Priority passing (simplified two-player model)
- [x] State-based actions: lethal damage, 0 toughness, life loss, poison, legend rule, planeswalker loyalty
- [x] Combat: declare attackers (tapping, vigilance, haste, summoning sickness)
- [x] Combat: declare blockers (flying restriction, reach)
- [x] Combat: damage assignment (trample, deathtouch, lifelink, first strike)
- [x] GameEngine orchestrator tying it all together

## What's Next (Phase 2)

- [ ] Activated abilities (tap abilities, loyalty abilities)
- [ ] Triggered abilities (ETB, dies triggers, combat triggers)
- [ ] Targeting system with legality validation on cast and resolution
- [ ] Instant-speed activated abilities on the stack
- [ ] Spell effects for instants/sorceries (deal damage, destroy, draw, counter)
- [ ] `HasLandPlayedThisTurn` reset on new turn
- [ ] Multiple blocker ordering prompt
- [ ] Full priority model (track "both passed" explicitly)

## Phase 3+

See `MTG-Rules-Engine-Architecture.docx` for the full roadmap including
the .NET Web API, SignalR hub, Angular frontend, and Scryfall integration.
