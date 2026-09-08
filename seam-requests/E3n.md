# E3n — wave 23: the three offered costs the casting system now reads (V11 row 4, V12 rows 1 and 3)

All three landed inside `MtgEngine.Rules/Casting/**` with no seam change, so nothing here blocks the
merge. Each ask below is a place where the road I built is honest but narrower than the vocabulary,
and a later wave can widen it without redoing this work.

## What was built

| Record | Where | How the command names it |
|---|---|---|
| `Face.AlternativeCosts` / `AlternativeCost(Cost, OnlyIf?)` (CR 118.9) | `CastPlan.Alternative` → `CastPlan.PrintedAlternative` | `CostChoices` carries `"alt"` (the first) or `"alt:2"` |
| `OptionalAdditionalCost(Spells, Cost, Who, IfPaid?)` with `CostModifier.OnlyColor` (CR 118.8, 601.2f) | `CastPlan.Offered` / `Offers` / `Bought` | `CostChoices` carries `"addcost"` or `"addcost:1"` |
| `ChooseMode.Budget` with `Mode.Cost` (CR 700.2) | `ModeAnnouncement.Require` → `Afford` / `Price` | nothing new — the announced `Modes` are priced |

The alternative cost is read **before** the three other roads to the same replacement (the
permission's waived mana cost, the permission's life-instead rider, and a keyword alternative cost),
so every pair of them is refused by CR 118.9c rather than paying two prices or none. The offer list is
read off `state.ActiveEffects` exactly the way `CastPlan.Permission` reads `MayPlayFromZone` — the
effect's own filter and `Who`, or the objects and players a resolution named on the `ActiveEffect` — so
a cost offer and a play permission can never disagree about which spells and players an effect reaches.

## Asks

1. **E-series (query)** — `Query.Conditions.Paid` answers `PaidAlternativeCost` with no keyword named
   as `OptionalCostsPaid.Count > 0`. That was already loose (a kicked spell answers true), and this
   wave adds two more strings to that list: `"alternative"` (a printed alternative cost was paid, which
   the condition **should** answer true for) and `"additional"` (an offered additional cost was paid,
   which it should **not**). The constants are `CastPlan.PaidPrintedAlternativeCost` and
   `CastPlan.PaidOfferedAdditionalCost`; a precise reading asks for the alternative markers by name
   (the keyword names, `"free"`, `"life"` and `"alternative"`) instead of counting the list.
2. **E-series (commands)** — `CastSpell` has no typed channel for either offer, so both ride
   `CostChoices` as `"alt[:n]"` / `"addcost[:n]"`. That is the channel the brief pointed at and it
   costs nothing at the seam, but an index into `Face.AlternativeCosts` and an index into the offers in
   force are two different namespaces sharing one string list. If a later wave adds
   `AlternativeCostIndex` and `AdditionalCostsTaken` to `CastSpell`, the two readers above are the only
   callers to change.
3. **E-series (interpreter / stack entry)** — nothing records `ChooseMode.Budget` on the stack entry
   the way `ModeAnnouncement.CeilingKey` records a conditional ceiling. A modal spell announced with
   **no** modes leaves them to the resolution's question (as it always has), and that question is
   therefore not told the budget. Season of Gathering `3ea060d8` announces its modes at 601.2b so this
   does not block it; a card that chooses priced modes on resolution would need a second key beside
   `CeilingKey`.
4. **Coordinator / brief** — `.agents/briefs/wave23.md` does not exist in the tree at the wave-22 tip
   (`38cf3ea`), so the task came from the launch prompt only. Nothing was lost that I can tell — the
   three items and the three card ids matched V11/V12 — but a builder who needed the full text could
   not have read it.

## Shapes refused rather than thrown

Every one is a `RefusedException` naming its rule, and each has a test.

- CR 118.9 — a face that prints no such alternative, or one whose `OnlyIf` does not hold.
- CR 118.9c — two printed alternatives at once, a printed one beside a keyword one, and a printed one
  beside a permission that waives the mana cost or asks for life instead.
- CR 601.2h — a cost choice shaped `alt:<not a number>`.
- CR 118.8 — an `addcost` no effect in force offers, and an `IfPaid` that is not a `CostModifier`
  (nothing else can be applied to one casting yet).
- CR 601.2b — the same offer taken twice.
- CR 601.2f — a `CostModifier` that is neither `GenericOnly` nor tied to an `OnlyColor`, which names
  no half of the cost to come off.
- CR 700.2 — a mode with no price, or a price that is not mana, under a budget.

## Decisions taken here

- **A printed alternative cost grants no zone.** A keyword alternative cost names the zone it is paid
  from (CR 702.34a), so `CastPlan.Alternative` lets `command.AlternativeCost` past the CR 601.3 zone
  check; `Face.AlternativeCosts` says nothing about a zone, so a card in a graveyard still needs a
  permission to be cast for one.
- **The alternative's `Cost` rides with the non-mana costs.** It is a `Cost` of any shape, and
  `CostPayment` already pays every shape it can and refuses the rest by name, so a mana alternative and
  a "sacrifice a creature" alternative take one road.
- **`OnlyColor` wins over `GenericOnly`.** The vocabulary defaults `GenericOnly` to true and L030
  refuses a record that claims both, so a card that sets `OnlyColor` has `GenericOnly: false`; reading
  the colour first means a card file that carries the default anyway still reduces the colour it names.
- **A colour reduction leaves a hybrid or Phyrexian symbol alone.** Removing half of `{2/G}` or
  `{G/P}` would change which mana may pay the rest of it (CR 107.4e, 107.4f), so only a whole
  single-colour symbol comes off.
- **A positive `Delta` raises the cost.** The corpus spells a reduction as a negative literal (V12's
  own note), so the sign is read rather than assumed: a positive delta adds that much generic, which
  is what "costs {1} more" means, and zero does nothing.
- **`Max` is not read while a budget rules the modes** (V12's row 3), but the conditional ceiling is
  still computed and still recorded, so a card with both a budget and a `MaxIf` records what it always
  did and nothing reads the condition twice.
