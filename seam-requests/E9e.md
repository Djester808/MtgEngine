# E9e — seam requests

Wave 5's `ObjectDesignated` and `StackTargetsChanged` landed and the arms that needed them are real:
prepare/unprepare, suspect, goad, monstrosity, harness, the Ring-bearer, `ChangeTarget` and
`AnyTarget` all play now, tested in `tests/MtgEngine.Rules.Tests/InterpreterDesignationTests.cs`.
Two seams are left, one a line in the composition and one a field on a command.

## 1. `Wave2()` sets the interpreter's casting handle (CR 601)

`EffectInterpreter` now carries the handle E9d asked for, on
`MtgEngine.Rules/Interpreter/EffectInterpreter.Retarget.cs`:

```csharp
[RuleRef("601")]
public ICastingSystem? Casting { get; set; }
```

`MayCast` refuses naming CR 601 while it is null, so a table is told rather than the card doing
nothing. One line in `MtgEngine.Rules/Engine/Game.cs`, in `Wave2()`, immediately after the casting
system is constructed and before the turn system takes it:

```csharp
        var casting = new Casting.CastingSystem(zones, query, layers, interpreter);

        // CR 601: a card cast from inside a resolution is announced by the casting system, which
        // resolves through the interpreter — the same cycle as the replacement handle above.
        interpreter.Casting = casting;
```

`Wave1()` has no interpreter and needs nothing.

## 2. `CastSpell` cannot say "without paying its mana cost" (CR 601.2h)

`MayCast.WithoutPayingManaCost` is twelve of the pilot's 200 faces, and there is no channel for it:
`CastSpell.AlternativeCost` is a `KeywordKind?`, and `CastPlan.Alternative` requires a printed
keyword ability whose cost it can parse. So the arm **refuses naming CR 601.2h** rather than
handing the casting system a command that would charge the printed mana cost — a spell is cast for
the cost that was announced or it is not cast at all. The channel:

```csharp
// Commands/Command.cs, on CastSpell, after CostObjects
/// <summary>
/// CR 601.2h: the spell is cast without paying its mana cost, the permission an effect gave
/// ("you may cast it without paying its mana cost", CR 118.9). The mana cost is not paid; every
/// additional cost still is (CR 601.2b), which is why this is a flag beside the costs and not an
/// alternative cost of its own.
/// </summary>
bool WithoutPayingManaCost = false
```

and in `CastPlan.For`, where `Alternative` returns null today:

```csharp
        if (command.WithoutPayingManaCost)
        {
            paid.Add("free");                 // CR 118.9: recorded on the entry, so "if it was cast
            return [];                        // that way" can be asked when it resolves
        }
```

That also lifts CR 601.3's "a card in that zone cannot be cast without permission" for the card the
effect named, because `MayCast` *is* the permission (CR 601.3a) — the plan's zone check needs the
same flag to let a card be cast from a graveyard or from exile. With both in place the arm's refusal
becomes `new CastSpell(who, card, WithoutPayingManaCost: true)` and one changed assertion in
`InterpreterDesignationTests.Casting_without_paying_the_mana_cost_refuses_naming_the_rule`.

## Not asked for, recorded instead

- **`MayCast` with a `Until` duration or `AsCopy`** (CR 601, 707) refuses by name. A permission that
  lasts ("you may cast it this turn") is a continuous effect the layer system would have to hold,
  and a copy of a card in a zone is not this card being cast; neither is a casting seam.
- **`RingTemptsYou` gives no emblem.** CR 701.54b's emblem named The Ring, with its four abilities,
  has no spelling in the vocabulary — `Emblem` is not an `Effect` record — so the arm does the
  Ring-bearer half of CR 701.54a and c only. The emblem is a V-series vocabulary request, not an
  engine one, and until it exists a card that tempts is not `Verified`.
- **`ChangeTarget` re-check.** The new target is checked against the filter of the `Target` slot it
  fills (`IQuery.Matches`), and an `AnyTarget` slot admits any object, because CR 115.4's four kinds
  are the query layer's arm (E0c) and the announcement already split the choice into `Targets` and
  `TargetPlayers`. When E0c lands, `FilterAt` in `EffectInterpreter.Retarget.cs` is where the
  narrower reading goes.
- **`EffectWalk.TargetsIn` now returns `ImmutableList<Selector>`**, not `ImmutableList<Target>`, so
  an `AnyTarget` occupies its slot in the CR 608.2b re-check. It is internal to
  `MtgEngine.Rules/Interpreter`, so nothing outside changed.
