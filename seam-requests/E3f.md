# E3f — seam requests

No seam change is asked for. All three items landed inside `Casting/`, and the two seams they
needed (`GameObject.AttachedToPlayer`, `ObjectChanged.AttachedToPlayer`) were already on the
wave-8 tip. What follows is the shape E9g asked for, and the two narrowings a later wave may want
to widen.

## 1. The refusal shape a may-cast question would need (asked for by the brief, E9f § 2)

`Refusal` is `(string Reason, string? Rule)` and it is **not changed**: it is in
`Engine/Seams.cs`, which is not mine, and nothing needs it yet. `Check(state, CastSpell)` already
answers E9g's question with what it has:

| board | what `Check` returns today |
|---|---|
| a cost naming objects, and more legal objects than it wants | `("Which of the permanents to sacrifice pay the cost was not chosen.", "601.2c")` |
| a cost naming objects, and exactly as many legal ones as it wants | **null** — CR 601.2c's payment is forced, so the may-cast can be announced with no objects at all |
| a cost naming objects, and too few legal ones | `("There are not enough permanents to sacrifice.", "601.2h")` |
| a cost naming no objects (pay life, sacrifice this, discard this) | **null** |

That is the whole of what the interpreter needs to stop refusing every card with an
`AdditionalCosts` entry: hand the `CastSpell` over, relay the refusal, cast when it is null. Both
rows are tests in `CastingTests` (`A_cost_that_names_objects_the_command_does_not_carry_is_refused_by_name`,
`A_cost_that_names_no_objects_is_accepted_with_none_on_the_command`), and the reason names the
cost, so a question can be worded from it.

**If a later wave wants the question to offer the objects rather than refuse**, the shape I would
return is a third member on `ICastingSystem`, not a wider `Refusal`:

```csharp
/// <summary>What each cost of this cast needs chosen, CR 601.2c, in the order the costs are written.</summary>
[RuleRef("601.2c")]
ImmutableList<CostChoice> Choices(GameState state, Command command) => [];

/// <summary>One cost's ask: the objects that could legally pay it, and how many of them it takes.</summary>
public sealed record CostChoice(string Cost, ImmutableList<ObjectId> Legal, int Count);
```

`Legal` and not an `ObjectFilter`: the filter alone is not enough to offer from — a sacrifice also
wants the battlefield, the caster's control, and (for a tap cost) untapped — and `ObjectCosts`
already computes exactly that list to decide whether the payment is forced. Returning the list is
therefore free, and it cannot drift from what the payment will accept. `Refusal` stays two
strings.

## 2. Two narrowings recorded rather than guessed

- **`MayPlayFromZone.TopOfLibraryOnly` grants nothing.** `CastPlan.Permission` skips a permission
  with that flag and the card is refused by CR 601.3 as if none existed. Nothing in `Casting/`
  can say which card is on top of a library, and a permission whose bound cannot be checked would
  otherwise be widened silently to the whole library. The primitive it needs is a
  "top card of that library" predicate on `IQuery` (or an ordered library on `GameState`); the
  first "play the top card of your library" card is the trigger.
- **An Aura cast at a player is not re-checked on resolution.** CR 608.2b re-checks targets, and
  `AuraAttachment` re-checks an object target (zone, and the enchant filter of CR 702.5b). A
  player is not an object and there is no player-filter to match one against, so a player target
  is taken as legal. A player who has left the game (CR 800.4a) would be the case that needs it.

## 3. What CR 704.5m still owes this (E5c, same wave)

`ResolveTop` now emits `ObjectChanged(..., AttachedToPlayer: player)` and the permanent carries
it. `StateBasedActions.Permanents.AttachedLegally` has to accept a player attachment as legal or
the Aura is buried the instant it enters — that is E5c's file this wave, and until it lands
`AuraResolutionTests.An_Aura_cast_at_a_player_enters_attached_to_that_player` asserts the event
and the field right after resolution rather than survival through a settle. The test says so in
its own comment.
