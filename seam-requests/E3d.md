# E3d — seam requests

One seam: restricted mana on the pool. Everything else in the E3d brief landed without one —
`CostObjects` on both commands, `PlayLand.FaceIndex` through `IZoneSystem.Move(…, faceIndex)`, and
the gift promise on `StackEntry.GiftPromisedTo` are all read by the casting system now.

---

## Restricted mana on the pool — `AddMana.SpendOnlyOn` (CR 106.6a, 106.6b)

`MtgEngine.Rules/State/PlayerState.cs`. `ManaPool` is six ints, so a restriction has nowhere to
live and mana produced by "add {G}. Spend this mana only on …" comes out unrestricted, which is a
different card. This is the request E3c filed (`seam-requests/E3c.md` § 2) and the wave-5 decision
deferred to this wave; the casting half is built and waiting for the field.

### The state (one field plus one record)

```csharp
/// <summary>Mana a player has, CR 106.4, with the restricted mana of CR 106.6a beside it.</summary>
[Cards.RuleRef("106.4")]
public sealed record ManaPool(int White, int Blue, int Black, int Red, int Green, int Colorless)
{
    public static ManaPool Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Mana that may be spent only on what its filter matches, CR 106.6a. It is otherwise
    /// ordinary mana and empties with the rest at the end of each step and phase, CR 106.6b.
    /// </summary>
    public ImmutableList<Mana.RestrictedBlock> Restricted { get; init; } = [];

    public int Total => White + Blue + Black + Red + Green + Colorless + Restricted.Sum(r => r.Amount);
}
```

`RestrictedBlock` already exists, in `MtgEngine.Rules/Mana/RestrictedPool.cs`, because the casting
side needed it to be able to read one:

```csharp
public sealed record RestrictedBlock(Color Color, int Amount, ObjectFilter SpendOnlyOn);
```

### The one line in casting

`Mana/RestrictedPool.cs` reads the blocks through `IRestrictedMana`, whose only implementation
today answers "none":

```csharp
public sealed class NoRestrictedMana : IRestrictedMana
{
    public ImmutableList<RestrictedBlock> In(ManaPool pool) => [];   // becomes: => pool.Restricted;
}
```

`RestrictedPool.Spendable` already offers a payment the ordinary mana plus every restricted block
whose filter matches the object being paid for, and `CostPayment.PaySymbols(state, context, …)`
already routes every cast and every activation through it. So the field plus that one line make
restricted mana unspendable on the wrong cost immediately.

### The fold, and what is still owed after the field lands

`Events/GameEvent.cs`: `ManaChanged(Player, Delta, Reason)` keeps carrying the restriction —
a positive delta adds a block, a negative one spends it — so no new event is needed, only a wider
fold:

```csharp
var next = new ManaPool(m.White + Delta.White, …, m.Colorless + Delta.Colorless)
{
    Restricted = FoldRestricted(m.Restricted, Delta.Restricted),
};
```

`FoldRestricted` folds by `(Color, SpendOnlyOn)` and drops a block whose amount reaches zero.
`ManaPoolEmptied` already resets to `ManaPool.Empty`, which is CR 106.6b's "empties with the rest"
for free.

Two pieces of casting follow the field and are E3-side, not seam work: `CastingSystem.ManaFrom`
emits a block when `AddMana.SpendOnlyOn` is set, and the spend has to come out of the block it
used rather than out of the ordinary counts, which means `ManaSymbols.Pay` returning which block
paid. Both are inside `Mana/` and `Casting/`.

**Until it lands:** a card whose `AddMana.SpendOnlyOn` is set produces ordinary mana. It is the one
gap in this area that refuses nothing, so it is named in a test rather than left silent:
`CastingChoiceTests.Mana_produced_with_a_spend_restriction_is_ordinary_mana_until_the_pool_can_hold_it`.
