# E6b — one dead constant left in a file this agent does not own

`Query/TurnFacts.cs` is deleted and `Query/Query.History.cs` reads the three `TurnHistory` fields
directly, as the wave-6 decision table says ("`TurnFacts` in the query is now redundant — delete it
in wave 7"). That left one refusal message with no thrower:

```csharp
// MtgEngine.Rules/Query/Query.Predicates.cs, lines 89-90 — delete both lines
private const string NoDamagePairs =
    "the turn's history totals damage per source and per receiver, not per pair, so \"dealt damage to that player this turn\" has nothing to read; see seam-requests/E0c.md";
```

Every shape of `DealtDamageThisTurn` and `DamageDealtThisTurn` is answered from
`TurnHistory.DamageDealtToPlayer` now, so nothing can throw it again. `IDE0051` is an error in this
build, so the deletion of the guard **fails the build unless the constant is still referenced**;
`Query.History.cs` therefore names it in the doc comment of `DealtDamage`, which counts as a
reference and says why it is dead. Delete the constant and that sentence together — whoever owns
`Query.Predicates.cs` next, or the coordinator in the seam commit. Nothing else refers to it.

No other seam is needed: `TriggeredAbility.OncePerTurn`, `ObjectMoved.Departed` and
`TurnHistory.AbilitiesResolvedThisTurn` were all already on the seam.

## Noted, not requested: the one firing the once-per-turn rule cannot see

`TriggerSystem.OncePerTurn` reads "has this ability already triggered this turn" as *its resolution
is in `AbilitiesResolvedThisTurn`* or *a stack object of that source and index is waiting*, plus a
per-batch guard so two events in one batch fire it once. That is exact except for a trigger that
fired and then left the stack without resolving — countered, or its source's ability removed — which
fires a second time. Closing it needs the history to record firings as well as resolutions
(`TriggersFiredThisTurn`, folded by `TriggerFired` exactly as `StackObjectResolved` folds the
tally), on a record this agent does not own. Not requested here because no card in the slice cares
and the extra field is state that has to be reset, replayed and serialised; it is written down so
the gap is a decision rather than an oversight.
