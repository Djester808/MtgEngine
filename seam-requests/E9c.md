# E9c — what the interpreter offers, and the one seam it needs

## 1. `IInterpreter.Run` should carry the ability index — CR 101.4, 614.1a

A question raised inside `Run` has no stack object, so `Resume` cannot find the effect tree by
walking the stack. E9c now records the source and the ability index in the `ChoiceContext` and
finds the tree again in the source's own abilities (`ReplacementAbility.With`, or a triggered
ability's body), falling back to the one `DelayedTriggerState` that source created, and refusing
with `RefusedException` and CR 101.4 where neither is found.

The index has to come from the caller: only the replacement system knows which of the source's
abilities the `With` it is running belongs to. `EffectInterpreter` therefore grew a four-argument
overload of its own, which is what `InterpreterProposalTests` uses:

```csharp
Outcome Run(GameState state, Effect effect, EffectContext context, int? abilityIndex);
```

The seam change wanted is the same parameter on the interface, so `ReplacementSystem.Instead` can
pass it:

```csharp
/// <summary>Run an effect outside the stack: a replacement's With, a static ability's setup, a manual scenario step.</summary>
Outcome Run(GameState state, Effect effect, EffectContext context, int? abilityIndex = null);
```

`Forwarding.cs` forwards it; `ReplacementSystem.Applying.cs` passes `candidate.AbilityIndex` (the
candidate already knows which ability it came from — if it does not, that field is the other half
of this request). Until it lands, a "may" inside a replacement's `With` raises a question that is
found again only when the caller happens to be a test: through the interface, the index is null,
the delayed-trigger fallback misses, and the answer is refused rather than resolved. Nothing
crashes and no table is silently wrong; the question simply stays open.

## 2. Two events nothing yet offers — not asked for, recorded

- `PutPlayerCounters` emits `PlayerCountersChanged` and is *not* proposed, because no
  `ReplacedEvent` in the vocabulary matches that event (`WouldPutCounters` reads `CountersChanged`,
  which is objects only). A "would get a poison counter" replacement needs either a new record or
  `WouldPutCounters` widened; whichever it is, it is a vocabulary change (V3), not an interpreter
  one.
- `AddMana` is proposed nowhere either, though `WouldAddMana` matches `ManaChanged`. The mana
  subsystem and the interpreter both emit it; proposing it from one side only would replace half
  the mana in a game, so it wants doing in both places at once, by whoever holds `Rules/Mana`.

## 3. Zone changes are E1b's, deliberately

`Move`, `Leave`'s relocation, `Draw`, `Mill`, `Search`, `CreateToken` and the scry/surveil sorts
all go through `IZoneSystem`, which E1b is teaching to propose its own `ObjectMoved`/`ObjectDrawn`
this wave (EINT2 § 1). The interpreter proposes the *named action* beside the move —
`ObjectLeftBy` for countered/destroyed/sacrificed/discarded/exiled — and, when a replacement drops
it, skips the zone change. It does not propose the move as well; if E1b's handle is not wired,
`WouldEnterBattlefield` and the rest are still dead, and that is one change in one place.
