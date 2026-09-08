# E4j — the attack tax pays through the casting system

Wave 29. Owns `MtgEngine.Rules/Combat/**`, `MtgEngine.Rules/Engine/**`,
`tests/MtgEngine.Rules.Tests/Combat*.cs`, `tests/MtgEngine.Rules.Tests/Engine*.cs`. Anchor
`Combat`: **90 green**, up from 84; six new tests in `tests/MtgEngine.Rules.Tests/CombatTests.Wave29.cs`.
The whole Rules suite is **1184 green** (up from 1178), `dotnet format --verify-no-changes` clean on
`MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

## 1. Done — E3p § 3 and E3q § 3's ask, both halves

- `CombatSystem.Casting` is a settable `ICastingSystem?` (`Combat/CombatSystem.Wave29.cs`), the same
  shape and for the same reason as `Replacements`: the casting system is built with the interpreter,
  which moves and taps permanents that are in combat, so the two are composed after both exist.
- `Engine/Game.cs` assigns it in **both** compositions — `Subsystems.Wave1()` and `Subsystems.Wave2()`
  — beside the interpreter's `interpreter.Casting = casting`.
- `TaxSymbols` (`Combat/CombatSystem.Wave26.cs`) now returns `ImmutableList<ManaSymbol>?`: a mana cost
  is symbols as before, and **anything else returns null after asking
  `Casting.CanPayForRule(lockedIn, player, cost)`** — the CR 508.1h moment, where the total is
  determined and locked in and nothing has been paid. A non-null `Refusal` is re-thrown as a
  `RefusedException` naming **CR 508.1h**, so an unpayable tax refuses the whole declaration
  (CR 508.1's preamble with CR 733) rather than crashing it or dropping the attacker that owed it.
- `PayAttackTax` splits the total into the mana half and the rest: the mana is spent out of the pool
  by `CostPayment.PaySymbols` as before, and each other cost is paid by
  `Casting.PayForRule` at **CR 508.1j**, folded onto the same outcome. Order inside CR 508.1j is
  free ("they pay all costs in any order"), so the mana half going first is not observable.
- A **null** `Casting` keeps the older refusal verbatim (CR 508.1h), so nothing that composes a
  combat system without one changed behaviour.

Tested: a `PayLife` tax is paid through the seam and the life event precedes `AttackersDeclared`
(CR 508.1j then 508.1k); an unpayable one refuses the whole declaration with the untaxed attacker
included and **`PayForRule` never called**, which is the mutation that proves `CanPayForRule` runs
first; a declaration owing a mana tax *and* an unpayable life tax spends no mana; the payability
question is asked against the state the declaration's `AttackersDeclared` was folded into (CR 508.1h);
a null handle still refuses; and both composed `Subsystems` hand combat the casting system they built.

## 2. Asked — what the legal-actions view must read (E14c, CR 508.1d)

CR 508.1d: "If a creature can't attack unless a player pays a cost, that player is **not required** to
pay that cost, even if attacking with that creature would increase the number of requirements being
obeyed." So the view's job is to offer the declarations whose tax this player *can* pay, and to say
what each costs. Four things it needs, in order of how much they cost to build.

**(a) `CouldAttack` is already the right filter and is not enough.** `ICombatSystem.CouldAttack`
(CR 508.1a) admits a taxed creature — E4i made it, deliberately, because a taxed creature *is* able to
attack. So the view already lists the creature; what it cannot yet say is "and it will cost you {4}",
and it cannot say "and you cannot afford it".

**(b) The price is per declaration, never per creature.** `AttackTaxes` totals **once per effect
record**, not once per attacker, because the scaling lives inside the cost's own amount
(`PayManaCost(Times(AttackersAgainst(You), 2))` — E4i § 3). The amount is evaluated against the state
the declaration's `AttackersDeclared` has been folded into, which is CR 508.1h's "the total cost to
attack". A view that prices creatures one at a time and adds up will charge the square of what the
card says. **The only honest per-creature number is a delta**: `cost(D ∪ {c}) − cost(D)` for the
declaration `D` the player has assembled so far. Offer that as "adds {2}", never as "costs {2}".

**(c) The reading itself does not exist and belongs on `ICombatSystem`.** `AttackTaxes`, `TaxSymbols`
and `PayAttackTax` are all private, and re-deriving them in `Legal/**` would be a second copy of
CR 508.1h that drifts. `LegalActions` is already constructed with the combat system
(`Engine/Game.cs`: `new Legal.LegalActions(casting, layers, combat)`), so the seam is the place.
What I propose to add — **add-only** to `Engine/Seams.cs`, so it cannot break another folder:

```csharp
/// <summary>CR 508.1d and 508.1h: what this declaration would cost, and the rule when its player could not pay.</summary>
[RuleRef("508.1d")]
AttackPrice PriceToAttack(GameState state, PlayerId player, ImmutableDictionary<ObjectId, CombatTarget> declaration) =>
    throw new NotImplementedException("Pricing a declaration is not built in this combat system.");

/// <summary>The mana half of CR 508.1h's total, the costs that pay through CR 118.6, and why it cannot be paid — null when it can.</summary>
[RuleRef("508.1h")]
public sealed record AttackPrice(ImmutableList<ManaSymbol> Mana, ImmutableList<Cost> Other, Refusal? Unpayable);
```

The view reads `Unpayable is null` to decide whether to **offer** the declaration and `Mana`/`Other`
to say what it **costs**. Both fields are wanted: a tax that is not mana has no mana symbols to show,
and CR 508.1j pays them as one total. It is four lines over the private methods this wave already
built — `AttackTaxes`, then `TaxSymbols` per cost with the throw replaced by the `Refusal`, then
`CostPayment.PaySymbols` against a trial state for the mana half. **Owner: `Combat/**` with
`Engine/**` (mine). I will build it next wave unless E14c wants it sooner — say so and it is one box.**

**(d) Payability is two questions, and one of them this engine does not ask.** A declaration is
affordable when the mana half fits the pool (CR 508.1j spends what is there) **and** every other cost
answers `CanPayForRule` null (CR 118.6). But **CR 508.1i gives the player a window to activate mana
abilities between the total being locked in and the payment**, and `CostPayment` never suspends, so a
view that prices against the pool as it stands **understates what the player could pay** — an
untapped Forest is not counted. E4i § 2 recorded that gap against `Turn/**`/`Engine/**` (priority) and
it is still open. Until it closes, the view should say "you cannot pay this **now**", not "this
declaration is illegal", or it will hide a legal attack behind an unspent land.

## 3. Recorded, not asked

- **The declaration space is exponential and must not be enumerated.** With *n* untapped creatures and
  *d* defenders there are `(d+1)^n` declarations; the view cannot price them all. CR 508.1d's shape is
  that the player assembles a declaration and declines the cost by leaving creatures out, so the view
  should price **the declaration in hand** and offer the per-creature delta from (b), not a catalogue.
- **E4i § 3's identity limit stands.** Two permanents printing the same tax produce two *equal*
  `CantAttack` records and are counted by how many copies a single attacker carries. A view that wants
  to say *which* permanent is taxing the player cannot, until `ActiveEffect` carries its source
  (a `State/**` and `Characteristics/**` ask, not combat's). Nothing in the corpus prints two yet.
- **The refusal path is a throw, by design.** `DeclareAttackers` throws `RefusedException`, which
  `Game.Apply` turns into a `Refused`. The view must not learn payability by trying the declaration —
  a trial `DeclareAttackers` would pay the tax on the way to finding out. `PriceToAttack` above exists
  precisely so the view never has to.
- **CR 508.1g is where "is this tax owed by this attack" is read** (`Taxes`), which depends on the
  *defender chosen*, so a price is only meaningful for a declaration that names its targets. A view
  offering "attack with this creature" without a defender cannot be priced.

## Not done in this box

- **`PriceToAttack` is not built** — § 2(c). This box built the payment; the view's reading is the
  next one, and no corpus card needs it before a client screen does.
- **No verify run**: the corpus lives under `MtgEngine.Api/bin` in the main checkout and this worktree
  cannot reach it. No corpus card prints a non-mana attack tax today, so nothing in `cards/` moves on
  this change; the six anchor tests and the 1184-green suite are the evidence.
- **Nothing outside my own files is touched**; `cards/` and `.agents/` are untouched.
