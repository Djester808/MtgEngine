# E4i — the attack tax and the monarch's move

Wave 26. Owned: `MtgEngine.Rules/Combat/**`, `tests/MtgEngine.Rules.Tests/Combat*.cs`.
Both halves of the brief landed and are tested. What follows is what another folder owns, what
the rules text says where the brief said something else, and the one limit the vocabulary's shape
puts on the tax.

## 1. The attack tax is a cost, not a restriction (CR 508.1c vs 508.1d and 508.1h)

The brief and `V14.md` both read `CantAttack.UnlessPays` at "declare attackers"; reading CR 508.1
in full says **where** in the declaration it belongs, and it is not with the restrictions.

- **CR 508.1c** is the restriction check: "effects that say a creature can't attack, or that it
  can't attack unless some condition is met". A cost is not a condition.
- **CR 508.1d** says it in as many words: "If a creature can't attack unless a player pays a cost,
  that player is not required to pay that cost, even if attacking with that creature would increase
  the number of requirements being obeyed."
- **CR 508.1h** is the tax: "the active player determines the total cost to attack… Once the total
  cost is determined, it becomes 'locked in.' If effects would change the total cost after this
  time, ignore this change." **CR 508.1i** then offers mana abilities and **CR 508.1j** pays: "they
  pay all costs in any order. Partial payments are not allowed."

So `UnlessPays` is now skipped by `CheckAttackRestrictions` (CR 508.1c) and by `CouldAttack`
(CR 508.1d — a taxed creature *is* able to attack), and is totalled and paid in
`CombatSystem.Wave26.cs`'s `PayAttackTax` between CR 508.1d's requirement check and the
`AttackersDeclared` of CR 508.1k. `V14.md`'s citation of "508.1c with 508.1h" should read
**508.1h with 508.1j**; 508.1c is the half the tax is expressly *not*.

**One divergence from the brief, deliberately.** The brief asked that "an attacker whose controller
does not pay is not declared". The rules text does not offer that: the creatures were chosen at
CR 508.1a, the total was locked in at CR 508.1h, and CR 508.1's preamble says that a player unable
to comply with any step makes **the whole declaration** illegal, with CR 733 returning the game to
the moment before it. Dropping one attacker would also re-open the total, which 508.1h has just
frozen. So an unpayable tax throws `RefusedException`, exactly as every other illegal declaration
in this subsystem does, and the state is untouched (tested).

## 2. Owed to other folders

| Ask | Folder | Why |
|---|---|---|
| **The player is never offered the choice to decline the tax and attack with fewer creatures.** CR 508.1d makes paying optional, so the real shape is: the legal-actions view offers the declarations whose taxes this player can pay, and the player picks one. | `Views/**` (`LegalActions`) with `Commands/**` | Combat can only answer "is this declaration legal"; it does not enumerate declarations. `CouldAttack` now admits a taxed creature (CR 508.1d), so the view already lists it — what the view cannot yet say is "and it will cost you {4}". |
| **A non-mana attack cost cannot be paid.** `TaxSymbols` refuses with CR 508.1h for anything but `ManaCost`/`PayManaCost`. | `Casting/**` | `CostPayment.Pay` needs a `CostContext`, which needs an `IZoneSystem`; combat is constructed with `ICharacteristics` and `IQuery` only. Either combat gains a settable `Zones` handle wired in `Engine/Game.cs` the way `Replacements` is, or the casting system grows a "pay this cost for a rule, not for a spell" entry point. Nothing in the corpus prints a non-mana attack tax today. |
| **CR 508.1i is not taken.** The player gets no window to activate mana abilities between the total being locked in and the payment; the tax is spent out of the pool as it stands. | `Turn/**` / `Engine/**` (priority) | Same shape as every other cost this engine pays: `CostPayment` never suspends. Worth a row only because 508.1i is a real step. |

## 3. The one limit the vocabulary's shape puts on the tax

`CantAttack.UnlessPays` is a `Cost` whose scaling lives **inside** the amount
(`PayManaCost(Times(AttackersAgainst(You), 2))` — `V14.md` § "Decisions taken here"). That is one
cost for the whole declaration, not a cost per creature, so `AttackTaxes` totals it **once per
effect record** rather than once per attacker; charging per attacker would charge the square of
what the card says. The amount is evaluated against a state the declaration's `AttackersDeclared`
has already been folded into, which is CR 508.1h's "against the declaration being made" (tested:
two attackers cost {4}, not {8} and not {2}).

**The limit**: an effect record carries no identity of the permanent that created it, so two
separate permanents printing the same tax produce two *equal* `CantAttack` records. The only
evidence of the second copy is how many copies of that record a single attacker carries, so the
count is taken as `max` over the attackers of that multiplicity. That is right when the layer
system grants each taxing permanent's effect separately, and wrong if it ever de-duplicates
identical continuous effects onto an object. If a card ever needs this to be exact, `ActiveEffect`
would have to carry its source to the reader — a `State/**` and `Characteristics/**` ask, not
combat's. Not asked for now: no slice prints two copies.

## 4. The monarch's combat half — the road taken, and why (CR 725.2)

**Taken as the combat system's own emit, not as a triggered ability.** `CombatSystem.Wave26.cs`'s
`MonarchMoves` emits `MonarchChanged` per hit inside the damage batch.

The reason is the one already recorded for the other half of CR 725.2. That rule's two inherent
abilities "have no source" and belong to a *player*, and nothing in this engine can give a player
an ability — an ability is data on a card and the monarch is not a card. `E9r.md` recorded that
for the end-step draw and `Turn/TurnSystem.Wave24.cs` takes it as a turn-based action; taking the
combat half the same way keeps one rule on one road. The observable divergence is written in the
doc comment, as it is there: this does not use the stack, so nothing can be cast between the damage
and the crown moving. A card's own `BecomesMonarch` trigger is unaffected — E6h landed that matcher
on `MonarchChanged`, which is exactly the event this emits.

Read per hit off what was **actually dealt**, the same events CR 702.15b's lifelink reads, so a
prevented hit moves nothing; the resumption after a CR 616.1 halt crowns off the events the
replacement system finished the halted hit with. The controller is the one the batch was computed
with (CR 510.2), so a creature that dies to first strike after connecting still crowns its
attacker. CR 725.3 skips a move to the player who already holds it.

## 5. Citations checked against the shipped text

- **CR 725.2** is the monarch's two inherent abilities (E6h's correction holds; 724 is Ending Turns
  and Phases). Both the end-step draw and the combat-damage move are 725.2 — `E9r.md`'s "724.3 /
  724.4" split does not exist in the shipped text, which puts both in one rule.
- **CR 508.1e is banding**, as `V14.md` says; the attack costs are **508.1g** (optional costs
  chosen), **508.1h** (total determined and locked in), **508.1i** (mana abilities) and **508.1j**
  (paid in full). The engine cites 508.1g for "is this tax owed by this attack", 508.1h for the
  total and 508.1j in the refusal path.
