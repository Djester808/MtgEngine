# E9f — seam requests

Wave 6's two seams landed, so the may-cast arm casts for real:
`MayCast(WithoutPayingManaCost: true)` answered yes now hands the casting system
`new CastSpell(who, card, WithoutPayingManaCost: true)`, and `CastPlan.For` records `"free"`
and lifts the CR 601.3 zone check for it. `MayCast` with a `Until` duration creates a
`MayPlayFromZone` continuous effect instead of casting. Three things are left, none of them in
`MtgEngine.Rules/Interpreter`.

## 1. Nothing reads `MayPlayFromZone` (CR 601.3, `Casting/` is E3e's)

`MayCastCard` now emits, for "you may cast it this turn":

```csharp
Create(run, new MayPlayFromZone(may.Who ?? new You(), from), cards, [who], until)
```

with `AppliesTo` the cards the resolution named and `from` the zone they were in. The effect is
on `GameState.ActiveEffects` and **no reader exists** — `grep -rn MayPlayFromZone` finds only the
record and the canonicaliser — so the permission is state a table can see and not yet a permission
a player can take. `CastPlan.For` is the place: where it throws

```csharp
if (zone != CardZone.Hand && !OnAdventure(card))
    throw new RefusedException("A card in that zone cannot be cast without permission.", "601.3");
```

CR 601.3a's "an effect allows it" is exactly an active `MayPlayFromZone` whose `AppliesTo` holds
this card (or whose `What` filter matches it) and whose `Who` resolves to the caster:

```csharp
// CR 601.3a: an effect that says a player may cast this card is the permission the zone check
// wants. The effect is created by the interpreter's MayCast arm (Until duration) and by any
// "you may play lands from your graveyard" static.
private static bool Permitted(GameState state, GameObject card, PlayerId player) =>
    state.ActiveEffects.Any(e =>
        e.Effect is MayPlayFromZone from
        && from.From == card.Zone.Zone
        && (e.AppliesTo.Contains(card.Id) || e.AppliesTo.IsEmpty)
        && (e.AppliesToPlayers.IsEmpty || e.AppliesToPlayers.Contains(player)));
```

Until it is read, a card given a lasting permission still cannot be cast, and the arm says so in
its own doc comment rather than pretending otherwise.

## 2. The may-cast question cannot carry cost objects (CR 601.2b, 601.2c)

CR 601.2b: an additional cost printed on the card is paid whether or not the mana cost is, and
CR 601.2c has the objects it needs chosen as the spell is announced. `CastSpell.CostObjects` is
the channel, but the interpreter's question is `Ask(run, who, "may-cast", …, YesNo, 1, 1)` — a
yes/no with no objects to offer, and the answer comes back as `"yes"`. So the arm **refuses by
name** a card whose printed face carries any `AdditionalCosts`:

```csharp
if (PrintedFace(run.State, card) is { AdditionalCosts.Count: > 0 })
    throw new RefusedException(
        "That card's additional cost cannot be chosen from a may-cast question; asked for in seam-requests/E9f.md",
        "601.2b");
```

That is deliberately wider than it has to be: an additional cost of `PayLife` or `SacrificeSelf`
needs no object and could be cast today. Narrowing it means the interpreter would carry a table of
which `Cost` shapes name objects, which is `CostPayment`'s knowledge, not the interpreter's. Two
ways out, either is fine:

- **`ICastingSystem` answers it.** A `Refusal? Objects(GameState, CastSpell)`-shaped member, or
  simply letting `Check` fail on the missing objects, so the interpreter hands the command over and
  the casting system is the one place that knows what a cost needs (preferred; E3e).
- **A two-step question.** The "may-cast" answer of `"yes"` is followed by a second question of
  kind `"cast-cost"` offering the legal objects for each cost the plan names, whose answer fills
  `CostObjects`. That needs the plan before the cast, which only the casting system can build, so
  it is the same seam wearing a hat.

## Not asked for, recorded instead

- **`MayCast(AsCopy: true)`** refuses naming CR 706.2. A copy of a card in a zone is a new object,
  and `IZoneSystem.CreateToken(CopiableValues, …)` (wave 4) makes a permanent, not a spell being
  cast. It is a casting seam when a card needs it.
- **`MayCast(AlternativeCost: …)`** refuses naming CR 118.9. `CastSpell.AlternativeCost` is a
  `KeywordKind?` — a printed keyword whose cost `CastPlan` parses — and `MayCast.AlternativeCost`
  is a `Cost` the effect states in its own words ("you may cast it by paying {1} rather than…").
  A `Cost?` beside the flag on `CastSpell` is the channel if the corpus needs it; none of the
  pilot's faces did.
- **A lasting permission that also changes the cost** ("you may cast it this turn without paying
  its mana cost") refuses naming CR 118.9: `MayPlayFromZone(Who, From, What, TopOfLibraryOnly)` has
  no cost half. This is a **vocabulary request** (V-series), not an engine one — the record needs a
  `bool WithoutPayingManaCost = false`, and then §1's zone check hands the flag to the `CastSpell`
  the player announces. Foretell, suspend and "cast it later" cards all want it.
- **`CastPlan` is `internal` to `MtgEngine.Rules` and there is no `InternalsVisibleTo`**, so
  `InterpreterDesignationTests` asserts the flag on the `CastSpell` the interpreter hands over, not
  the `"free"` string on the resulting stack entry. The `"free"` half is `CastPlan.For`'s and has no
  test in any project. If it should have one, either add `<InternalsVisibleTo Include="MtgEngine.Rules.Tests" />`
  to `MtgEngine.Rules.csproj` or let a casting-side test in `CastingTests` assert
  `StackEntry.OptionalCostsPaid` after a free cast (E3e's box, one test).
