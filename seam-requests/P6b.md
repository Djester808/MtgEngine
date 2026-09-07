# P6b — seam requests

## 1. `AnyTarget`: the vocabulary has no spelling for "any target"

**Not mine to add** (`MtgEngine.Cards/Selectors.cs` is V3's). Filed because it is the only
remaining first difference in the pilot that canonicalisation is forbidden to close.

CR 115.4 gives "any target" one meaning: any creature, player, planeswalker or battle. The
vocabulary makes a reading spell that out twice, in two records that do not know about each
other — `DealDamage(To: Target(ObjectFilter), ToPlayer: TargetPlayer(PlayerFilter))` — so two
models produce these two readings of the same three words:

```
abilities[0].do.to.filter.anyTypes: ["Battle","Creature","Planeswalker"] vs absent
```

Both are right. Neither is canonical, because neither says "any target": one names the three
permanent types and leaves the player half to `ToPlayer`, the other leaves the filter open and
lets `ToPlayer` carry the whole meaning. Canonicalising either into the other would be
inventing a reading, so `CanonicalTests.Any_target_is_left_as_written_because_the_vocabulary_has_no_spelling_for_it`
and `The_second_pass_canonicalises_nothing_semantic_away_either` both assert the card stays
disputed. That is honest and it costs every "any target" card its status.

The record that closes it, exactly:

```csharp
/// <summary>"Any target", CR 115.4: any creature, player, planeswalker or battle.</summary>
[RuleRef("115.4")]
public sealed record AnyTarget(Amount? Count = null, bool UpTo = false, string? Key = null) : Selector;
```

With it:

- the translator's reference gets one line — "any target" is `{"kind":"AnyTarget"}`, never a
  filter naming the three types plus a `toPlayer`;
- `Canonical` gains a collapse rule (mine, on request): `DealDamage` whose `To` is a `Target`
  over exactly `AnyTypes: Creature | Planeswalker | Battle` with nothing else set **and** whose
  `ToPlayer` is `TargetPlayer(PlayerFilter.Any)` becomes `To: AnyTarget()`, with a negative test
  that damage to only creatures, or to only a player, is left alone;
- the interpreter (E9c) needs one arm; the query layer already resolves each half.

`Count`/`UpTo`/`Key` are on it because "any target" appears with "up to one target" and with
divided damage, and because `Target` carries them. `Count` follows the rule P6b just landed:
absent means one, so a written `Fixed(1)` is stripped by reflection the day the record exists —
no change here needed for that.

Until it lands, "any target" cards stay `Disputed`, which is the honest status.

## 2. Not requested

Everything else in P6b's brief was doable inside `Agree/**` and `VocabularyReference.cs`.
`ReplacementSystem.cs` was read only, to take `SelfOnly`'s meaning from the engine rather than
from the flag's name.
