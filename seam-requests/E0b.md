# E0b — what the query layer still cannot read

The wave-5 brief's arms all landed. These are the five places where the state records *almost*
the answer, so the query returns a weaker answer than the rule wants, or refuses. Each one is a
field on a record E0b does not own. Nothing below blocks the query work that shipped.

## 1. `TurnHistory.Died` / `LeftBattlefield` lose the object, so morbid and revolt cannot see it

`ObjectMoved.Apply` folds the *departed* id into `Died` and `LeftBattlefield`, and
`ObjectCeasedToExist` has already taken that object out of `state.Objects`. So the query can say
"something died this turn" but not "a **creature** died this turn" (CR 700.4 / the morbid ability
word) and not "a permanent **you controlled** left the battlefield" (revolt). Both are implemented
as the unqualified count today, which is right for the common card and wrong for a board where
only a land was sacrificed.

`ObjectMoved` already carries `GameObject? Departed`. The fix is to keep it:

```csharp
public sealed record TurnHistory(
    ImmutableList<ObjectId> EnteredBattlefield,
    ImmutableList<GameObject> Died,
    ImmutableList<GameObject> LeftBattlefield,
    ...
```

with `ObjectMoved.Apply` adding `Departed ?? old`. The query then reads the departed object's
characteristics through `ICharacteristics` — which needs the object to be *in* the state to be
computed, so the honest version is the printed face of the departed object, CR 603.10's look-back.
If that is too big, the narrow form is two extra lists, `CreaturesDied` and
`LeftBattlefieldControlledBy: ImmutableDictionary<PlayerId, int>`.

## 2. Damage this turn is totalled per source and per receiver, never per pair

`DamageDealtBy`, `DamageReceivedBy` and `DamageReceivedByPlayer` are three separate totals. So
`DealtDamageThisTurn(To: …)` ("if it dealt damage to you this turn") and
`DamageDealtThisTurn(By: …, To: …)` cannot be answered at all, and both **refuse** — the arms are
written, they just have nothing to read. One field closes both:

```csharp
ImmutableDictionary<(ObjectId Source, PlayerId To), int> DamageDealtToPlayer,
```

folded in `DamageDealt.Apply` beside the two totals it already writes.

## 3. `StackEntry` has nowhere to record a promised gift (V3 item 9)

`GiftPromised` **answers false** rather than throwing, as the brief directed, so a card with gift
plays as if the gift were never promised instead of killing the game. E3c's casting work owes the
field; when it lands the arm is one line:

```csharp
// on StackEntry, after OptionalCostsPaid
PlayerId? GiftPromisedTo = null,   // CR 702.174b, set as the spell is cast
```

`Query.Holds`'s arm becomes `entry.GiftPromisedTo is { } to && (g.Who is null || Players(g.Who).Contains(to))`.

## 4. Nothing counts how many times an ability has resolved this turn

`FirstTimeThisTurn` (CR 603.4, "the first time this ability resolves each turn") is the one
condition left refusing. It needs a per-turn tally keyed by source **and ability index**, folded
where an ability finishes resolving and cleared by `TurnBegan`:

```csharp
ImmutableDictionary<(ObjectId Source, int AbilityIndex), int> Resolutions,
```

`TriggeredAbility.OncePerTurn` will want the same tally, so it is one field for two readers.

## 5. `Designations` lives in `MtgEngine.Rules/Query` and should probably live in `State`

The designation names (`suspected`, `ring-bearer`, `harnessed`, `prepared`, `goaded:<seat>`,
`city's blessing`) had no home — nothing sets them yet. E0b put
`MtgEngine.Rules/Query/Designations.cs` there because it owns that folder and the alternative was
ad-hoc strings on both sides of the seam. E9d's `Prepare`/`Unprepare` arms and whatever puts
suspected/goaded/ring-bearer on must use the same constants; if the coordinator would rather they
sat beside `GameObject.Designations` in `State/`, that is a file move and a namespace change, no
behaviour. **`Goaded` is a prefixed name, not a flag** (CR 701.15a names the goader), so the setter
must write `Designations.GoadedBy(player)`.

## Two readings the coordinator may want to check

- **`AttackedThisTurn` / `Raid` attribute every attacker to `state.Turn.ActivePlayer`.**
  `AttackersDeclared` carries the declaring player but `TurnHistory.AttackedWith` keeps only the
  ids. Only the active player declares attackers (CR 508.1), so this is right for every ordinary
  game and wrong only if a future effect lets a non-active player declare attackers. Adding
  `AttackedWith: ImmutableDictionary<PlayerId, ImmutableList<ObjectId>>` would make it exact.
- **`CastThisTurn` matches the spell object, not the permanent it became.** CR 400.7 makes the
  resolved permanent a new object with a new id, and nothing maps one to the other, so
  "a creature you cast this turn" is only true while it is still on the stack. A card that needs
  the permanent should reach it through the enters-the-battlefield trigger instead.
