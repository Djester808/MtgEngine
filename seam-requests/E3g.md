# E3g — seam requests

Three of the four items landed inside `Casting/` and `Mana/` with no seam change. One needs a
state change and is written up here; the fourth is an agreement with E9h about which channel
carries a spend.

## 1. `StackEntry` cannot say which mana paid (CR 106.6a) — a `State/` change

`StackEntry.ManaSpent` is `ImmutableDictionary<Color, int>?`: it records **how much** of each
colour paid, which is all CR 202.3a ("mana spent to cast this spell") needs. It cannot record
**which** mana paid — the permanent whose ability produced it, and the key the card gave it — and
that is what CR 106.6a's "when that mana is spent" trigger reads.

The brief allowed either, so the spend is reported on the delta and **not** on the stack entry:

- `ManaChanged.Delta.Provenance` now carries the spent blocks **negative** (`ManaProvenance` with
  a negative `Amount`), which is the same shape `ManaPool.FoldProvenance` already expects of a
  delta. One event both empties the pool and says which mana left it.
- Every payment that goes through `CostPayment.PaySymbols(state, CostContext, …)` emits it — a
  spell's cost, an ability's cost, an additional mana cost — so the channel is uniform.

**The change I would ask for** when a card needs the spend to survive the payment (a trigger that
fires on resolution rather than on the event, or a query that reads the spell later):

```csharp
// State/GameObject.cs, on StackEntry, after ManaSpent:
/// <summary>Which mana paid, CR 106.6a: the blocks the spend took, positive. Beside
/// <see cref="ManaSpent"/>, which is the same spend counted by colour for CR 202.3a.</summary>
ImmutableList<Mana.ManaProvenance>? ManaProvenanceSpent = null,
```

It is a pure addition with a default, so no existing construction changes; `Casting/` would fill
it from the same list it already puts on the delta. **Nothing reads it yet**, which is why it is a
request and not a landing — the delta answers E9h today.

## 2. The channel E9h's delayed `ManaSpent` trigger reads — agreed here

E9h: read the spend off **`ManaChanged` with a negative `Provenance` block**, not off the stack
entry. Concretely, for "when that mana is spent":

- The production put a block in the pool with the source and the card's `AddMana.Key`
  (`ManaSources.Added`, CR 106.6).
- The spend emits `ManaChanged(player, delta, reason)` where `delta.Provenance` holds that block
  with a negative `Amount`. Match on `Key` (the card gave one) or on `Source` (the permanent),
  and the amount's magnitude is how much of it was spent.
- `reason` is `"cast"` for a spell's mana cost and `"cost"` for an ability's or an additional
  cost, if the trigger needs to tell them apart.

If E9h needs it on the stack entry instead, say so in `E9h.md` and item 1 lands next wave.

## 3. The interpreter's own `AddMana` arm should produce provenance the same way — no seam change

`MtgEngine.Rules/Interpreter` resolves `AddMana` for a non-mana ability (a spell that adds mana,
a triggered ability's mana). That arm is not mine. It should build its delta with

```csharp
ManaSources.Added(total, add.SpendOnlyOn, source, add.Key)   // MtgEngine.Rules/Mana/ManaSources.cs
```

which is the one place that turns produced mana into "a restricted block when the card said what
it may be spent on, plus one provenance block per colour" (CR 106.6, 106.6a). It is `public
static`, takes no state and no seam, so this is a one-line change on that side and no interface
moves. Mana produced without it is spendable but remembers nothing, and a `PayOnlyFrom`
restriction will refuse it — which is the honest failure, not a silent one.

## 4. `ICastingSystem.CostChoices` is implemented — E9g/E9h may delete its copy of the table

`CostChoices(state, command)` answers for both `CastSpell` (the plan's non-mana costs) and
`ActivateAbility` (the ability's own costs), from `Casting/ObjectCosts.Choice` — the same table
`ObjectCosts.Pay` switches on, read the other way round. The five object-naming shapes E9g listed
(`SacrificeCost`, `DiscardCost { Random: false }`, `TapOther`, `ExileFromZoneCost`,
`ReturnToHandCost`) are all answered, with the zone, the control-or-ownership split, the filter
and the untapped/exclude-source narrowings applied — so `CostObjectsWanted` in the interpreter can
go.

- `CostChoice.Cost` is the phrase a refusal uses ("permanents to sacrifice", "cards to discard",
  "untapped permanents", "cards to exile", "permanents to return"), so a question can be worded
  from either half and they cannot drift.
- `CostChoice.Legal` is ordered by object id, the same order the forced payment takes.
- A command that cannot be announced at all (no such object, not yours to cast) returns an **empty
  list**, not a throw: `Check` is what says why. A cost whose count evaluates to zero asks nothing.
- `RemoveCountersCost { From: not null }` is **not** answered yet: it resolves its selector through
  `IQuery.Resolve`, which is a different question shape (a selector, not a zone-and-filter walk).
  Say so if a card needs it and it lands next wave.
