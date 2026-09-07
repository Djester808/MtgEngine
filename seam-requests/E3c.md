# E3c — seam requests

Three seams the casting backlog needs. Everything else in the E3c brief landed without one
(printed additional costs, `PayManaCost`, `Repeatable`, `KeywordAbility.Costs`,
`ActivatedAbility.PayOnlyWith`); each request below names what is refused until it lands and
what the engine does in the meantime, so nothing is silently half-implemented.

---

## 1. `CastSpell.CostObjects` — which objects pay a cost that names some (CR 601.2c)

`MtgEngine.Rules/Commands/Command.cs`. `CostChoices` is `ImmutableList<string>` and is already
spoken for: `CastingSystem.OneProduction` reads a mana colour out of it (CR 608.2d). Object
choices need their own field, typed, because `ObjectId` is a `readonly record struct` and
squeezing ids through the colour list would make one channel mean two things.

```csharp
[RuleRef("601")]
public sealed record CastSpell(
    PlayerId Player,
    ObjectId Card,
    int FaceIndex = 0,
    ImmutableList<ObjectId>? Targets = null,
    ImmutableList<PlayerId>? TargetPlayers = null,
    ImmutableList<int>? Modes = null,
    int? X = null,
    ImmutableList<string>? CostChoices = null,
    KeywordKind? AlternativeCost = null,
    ImmutableList<KeywordKind>? OptionalCosts = null,
    /// <summary>
    /// The objects chosen to pay the costs that name some, CR 601.2c: the creature sacrificed as
    /// an additional cost, the cards exiled for escape, the permanents tapped for convoke. Read in
    /// the order the costs are written; a cost takes as many as it asks for.
    /// </summary>
    ImmutableList<ObjectId>? CostObjects = null) : Command(Player);
```

`ActivateAbility` wants the same field for the same reason — an activation cost of
"sacrifice a creature" has the identical choice.

**Until it lands:** `ObjectCosts.Choose` pays a cost whose objects are forced — when the number
of legal objects is exactly the number the cost asks for — and refuses anything with a real
choice naming CR 601.2c ("Which of the … pay the cost cannot be chosen yet"). No object is ever
picked for the player. Tests:
`A_choice_of_which_permanent_to_sacrifice_is_refused_rather_than_made_for_the_player`.

---

## 2. Restricted mana on the pool — `AddMana.SpendOnlyOn` (CR 106.6a, 106.6b)

`MtgEngine.Rules/State/PlayerState.cs`. `ManaPool` is six ints, so a restriction has nowhere to
live and the mana would come out of the pool unrestricted, which is a different card. Keeping the
restriction inside `ManaPool` means `ManaChanged(Player, Delta, Reason)` still carries it —
positive blocks add restricted mana, negative blocks spend it — so `Events/GameEvent.cs` needs
only its fold widened, not a new event.

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
    public ImmutableList<RestrictedMana> Restricted { get; init; } = [];

    public int Total => White + Blue + Black + Red + Green + Colorless + Restricted.Sum(r => r.Amount);
}

/// <summary>One block of restricted mana, CR 106.6a: its colour, how much, and what it may pay for.</summary>
[Cards.RuleRef("106.6a")]
public sealed record RestrictedMana(Cards.Color Color, int Amount, Cards.ObjectFilter SpendOnlyOn);
```

and in `Events/GameEvent.cs`, `ManaChanged.Apply` folds the blocks by
`(Color, SpendOnlyOn)`, dropping any block whose amount reaches zero:

```csharp
var next = new ManaPool(m.White + Delta.White, m.Blue + Delta.Blue, m.Black + Delta.Black, m.Red + Delta.Red, m.Green + Delta.Green, m.Colorless + Delta.Colorless)
{
    Restricted = FoldRestricted(m.Restricted, Delta.Restricted),
};
```

`ManaPoolEmptied` already resets to `ManaPool.Empty`, which is CR 106.6b's "empties with the
rest" for free.

**What E3c then does with it** (all in `Mana/` and `Casting/`, no further seam): `ManaFrom`
produces a block when `AddMana.SpendOnlyOn` is set; `ManaSymbols.Pay` takes the spell or ability
being paid for and spends a restricted block only when `IQuery.Matches` accepts it, unrestricted
mana first so a restricted block is never wasted on a cost ordinary mana could pay.

**Until it lands:** `AddMana.SpendOnlyOn` is not read, so a card that produces restricted mana
would produce ordinary mana — the one arm of this brief that is *not* refused, because the
production path predates it. Flagged here rather than left silent. The other half of CR 106.6b,
`ActivatedAbility.PayOnlyWith`, needs no state and is implemented (`ManaSymbols.Only`, refusal
"106.6b"); test `An_ability_that_names_the_mana_it_accepts_refuses_every_other_colour`.

---

## 3. `StackEntry.Gift` — the promise made as a spell is cast (CR 702.174b)

`MtgEngine.Rules/State/GameObject.cs`. The gift is promised on casting and the spell has to
remember whether it was, because its own text asks later ("if the gift was promised, …").

```csharp
public sealed record StackEntry(
    ObjectId? Source,
    int? AbilityIndex,
    ImmutableList<ObjectId> Targets,
    ImmutableList<PlayerId> TargetPlayers,
    ImmutableList<int> Modes,
    int? X,
    bool IsCopy,
    ImmutableDictionary<Color, int>? ManaSpent = null,
    Zone? CastFrom = null,
    ImmutableList<string>? OptionalCostsPaid = null,
    ImmutableDictionary<string, ImmutableList<ObjectId>>? Remembered = null,
    ImmutableDictionary<string, ImmutableList<PlayerId>>? RememberedPlayers = null,
    ImmutableList<ObjectId>? Triggering = null,
    /// <summary>
    /// The player the gift was promised to as this spell was cast, CR 702.174b; null when no gift
    /// was promised. The promise is part of the spell, so a copy of it carries the same promise.
    /// </summary>
    PlayerId? GiftPromisedTo = null);
```

A player has to be named because gift is "gift a [thing] to an opponent" — an
`OptionalCostsPaid` entry cannot say who, and the trigger that gives the gift needs them.

**Until it lands:** `CastPlan.Additional` refuses a cast that names `KeywordKind.Gift`,
"A gift promised as a spell is cast cannot be recorded yet", CR 702.174b. Test:
`A_gift_is_refused_until_the_promise_can_be_recorded_on_the_stack`.
