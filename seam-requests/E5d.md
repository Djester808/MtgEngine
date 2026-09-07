# E5d — seam requests

E5c's vocabulary request landed: `KeywordAbility.OfPlayer` is on the wave-9 base, and CR 704.5m
now reads it. `StateBasedActions.EnchantsAPlayer` has the three cases the brief named — a named
`OfPlayer` asked of the player, neither half named admitting anyone, `Of` alone refusing every
player — and `AttachedLegally` asks `Of` of an object attachment and `OfPlayer` of a player one,
so an ability that carries both halves ("enchant creature or player") already works without a
further record.

Two things could not be done inside `Sba/`, and one thing the brief asked about needs nothing.

---

## 1. `PlayerFilter` matching has no seam, so there are now two private copies of it

`MtgEngine.Rules/Engine/Seams.cs`. `IQuery` answers `Matches` for an `ObjectFilter` and `Players`
for a `PlayerRef`, but nothing on it answers "does this player satisfy this `PlayerFilter`".
`TriggerMatching` needed that in wave 2 and wrote a private `PlayerMatches`
(`Triggers/TriggerMatching.cs:357`); 704.5m needs it now and has written a second private
`PlayerMatches` in `Sba/StateBasedActions.Permanents.cs`. A third caller is a matter of time —
`ChoosePlayer` and `TargetPlayer` both carry a `PlayerFilter` that nothing currently enforces.

The record that would close it:

```csharp
/// <summary>Whether a player answers a <see cref="PlayerFilter"/>, CR 601.2c with 702.5b.</summary>
bool PlayerMatches(GameState state, PlayerFilter filter, PlayerId candidate, EffectContext context);
```

`Query/Query.Selectors.cs` is the place; both private copies then delete.

### The part that is not just duplication: the two copies disagree about `Also`

`PlayerFilter.Also` is a `Condition`, and a `Condition` has no way to say "the player being
filtered" except through `You`, which reads `EffectContext.Controller`. So the point of view the
condition is evaluated from decides what `Also` means:

| | `TriggerMatching.PlayerMatches` | `StateBasedActions.PlayerMatches` (this wave) |
|---|---|---|
| context for `Also` | the ambient one — `Controller` is the ability's controller | `context with { Controller = candidate }` |
| what `Also` then means | a board condition, unrelated to the candidate | a restriction on the candidate |

The second is what makes `Also` a filter at all, and it is what `Query.Selectors.Extreme` already
does for `PlayerWithMostOf` (it evaluates the amount once per player, as that player). It is
asserted by `StateBasedActionTests.An_enchant_players_condition_is_asked_of_the_player_it_filters`,
which fails under the other reading. **Whoever writes the seam should pick the candidate's point
of view and retarget `TriggerMatching`** — but that is a trigger-side behaviour change, so it is
named here rather than made here.

## 2. A bare `Enchant` still admits any player, and only the linter can close that

Unchanged from E5c, and now the only ambiguous case left: an enchant ability with neither `Of`
nor `OfPlayer` is read as "enchant anything", so a translation that drops a filter produces an
Aura that is legally attached to a player. The engine cannot tell that apart from a card that
means it. The linter's pair rule — warn on an `Enchant` with both halves null — is what stops it,
and it is a vocabulary-side job.

## 3. The view needs nothing

The brief asked whether the view should show what an Aura may enchant. It should not, and it
already shows what it needs to: `Views.GameView` carries `AttachedToPlayer` beside `AttachedTo`
for every viewer (E5c), so a client can draw the attachment. What an Aura *may* enchant is a
legality question about a move that has not been made; it belongs to `ILegalActions` beside the
targets of a cast, not to the per-object projection, and no test here needs it. Nothing added.
