# E9d — seam requests

Three arms in `MtgEngine.Rules/Interpreter/EffectInterpreter.Backlog.cs` refuse today, naming the
record and the rule, because each needs an event or a seam member the interpreter does not hold.
Each refusal is asserted by `InterpreterBacklogTests.An_arm_that_needs_a_seam_refuses_naming_its_rule`,
so the arm turning real is one edit and one changed assertion.

## 1. An event that sets a designation (CR 702.180) — `Prepare` / `Unprepare`

`GameObject.Designations` exists as state and **nothing in the engine writes it**: there is no
event, so prepare, suspect, goad, monstrous and the ring-bearer are all dead records. One event
covers all of them.

```csharp
/// <summary>
/// A designation the rules give an object, CR 702.180 and 701.60: prepared, suspected, goaded,
/// monstrous, the ring-bearer. Not a counter and not an ability; <paramref name="Given"/> false
/// takes it away, and taking away one that was never there changes nothing.
/// </summary>
[RuleRef("702.180")]
public sealed record ObjectDesignated(ObjectId Id, string Designation, bool Given = true) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Id, out var obj))
            return state;
        var designations = Given ? obj.Designations.Add(Designation) : obj.Designations.Remove(Designation);
        return state with { Objects = state.Objects.SetItem(Id, obj with { Designations = designations }) };
    }
}
```

With it the arm is `Emit(run, new ObjectDesignated(id, "prepared", given))` over
`Objects(run, what)`, skipping an object that already has it so nothing triggers twice (CR 702.180).
A `PlayerDesignated` twin for `PlayerState.Designations` (the city's blessing, CR 702.131) has the
same shape and the same gap.

## 2. An event that rewrites a stack entry's targets (CR 115.7) — `ChangeTarget`

Changing a target changes a `StackEntry`, and no event carries one. `ObjectCreated` cannot be
reused: its `Apply` inserts into the zone again, so the spell would appear twice on the stack.

```csharp
/// <summary>
/// A spell or ability on the stack now points somewhere else, CR 115.7: at least one target
/// changed, and CR 114.6's "choose new targets" may leave them all as they were. Legality is
/// rechecked on resolution (CR 608.2b), so an illegal change is simply not made here.
/// </summary>
[RuleRef("115.7")]
public sealed record StackTargetsChanged(ObjectId Id, ImmutableList<ObjectId> Targets, ImmutableList<PlayerId> TargetPlayers) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(Id, out var obj) || obj.Stack is not { } entry)
            return state;
        return state with
        {
            Objects = state.Objects.SetItem(Id, obj with { Stack = entry with { Targets = Targets, TargetPlayers = TargetPlayers } }),
        };
    }
}
```

## 3. `ICastingSystem` on the interpreter's seam (CR 601) — `MayCast`

`MayCast` is twelve of the pilot's 200 faces ("you may cast it without paying its mana cost").
The interpreter is constructed with `IQuery`, `IZoneSystem`, `IReplacementSystem` and
`ICharacteristics`; casting from inside a resolution needs the casting system as a fifth
constructor argument, set the way `zones.Replacements` is — after construction, so the two
subsystems can be built in either order:

```csharp
// EffectInterpreter
public ICastingSystem? Casting { get; set; }
```

`Subsystems.Wave2()` sets it once both exist. Without it the arm refuses; with it, `MayCast`
becomes an offer of `ICastingSystem.Cast(state, what, controller, withoutPayingManaCost)` wrapped
in the usual yes/no question. The same seam is what `MayPlay` (CR 305.9) will want.

## Not asked for, recorded instead

- **`DistributeCounters` announcement (CR 601.2d).** The division belongs to casting, not to
  resolution. The arm uses a split recorded under `"distribute:<path>"` or the plain `"distribute"`
  key when the casting system records one, and otherwise asks a single `value` question over the
  legal divisions. It refuses rather than listing when a division has more than sixty-four
  readings; that one was announced on casting or not at all. E3c filling the key retires the
  question with no interpreter change.
- **`Meld` (CR 701.42a)** still throws for `GameObject.Components`, which is E9b's standing wave-4
  request and unchanged.
