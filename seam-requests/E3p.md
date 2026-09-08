# E3p — the flash permission, a cost paid for a rule, and one reading of CR 605.1a

Wave 27. Owns `MtgEngine.Rules/Casting/**`, `tests/MtgEngine.Rules.Tests/Casting*.cs`. Anchor
`Casting`: **141 green**, up from 134; seven new tests in
`tests/MtgEngine.Rules.Tests/CastingTests.Wave27.cs`. Engine changes are in a new partial
`MtgEngine.Rules/Casting/CastingSystem.Wave27.cs` and two lines of
`CastingSystem.Instructed.cs`/`CastingSystem.ManaAbilities.cs`. `dotnet format --verify-no-changes`
clean on `MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

## 1. Done — CR 702.8b: the timing check reads the permission E9u creates

`RequireCastingTiming` now consults `MayCastAsThoughItHadFlash` after the instant/flash-keyword arm
and before `RequireSorcerySpeed`. A player holding an `ActiveEffect` whose `Effect` is a
`MayCastAsThoughFlash` in `AppliesToPlayers` (or whose own `Who` resolves to them, for a static
grant that named nobody) may cast a spell the effect's `What` admits any time they hold priority. A
null `What` admits every spell. **Timing only**: nothing about the cost, the zone or the events
changes, so everything watching casts still sees an ordinary cast (V14 row 5). The check is
instance, not static, because it needs the query.

**One reading that was not obvious, and is tested.** The filter is asked about the card **in the
zone the card is in**, by handing `query.Matches` the filter `with { In = card.Zone.Zone }`. The
timing check runs before CR 601.2a moves anything, so there is no spell on the stack to match, and
`Query.ZoneOf` reads a filter that names no zone against the **battlefield** — the trap E9t hit on
`Query.ZoneOf` in wave 26. Without the rewrite, "you may cast creature spells as though they had
flash" would have admitted nothing at all and the whole item would have been dead code that passes
a test written against the same mistake. `A_flash_filter_is_asked_about_the_card_in_the_zone_the_card_is_in`
stands on the real `Query.Query`, not a fake, so it fails if that rewrite is removed.

## 2. Done in this folder — the entry point CR 118.6 and CR 508.1h both need

`CastingSystem` gained two public members (`CastingSystem.Wave27.cs`):

```csharp
public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
```

They build the `CostContext` — which is what needs the `IZoneSystem` and the `IQuery` only this
system is constructed with — and hand it to `CostPayment.Pay`, so a cost imposed by the rules or by
a resolving effect is paid by exactly the code a spell's costs are paid by. `PayForRule` throws
`RefusedException` naming the rule; `CanPayForRule` answers with the `Refusal` and changes nothing
(the trial payment's result is discarded), the same shape `Check` already has. Tested both ways
against an empty pool and a stocked one.

## 3. Ask (Engine/**, `MtgEngine.Rules/Engine/Seams.cs`) — put the two on `ICastingSystem`

Neither caller can reach a `CastingSystem`; both hold the seam. Add to `ICastingSystem`, as default
members so no other implementation breaks (the shape `FinishResolving` and `CostChoices` already
use):

```csharp
/// <summary>CR 118.6 with 601.2h: a cost the rules or a resolving effect impose, paid in full or not at all.</summary>
[RuleRef("118.6")]
Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null) =>
    throw new NotImplementedException("PayForRule is not built in this casting system.");

/// <summary>Whether that payment would go through, with the rule when it would not, changing nothing.</summary>
[RuleRef("118.6")]
Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null) =>
    new("A cost cannot be paid by this casting system.", "118.6");
```

`CastingSystem` already has both with those exact signatures, so the members bind with no change in
this folder. Nothing in `Engine/Game.cs` needs wiring for the interpreter — it already holds
`ICastingSystem` on `EffectInterpreter.Casting` (E9e's seam). **Combat does need one line**: E4i § 2
asks for either a `Zones` handle or this entry point, and this is the entry point, so
`CombatSystem` wants a settable `ICastingSystem? Casting { get; set; }` assigned in `Game.cs` beside
`interpreter.Casting`, and `TaxSymbols`' refusal for a non-mana attack cost becomes
`Casting?.PayForRule(...)` (CR 508.1h with 508.1j). Owner of that half: `Combat/**` with
`Engine/**`.

## 4. Ask (Interpreter/**, `EffectInterpreter.Structure.cs`) — the `Unless` arm, and why it is the defect P25 § 2 found

This is the CR 118.6 defect and **it is not in this folder**. The arm today is:

```csharp
case Unless unless:
{
    var who = FirstPlayer(run, unless.Who);
    var key = "unless:" + run.Path;
    if (run.Context.Chosen.TryGetValue(key, out var paid))
        return Yes(paid) ? run : Child(run, 0, unless.Do);
    return Ask(run, who, "unless", "Pay the cost to stop this?", YesNo, 1, 1, key);
}
```

`unless.Paid` is **never read**. Nothing is charged and nothing is checked: a "yes" is taken at its
word, so a player with an empty pool "pays" `{1}` and the unpaid half — the counter — never runs.
That is `948217ac` Mana Tithe and `86bf58f2` Flusterstorm both failing `Life was 19`. It is not that
the cast does not spend the pool it was paid from; `CastStep` spends correctly (the casting tests
have asserted an emptied pool since wave 1, and `A_cost_paid_for_a_rule_empties_the_pool_it_was_paid_from`
asserts it again from the other end). The whole of it is the missing payment.

What the arm needs, once § 3 lands (CR 118.6, "unless a player pays a cost… that player may pay it
if able"; CR 601.2h, partial payments are not allowed):

- **Before asking**, `Casting?.CanPayForRule(run.State, who, unless.Paid, run.Context.Source, run.Context.X)`.
  A non-null refusal means the cost is unpayable, so the player is **not asked at all** and the arm
  goes straight to `Child(run, 0, unless.Do)` — the same shape E9u gave the edict for a player who
  controls nothing the choice admits.
- **On a "yes"**, `PayForRule` on the same arguments, and the walk continues on the outcome's state
  with its events folded in. A payment that refuses between the offer and the answer (the state
  moved while the question stood) is the "did not pay" branch, not a throw.
- **On a "no"**, unchanged.

`Unless.Paid` is a `Cost`, which is exactly what `PayForRule` takes, so no conversion is wanted.
The mirror in `EffectInterpreter.Backlog.cs` (CR 118.12a, the `MayPay` arm) should be read for the
same defect in the same sitting — the doc comment there calls itself the mirror of this arm.

## 5. Done — CR 605.1a is one reading (E5k § 4, ask 2)

`CastingSystem.IsManaAbility` calls `MtgEngine.Rules.Query.ManaAbilities.Is` and its own
`CouldAddMana`/three-criterion copy is deleted. The announcement's targets are still checked beside
it — a command carrying targets is not this ability resolving without the stack whatever its text
says — so no existing behaviour moved. The fourth criterion now bites:
`An_ability_that_adds_mana_and_draws_a_card_is_not_a_mana_ability` shows an ability that adds `{G}`
and draws using the stack, where the old copy resolved it on the spot. E5k's ask 1 (the runner's
copy, `ScenarioRunner.Mana.cs`) is **still open** — it is the R-series', and until it lands a
generated scenario can still count a number the casting system will not produce.

## Not done in this box

- **No verify run.** The corpus lives under `MtgEngine.Api/bin` in the main checkout and this
  worktree cannot reach it, so `948217ac`, `86bf58f2` and `7536eb66` are **unverified through the
  pipeline**; the seven anchor tests are the evidence for what is here. The two counterspells stay
  failing until § 3 and § 4 land together — they are one change in two folders, and neither half
  moves the card on its own.
- **Nothing outside my own files is committed**; `cards/` is untouched.
