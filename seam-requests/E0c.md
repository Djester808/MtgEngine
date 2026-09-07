# E0c — the three turn facts the query still cannot read

The `AnyTarget` arm (V4 § 1) needed no seam and has landed: `Query.Resolve` answers the object half,
`Query.AnyTargetFilter` and the two `Query.MatchesAnyTarget` overloads are the CR 608.2b re-check,
and `QueryTargetTests` covers a creature, a player and the refused land.

What follows is three fields on records this agent does not own. **The query already reads all
three** through `MtgEngine.Rules/Query/TurnFacts.cs`, which binds each one by name and by exact
type against the `TurnHistory` the build actually has: the arms answer the weaker reading (or
refuse) today and answer the rule itself the moment a field lands, with no edit in `Query/`. The
tests are written against the same three guards — `TurnFacts.KeepsDepartedObjects`,
`KeepsDamagePairs`, `CountsResolutions` — so `QueryBacklogTests` passes before and after and flips
to asserting the rule when it does. Delete `TurnFacts` once all three have landed; nothing else
depends on it.

## 1. `TurnHistory` keeps the departed object (morbid, revolt)

`ObjectMoved.Apply` folds the departed **id**, and `ObjectCeasedToExist` has already removed that
object, so morbid is "something died" and revolt is "something left". Wrong on any board where a
land was sacrificed. `ObjectMoved` already carries `GameObject? Departed`, so the fold is one line.

```csharp
// MtgEngine.Rules/State/GameState.cs — TurnHistory
public sealed record TurnHistory(
    ImmutableList<ObjectId> EnteredBattlefield,
    ImmutableList<GameObject> Died,             // was ImmutableList<ObjectId>
    ImmutableList<GameObject> LeftBattlefield,  // was ImmutableList<ObjectId>
    ...
```

```csharp
// MtgEngine.Rules/Events/GameEvent.cs — ObjectMoved.Apply, replacing the two Add(From) lines
if (fromZone == Zone.Battlefield && (Departed ?? old) is { } departed)
{
    h = h with { LeftBattlefield = h.LeftBattlefield.Add(departed) };
    if (To.Zone.Zone == Zone.Graveyard)
        h = h with { Died = h.Died.Add(departed) };
}
```

**Every reader, so none is missed** (the type change makes each a compile error, not a silent
behaviour change — that is why this shape is safe):

| File | Line | Today | After |
|---|---|---|---|
| `Rules/Triggers/TriggerMatching.cs` | 114, 126 | `state.History.LeftBattlefield.Contains(moved.From)` | `.Any(o => o.Id == moved.From)` |
| `Rules/Triggers/TriggerMatching.cs` | 116 | `state.History.Died.Contains(moved.From)` | `.Any(o => o.Id == moved.From)` |
| `Rules/Triggers/TriggerMatching.cs` | 349 | `state.History.LeftBattlefield.Contains(left.Object)` | `.Any(o => o.Id == left.Object)` |
| `Rules/Triggers/TriggerSystem.cs` | 59 | `state.History.LeftBattlefield.Contains(moved.From)` | `.Any(o => o.Id == moved.From)` |
| `tests/MtgEngine.Rules.Tests/SeamTests.cs` | 114–115 | `Assert.Equal(new[] { onField }, h.Died)` | `Assert.Equal([onField], h.Died.Select(o => o.Id))` |
| `Rules/Query/Query.History.cs` | 30, 47 | the `.Count > 0` fallbacks | delete the fallback, keep the `TurnFacts` branch |

The query reads the departed object's **printed face** (CR 603.10's look-back), because
`ICharacteristics` cannot compute characteristics for an object that is no longer in the state, and
its `InitialController` for revolt. Both readings are written down in `Query.History.cs`.

## 2. A per-pair damage map (`DealtDamageThisTurn(To:)`, `DamageDealtThisTurn(By:, To:)`)

Three totals, none of them a pair, so both questions refuse today. One field closes both:

```csharp
// MtgEngine.Rules/State/GameState.cs — TurnHistory, after DamageReceivedByPlayer
ImmutableDictionary<(ObjectId Source, PlayerId To), int> DamageDealtToPlayer,
```

with `TurnHistory.Empty` gaining
`ImmutableDictionary<(ObjectId, PlayerId), int>.Empty`, and the fold beside the two totals
`DamageDealt.Apply` already writes:

```csharp
// in the ToPlayer arm, beside DamageReceivedByPlayer
var pair = (Source, pid);
h = h with { DamageDealtToPlayer = h.DamageDealtToPlayer.SetItem(pair, h.DamageDealtToPlayer.GetValueOrDefault(pair) + Amount) };
```

Tuple element names are not part of the runtime type, so `TurnFacts` binds whatever names the field
is declared with. Damage to a **permanent** by a given source is the same shape and is not asked for
by any card in the pilot slice; add `DamageDealtToObject` the same way when one is.

## 3. A per-turn resolution tally (`FirstTimeThisTurn`, and `TriggeredAbility.OncePerTurn`)

```csharp
// MtgEngine.Rules/State/GameState.cs — TurnHistory
ImmutableDictionary<(ObjectId Source, int AbilityIndex), int> AbilitiesResolvedThisTurn,
```

**How the source and the index are known.** `StackObjectResolved(ObjectId StackObject)` fires while
the object is still on the stack (`ICastingSystem.FinishResolving` does the leaving, CR 608.2m/n),
so its own `StackEntry` names both: `entry.Source ?? obj.Id` is the ability's source (an activated or
triggered ability carries the permanent it came from; a spell is its own source) and
`entry.AbilityIndex ?? -1` is the index, `-1` standing for "the spell itself, not one of its
abilities". `Apply` is currently the identity:

```csharp
[RuleRef("608.2")]
public sealed record StackObjectResolved(ObjectId StackObject) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        if (!state.Objects.TryGetValue(StackObject, out var obj) || obj.Stack is not { } entry)
            return state;

        var key = (entry.Source ?? obj.Id, entry.AbilityIndex ?? -1);
        var tally = state.History.AbilitiesResolvedThisTurn;
        return state with
        {
            History = state.History with { AbilitiesResolvedThisTurn = tally.SetItem(key, tally.GetValueOrDefault(key) + 1) },
        };
    }
}
```

`TurnBegan` already resets the whole history, so the tally clears with it (CR 500.1).

The query counts a resolution that has **finished**, so the first check of the turn reads zero and
the condition holds. It keys on `(ctx.Source, index)` where the index comes from the topmost stack
object of that source; an intervening "if" checked *before* the ability is stacked (CR 603.4 checks
twice) cannot know the index, so there the arm asks whether any ability of that source has resolved
— exact for a source with one such ability, and the only reading available before the ability exists
as an object. If E6b would rather the trigger system passed the index down, the seam is one field on
`EffectContext` (`int? AbilityIndex`) and the arm reads it in preference; `OncePerTurn` wants the
same tally and the same key, which is why it is one field for two readers.
