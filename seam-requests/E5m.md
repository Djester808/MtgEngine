# E5m — wave 30: the query reads the plural zone, and the two V18 amounts

Owned: `MtgEngine.Rules/Query/**`, `tests/MtgEngine.Rules.Tests/Query*.cs`. Landed:
`Query/Query.Wave30.cs` (new partial), three edits in `Query/Query.cs`, one in
`Query/Query.History.cs`, three arms in `Query/Query.Amounts.cs`, and seven tests in
`tests/MtgEngine.Rules.Tests/QueryTests.Wave30.cs` (`QueryTests` anchor).

**E8g ask 1 is closed.** `Query.ZoneOf` read `filter.In` alone and defaulted to the battlefield, so
every reader handed an `ObjectFilter.InAnyOf` filter admitted nothing at all. Both doors now go
through `ZoneAdmits` (CR 400.1): `Matches` admits an object whose zone is `In` or one of `InAnyOf`,
`Candidates` enumerates every named zone (still ordered by id, so the order does not depend on which
zone an object is in), and the look-back `WasAsCreated` in `Query.History.cs` reads the same rider.
L047 forbids `In` beside `InAnyOf`, so the two spellings cannot disagree; the union is written
anyway, because a filter carrying both would otherwise silently lose the singular half.

**`ReplacementSystem.Admits`'s one-line rewrite is now redundant** (`Replacement/ReplacementSystem.Wave29.cs:124-134`,
the `what with { In = standing.Zone.Zone, InAnyOf = null }` rewrite). It is still correct — it hands
the query a single-zone filter, which reads exactly as the plural now does — so nothing breaks while
it stands. The E8-series deletes it, as E8g said; it is not mine.

`DistinctNameCount` (CR 201.2b with 201.2a) landed: the distinct names among the admitted objects,
counted once each, with a nameless object contributing no name — the one place it parts company with
`GreatestSameNameCount`, where a nameless object is still a group of one. A `Named` rider is **not**
refused here as it is there: "creatures named X with different names" answers 1 or 0 and is not a
contradiction.

## 1. Ask — `State/**` (E2-series, with the folders that lose life): the life lost this turn

`LifeLostThisTurn(PlayerFilter)` (CR 119.3, V18 row 2) has no number to read. `Query.Evaluate`
refuses it naming the missing field rather than answering zero, because zero is a legal answer to
"did an opponent lose life this turn" — answering it would play every such card as if nobody had,
with nothing in the log to say so.

**The field.** One per-player running total, beside `PlayerState.ObjectsDrawnThisTurn`, which is the
same shape and the same lifetime:

```csharp
/// <summary>Life this player lost this turn, CR 119.3; cleared by TurnBegan.</summary>
[Cards.RuleRef("119.3")]
public int LifeLostThisTurn { get; init; }
```

**What has to write it.** The one fold of `LifeChanged` (`Events/GameEvent.cs:226`) is the whole
write, and it is one line: `Delta < 0` adds `-Delta` to the total. That is loss and not net change —
a player who lost 3 and gained 3 lost 3 — so a positive delta adds nothing and never subtracts.
Every road that loses life already goes through that event: combat and non-combat damage (CR 119.3),
life paid as a cost (`Casting/CostPayment.cs:155` and `:415`, CR 119.4), and the interpreter's
lose-life verb (`Interpreter/EffectInterpreter.Verbs.cs:250`). Cleared where
`ObjectsDrawnThisTurn` is cleared, by `TurnBegan`.

**Why not `TurnHistory.DamageReceivedByPlayer`.** It is the damage half alone. Reading it would
answer "nobody lost life" after an edict, a payment or a plain lose-life effect, which is the silent
wrong answer this refusal exists to prevent. Two of the three roads above leave no damage at all.

The arm is written and reads the field the moment it exists: `Query.Wave30.cs`'s
`NoLifeLostThisTurn` becomes `Players(state, lost.Who, ctx).Sum(p => state.Player(p).LifeLostThisTurn)`,
one line, and the two refusal assertions in `QueryTests.Wave30.cs` become the totals.

## 2. Ask — `Casting/**` (E3-series, with `State/**`): the commander casts this game

`CommanderCastsThisGame(Selector?)` (CR 903.8, V18 row 4) has nothing anywhere in the engine keeping
the count — I grepped `MtgEngine.Rules/**` for it and for the tax: neither exists. Refused the same
way and for the same reason.

**Which folder.** The **casting system**, `MtgEngine.Rules/Casting/**`, at the cast from the command
zone — the announcement that already has to read the number to charge CR 903.8's additional {2} for
each previous such cast. One writer, at the one place that knows a cast is from the command zone;
nothing else in the engine has both facts.

**The shape.** Per game and never reset, so it cannot live on `TurnHistory`, which is cleared as each
turn begins (`GameState.History`). It is keyed by the commander and not only by the player, because
one player can have two commanders (a partner pair) with separate taxes, and the count follows the
card, not the zone — a commander that changed zones keeps it. On `GameState`, beside `Monarch`:

```csharp
/// <summary>Casts from the command zone this game, per commander, CR 903.8. Never reset.</summary>
[RuleRef("903.8")]
public ImmutableDictionary<ObjectId, int> CommanderCastsThisGame { get; init; }
    = ImmutableDictionary<ObjectId, int>.Empty;
```

A caveat the writer has to settle, not me: a commander that changes zones becomes a new object
(CR 400.7), so the key must be the identity the designation travels on and not the object id of the
moment, or the count resets every time the commander dies. `GameObject.IsCommander` marks it and
`Owner` names whose it is; if two commanders of one player must be told apart, the key is the
commander's own stable id.

The arm then resolves the record's `Which` (defaulting to this object, CR 903.8 with the V18
decision) and reads the entry. Not written here: guessing the key now would be a second spelling for
the casting system to disagree with.

## What is green, and what is not

- `MtgEngine.Rules` builds with 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean on
  `MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.
- The `MtgEngine.Rules.Tests` project runs whole in this box (the brief allows it): **1243 passed**,
  0 failed, of which the `QueryTests` anchor is **84**, up from 77.
- Each of the seven fails without its change, argued from the line each replaces rather than measured
  by a revert (the box had no room for two more builds): the old `ZoneOf` returns the battlefield for
  a filter whose only rider is `InAnyOf`, so `A_filter_naming_two_zones...` reads 0 where it asserts
  2 and `Matches` false where it asserts true; the three amounts fell to `Evaluate`'s `_` arm, whose
  `Unanswerable` carries no rule, so each refusal test's `Assert.Equal("119.3"/"903.8", Rule)` fails
  and `DistinctNameCount` throws where it asserts a number.
- No corpus loading, no full solution build, no card name in engine source or in a test.
