# EINT5 — the wave-10 joins, played through `Game.Apply`

Eight games on `Subsystems.Wave2()` with made-up definitions, in
`tests/MtgEngine.Rules.Tests/WaveTenIntegrationTests*.cs`. Every one of them writes its log,
reads it back and replays it to the same state.

Five of the six joins the brief named hold through the front door. One does not, and it is the
one the vocabulary's own documentation describes.

## 1. The documented `ManaProduction.ChosenColorKey` spelling reaches no interpreter arm

**Folder:** `MtgEngine.Rules/Interpreter` (`EffectInterpreter.Verbs.cs`, `PoolFor`).
**Rule:** CR 608.2d with 106.12a.

`MtgEngine.Cards/Triggers.cs` documents the two halves of "whenever a land you control is tapped
for green mana, add an additional one mana of that color" as a `TappedForMana` trigger and an
`AddMana` "whose `ManaProduction` carries the same `ChosenColorKey`". `TriggerSystem.cs` says the
same thing about `TappedForManaColor`: "the card's other half ... is an `AddMana` whose
`ManaProduction.ChosenColorKey` is this key, so the two ends agree without either naming a card."

`PoolFor` reads the key only when `ChosenColor` is **also** true:

```csharp
if (production.ChosenColor && production.ChosenColorKey is { } key
    && run.Context.Chosen.TryGetValue(key, out var colour))
    return ManaSymbols.OfColor(colour);
throw new NotImplementedException($"no interpreter arm for this shape of {nameof(ManaProduction)}");
```

So the documented spelling falls through to the throw. It is not a refusal: a
`NotImplementedException` comes out of `Game.Apply` while the trigger resolves, so the game is
not merely wrong, it is unplayable — the caller gets an exception rather than a `Refused` naming
a rule.

**Smallest reproduction** (`The_documented_chosen_colour_key_spelling_reaches_no_interpreter_arm`):
an artifact whose only ability is

```csharp
new TriggeredAbility(
    new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), OfColor: Color.Green),
    new AddMana(new ManaProduction(ChosenColorKey: TriggerSystem.TappedForManaColor)))
```

on the battlefield; tap a land that makes `{G}`; the trigger matches, goes on the stack carrying
the colour, and throws as it resolves. The same card with `ChosenColor: true` added plays
correctly (`A_tapped_for_mana_trigger_adds_a_mana_of_the_colour_the_firing_produced`): two green
from the green land, one white from the white one, no firing on the colour the filter misses.

The linter does not catch it. `L015` only forbids `OfColor` and `OfChosenColorKey` together on
the trigger; nothing lints an `AddMana` whose `ChosenColorKey` is set without `ChosenColor`, so a
lint-clean card file can crash a game.

**Ask:** either `PoolFor` reads `ChosenColorKey` on its own (the reading the two documentation
comments promise, and the one a translator would produce), or `ChosenColor` becomes required
next to it and a lint rule says so. The test asserts the crash so it keeps its meaning either
way; flip it when this lands.

Not a gap, but worth recording next to it: the throw is the wrong shape whichever spelling wins.
Every other "this vocabulary node has no arm" road in the engine is a `RefusedException` with a
rule (see `Unsupported`, CR 608.2, after E9h); this one is a bare `NotImplementedException` that
no caller can turn into a game event.

## What held

- **CR 106.12a with 608.2d** — a `TappedForMana` firing carries the colour it produced and the
  resolution adds one more of it (E6d + E9i), with the colour filter missing the other land.
- **CR 601.2c with 702.5b** — an "enchant creature" Aura announced at a player is refused at the
  cast naming 601.2c and stays in hand; the `OfPlayer` twin resolves attached to the player and
  survives a turn of CR 704.5m checks (E3h + E3f + E5c).
- **CR 608.2d** — `ActivateAbility(..., ManaColors: "W")` on "{T}: add one mana of any color" adds
  white; the same activation with no colour is refused naming 608.2d and costs nothing (E3h + P13).
- **CR 613.10 with 704.5a** — a player under "you can't lose the game this turn" sits at 0 life
  with the game still going, and loses on the first check after the turn's cleanup ends the
  effect (E5e + E0e).
- **CR 702.16** — a player with `PlayerProtectionFromEverything` takes none of an unblocked 2/2's
  combat damage; the control game with no protection takes all two, so the prevention is proved
  to be prevention (E5e's replacement arm through a real combat).
- **CR 510.3 with 616.1** — a combat damage batch halted twice by three one-damage shields deals
  the remaining point after the last answer, and the active player holds priority on an open
  combat damage step that a round of passes then ends (E4e).

## Wave-9 assertions not flipped

None of E9j, E3i, E6e or E4f had merged into `card-engine` when this ran (`git log card-engine`),
so every gap `WaveNineIntegrationTests*.cs` asserts as it behaves is still the behaviour, and
nothing there was flipped.
