# E9g — seam requests

Wave 8's two vocabulary landings closed most of E9f. `MayPlayFromZone.WithoutPayingManaCost`
exists, so the free lasting permission is created with the flag instead of refused (CR 118.9), and
`ICastingSystem.Check` now decides whether a card with an additional cost can be announced instead
of the interpreter refusing every one of them from the printed face (CR 601.2b). Three things are
left; none of them can be done inside `MtgEngine.Rules/Interpreter`.

## 1. The interpreter carries a table of which costs name objects (CR 601.2c)

`CostQuestion` turns a refusal that names CR 601.2c — the casting system saying nobody chose the
objects — into a second question of kind `"cast-cost"`, and casts again with the answer as
`CastSpell.CostObjects`. To offer options it has to know **what the cost is looking for**, and
`Refusal(Reason, Rule)` cannot say: a string and a rule number name no filter, no zone and no
count. So the arm reads the printed face's `AdditionalCosts` and maps the object-naming shapes
itself:

```csharp
private static CostObjectsWanted? Wants(Cost cost) => cost switch
{
    SacrificeCost sacrifice => new(sacrifice.What, sacrifice.Count, Zone.Battlefield, true, false),
    ReturnToHandCost hand => new(hand.What, hand.Count, Zone.Battlefield, true, false),
    TapOther tap => new(tap.What, tap.Count, Zone.Battlefield, true, true),
    DiscardCost { Random: false } discard => new(discard.What, discard.Count, Zone.Hand, false, false),
    ExileFromZoneCost exile => new(exile.What, exile.Count, exile.From, exile.From == Zone.Battlefield, false),
    _ => null,
};
```

That is a **second copy of `Casting/ObjectCosts.cs`'s knowledge** — the same five shapes, the same
zones, the same by-control-or-by-ownership split — and E9f said plainly that this is
`CostPayment`'s knowledge and not the interpreter's. It is here only because the seam has no
channel for it. The two copies will drift the first time a cost shape gains a zone or a filter on
one side and not the other, and the failure is silent: the question offers the wrong objects and
the casting system then refuses the answer.

**The shape asked for** is one member on `ICastingSystem`, so the one place that knows what a cost
wants is the one place that says it:

```csharp
/// <summary>
/// CR 601.2c: the objects this command still has to name, in the order the costs are written —
/// the filter each cost looks through, where it looks, and how many it takes. Empty when the
/// command needs nothing more. The interpreter asks the player for these; the casting system is
/// the only thing that knows what a cost consumes.
/// </summary>
[RuleRef("601.2c")]
ImmutableList<CostChoice> Wants(GameState state, Command command) => [];

public sealed record CostChoice(ImmutableList<ObjectId> Legal, int Count, string Prompt);
```

`Legal` rather than a filter keeps the query, the zone and the controller rule on the casting side;
the interpreter turns each entry into one question and appends the answers to `CostObjects`. With
it, `Wants`, `Where` and `CostObjectsWanted` all delete from the interpreter.

## 2. Two object-naming costs on one face cannot be asked for (CR 601.2c)

`CostObjects` is a flat list read in the order the costs are written, and one question returns one
flat list of options, so a face carrying two costs that each name objects ("sacrifice a creature
and discard a card") cannot be answered unambiguously — nothing in the answer says which cost each
object went to. The arm refuses by name when `Wants` finds anything but exactly one:

```csharp
if (wants.Count != 1)
    throw new RefusedException(
        "Which objects that spell's additional costs want cannot be asked for; asked for in seam-requests/E9g.md",
        "601.2c");
```

§1's `ImmutableList<CostChoice>` fixes this too — one question per entry, asked in order — but it
needs a mark to say which entry is being answered, because both questions come from the same node
and the same effect path. `Ask`'s `storeAs` is the existing channel for that; a `"cast-cost:<n>"`
key on the run's marks would do it. No pilot face needs two of them yet.

## 3. `MayCast(AsCopy: true)` still refuses (CR 707.12)

The rule number was wrong before — E9f named CR 706.2, which is about copying a spell on the stack.
The rule is **CR 707.12**: "the copy is created in the same zone the object is in and then cast",
following steps 601.2a–h. So the copy must exist as an object in that zone *before* anything is
handed to `ICastingSystem.Cast`, and no seam makes one: `IZoneSystem.CreateToken(CopiableValues, …)`
makes a permanent on the battlefield, which CR 707.12 explicitly is not.

**The shape asked for** is a zone-system member that creates a copy of an existing object in the
zone that object is in and returns its id, so the interpreter can cast the copy exactly as it casts
the original:

```csharp
/// <summary>CR 707.12: a copy of an object, created in the zone that object is in.</summary>
[RuleRef("707.12")]
Outcome CopyInPlace(GameState state, ObjectId original, PlayerId owner, out ObjectId copy);
```

CR 704.5e then wants the copy to cease to exist if it is anywhere but the stack or the battlefield,
which is a state-based action and the rules system's, not the interpreter's. Until both land the
arm refuses naming CR 707.12, which is honest: the original is not cast in the copy's place.

## Not asked for, recorded instead

- **`MayCast(AlternativeCost: …)`** still refuses naming CR 118.9, unchanged from E9f:
  `CastSpell.AlternativeCost` is a `KeywordKind?` and `MayCast.AlternativeCost` is a `Cost` the
  effect states in its own words. A `Cost?` beside the flag on `CastSpell` is the channel if the
  corpus needs it; no pilot face does.
- **`Where` resolves `You` against the run's context, not against `may.Who`.** For every spelling
  seen so far those are the same player, because `MayCast.Who` is `You` or null. A card that lets an
  *opponent* cast something and pay an additional cost would offer that opponent's objects from the
  resolving player's point of view. §1's `CostChoice` removes the question by taking the command,
  which already carries the caster.
- **`CastPlan.For` reading `MayPlayFromZone`** was E3f's this wave; the interpreter depends on the
  seam records and not on its new behaviour, so nothing here changed for it. The new
  `WithoutPayingManaCost` half of the record is the flag that check hands to the `CastSpell` the
  player announces later — it has no reader in the interpreter and is not one of ours.
