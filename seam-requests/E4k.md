# E4k — combat with the legal-actions view: the priced declaration

Wave 30. Owns `MtgEngine.Rules/Combat/**`, `MtgEngine.Rules/Legal/**`,
`tests/MtgEngine.Rules.Tests/Combat*.cs`, `tests/MtgEngine.Rules.Tests/Legal*.cs`. Anchor
`CombatTests`: ten new tests across `CombatTests.Wave30.cs` (seven) and
`LegalActionsTests.Wave30.cs` (three).

## 1. Done — E14c ask 1: `CombatSystem` implements `Views.IAttackTaxes` (CR 508.1d with 508.1h)

`Combat/CombatSystem.Wave30.cs`. `TaxToAttack` builds the one-attacker declaration, totals it with
the **same private `AttackTaxes`** `PayAttackTax` uses, and evaluates the amounts against the state
that declaration's `AttackersDeclared` has been folded into — CR 508.1h's "the total cost to attack",
locked in. Nothing is applied: the folded state is local and discarded, and no cost is paid or
offered to be paid. A creature that owes nothing returns null, which is every creature in a game
where nothing prints the cost, so null is the ordinary answer and not a failure.

`Payable` is CR 508.1j against the pool as it stands: the mana half through `ManaSymbols.Pay`, and
every other cost through `ICastingSystem.CanPayForRule` (CR 118.6) — the question that changes
nothing, proved by a test that asserts the fake's `Paid` list is empty and the player's life
unmoved. As E14c asked, **the refusal arm is now an offer and not a throw**: a cost this engine
cannot express as mana, and a game with no casting system to answer for it, both give
`Payable: false` with the cost's own text rather than a `RefusedException`. The declaration itself
still refuses (CR 508.1h), and one test asserts both halves of that pair on one board — the view
exists precisely so a player is not made to discover the price by being refused.

Two readings worth recording:

- **The cost's own text is the vocabulary record's spelling**, `Cost.ToString()`, not English
  written in engine source. A card's wording is data; a phrase invented here would be a second,
  drifting copy of what the card file already says. Printed mana is the exception — `{2}` is the
  cost's own text — and a counted cost prints `{n}` from the amount evaluated at CR 508.1h, with
  the coloured symbols alone when the generic half is nothing (CR 107.4).
- **An amount that comes out below nothing** is offered as the cost it is and not payable. The
  declaration refuses it (CR 508.1h); a view that threw would hide the attacker.

## 2. Done — E14c ask 2: `LegalActions.For` wires it (CR 508.1d)

`Legal/LegalActions.cs` takes `Views.IAttackTaxes? taxes = null` beside the existing
`ICombatSystem? combat = null`, and `For` sets
`AttackTaxes = AttackTaxesView.For(_taxes, state, player, attackers)` from the **`EligibleAttackers`
list it has just computed** — that list is now computed once into a local and used for both, so a
creature can never carry a price it was not offered. Composed without a producer the view offers
the attackers and no prices at all, which is the bargain `_combat` already makes; both arms are
tested, and the empty arm is the mutation that proves the wiring (the property defaults to empty).

## 3. Asks

| Ask | Folder | Why |
|---|---|---|
| **The one line in `Engine/Game.cs`**, twice — line 51 (`Subsystems.Wave1()`) and line 100 (`Subsystems.Wave2()`), identical text in both: `            new MtgEngine.Rules.Legal.LegalActions(casting, layers, combat, combat));` — the local `combat` is a `Combat.CombatSystem`, which now implements `Views.IAttackTaxes`, so no cast and no new local are needed. | `Engine/**` | Not mine. Until it lands the composed game offers no prices; nothing else changes, because the parameter is optional and the view property defaults to empty. |
| **The defending player on the signature** (E14c's third row, restated with a measurement). The shape implemented is the one that exists today, `TaxToAttack(GameState, PlayerId, ObjectId)`, and it prices against the forced defender: CR 506.2 says the nonactive player is the defending player, so in a two-player game nothing is guessed. With more than one opponent it prices against **no** defender, which leaves exactly the taxes owed by any attack and silently understates a tax that names one player. The signature I want is `AttackTaxOffer? TaxToAttack(GameState state, PlayerId player, ObjectId attacker, CombatTarget target)` — the whole record, not a `PlayerId`, because CR 508.1c's tax can name the controller of the planeswalker or battle being attacked (CR 506.1), and `DefendingPlayer` already reads a `CombatTarget` for exactly that. `Views/AttackTaxes.For` then needs the declaration's targets, which the view does not hold today either. | `Views/**` (E14-series), then `Combat/**` and `Legal/**` | Nothing in the corpus prints a multiplayer attack tax, so this is not urgent — but the two-player answer must not be mistaken for the general one, and `PricedAgainst`'s doc comment says so at the site. |
| **CR 508.1i is still not taken.** Unchanged from E4i § 2 and E14c ask 4, repeated only because the number now reaches a screen: `Payable` reads the pool as it stands, so a false there means "not out of what you are holding" and never "this attack is illegal". An untapped land that would pay is not counted. | `Turn/**` / `Engine/**` | A client that reads the flag as a legality will hide a legal attack behind an unspent land. |

## 4. Recorded, not asked

- **The per-creature price is not a delta yet.** E4j § 2(b) is right that the honest per-creature
  number for an assembled declaration `D` is `cost(D ∪ {c}) − cost(D)`, and `TaxToAttack` prices
  `{c}` alone because that is the signature the view has and the declaration space must not be
  enumerated. For every tax in the corpus today the two agree — the scaling lives inside the
  amount, and with one attacker there is nothing to scale — so the difference is unobservable until
  a card prints `PayManaCost(Times(AttackersAgainst(You), 2))` **and** a client assembles a
  multi-creature declaration. When that arrives the ask is a declaration parameter, not a second
  reading: the same `AttackTaxes` over `D ∪ {c}` and over `D`.
- **`PriceToAttack`/`AttackPrice` (E4j § 2(c)) was not built and is not wanted.** E14c chose the
  simpler shape and built the view against it; a second seam that returns `(Mana, Other, Unpayable)`
  would be the same reading twice. If a client needs the mana half separately from the rest, the
  place to split it is `AttackTaxOffer`, in `Views/**`.
- **Nothing verifies through this.** No corpus card prints an attack tax with a scenario, and the
  corpus lives under `MtgEngine.Api/bin` in the main checkout, which this worktree cannot reach.
  The ten anchor tests and the Rules suite are the evidence. Unverified against a running client:
  no screen reads `AttackTaxes` yet.
