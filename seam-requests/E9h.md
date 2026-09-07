# E9h — seam requests

Wave 8's `ICastingSystem.CostChoices` closed E9g §1 and §2 together: the interpreter's copy of the
cost table (`Wants`, `Where`, `CostObjectsWanted`) is deleted, the `"cast-cost"` question offers
what the casting system says the command still has to name, and a face carrying two object-naming
costs is asked twice — what the earlier answers named rides on the run's mark, and the accumulated
list goes back on `CastSpell.CostObjects` in cost order. Nothing in the interpreter reads a printed
face to guess at a cost any more. Three things are left.

## 1. Nothing fires a `ManaSpent` delayed trigger (CR 106.6a) — the half that is owed

The interpreter now does its half of V6 §3, and it is worth saying exactly what that half is so the
other half is not built to a different shape:

- An `AddMana` with a `Key` writes **one provenance block per single mana** on the pool
  (`ManaPool.Provenance`, wave 6's record): `Amount` 1, the resolving source, and the key
  `"<key>#<n>"` where `n` counts the mana this instruction produced. Two mana of `{G}{G}` are two
  blocks with two keys, which is what makes CR 106.6a's "a separate delayed ability for each mana
  produced" expressible at all.
- A `DelayedTrigger` whose `When` is a `ManaSpent` naming that key then creates **one delayed
  ability per block**, each watching its own mana: `ManaSpent("<key>#0")`, `ManaSpent("<key>#1")`.
  A `ManaSpent` key this resolution produced no mana for is left exactly as the card wrote it,
  because CR 106.6 lets a linked ability on another permanent be the one that makes the mana.

**Nothing fires them.** No subsystem reads `ManaSpent`, so the abilities sit in
`GameState.DelayedTriggers` until the pool empties. That is honest state and not a silent failure,
but a card that spells this out is **not** `Verified` until one of these lands:

- **Preferred, and E3e already owes half of it**: `ManaSymbols.Pay` reports which mana paid — the
  restricted block *and* the provenance block — so the casting system can hand the trigger system
  the keys that were spent, filtered by `ManaSpent.OnSpell` where the card names a spell. The
  filter needs the spell being paid for, which only the casting system has.
- **Or**, if that channel does not land: the trigger system folds a `ManaChanged` whose `Delta`
  carries a provenance block with a negative `Amount`, and fires the delayed ability whose
  `ManaSpent.ManaKey` equals that block's key. That reads the spend off the state rather than off
  the payment, so `OnSpell` cannot be honoured — a `ManaSpent` with `OnSpell` set should be refused
  by name rather than fired on the wrong spell.

Either way the firing side must match on the **written key** (`"<key>#<n>"`), not on the card's own
key, or one spend fires every ability the instruction created.

## 2. `MayCast(AsCopy: true)` still refuses (CR 707.12) — E9g §3, unanswered

Unchanged and restated because it is still the only `MayCast` shape that refuses on a live card:
CR 707.12 creates the copy **in the zone the object is in** and casts that, so the copy has to exist
as an object before `ICastingSystem.Cast` is handed anything, and no seam makes one —
`IZoneSystem.CreateToken` makes a permanent on the battlefield, which CR 707.12 explicitly is not.
The shape asked for is still:

```csharp
/// <summary>CR 707.12: a copy of an object, created in the zone that object is in.</summary>
[RuleRef("707.12")]
Outcome CopyInPlace(GameState state, ObjectId original, PlayerId owner, out ObjectId copy);
```

with CR 704.5e's "cease to exist anywhere but the stack or the battlefield" as the rules system's
state-based action. Until both land the arm refuses naming CR 707.12, which is honest: casting the
original in the copy's place is a different game action.

## Not asked for, recorded instead

- **`Unsupported` no longer throws `NotSupportedException`.** It throws `RefusedException` with the
  translator's reason and CR 608.2, so the runner reports a failed card with the sentence it could
  not say instead of a crash at the engine's front door. Anything that catches the old exception
  type — the pipeline runner, the smoke harness — sees a refusal now, which is the same shape every
  other unbuilt arm already had. The one pilot card that "crashed on its own `Unsupported` marker"
  should now be a named failure.
- **`MayCast(AlternativeCost: …)`** still refuses naming CR 118.9, unchanged from E9f and E9g:
  `CastSpell.AlternativeCost` is a `KeywordKind?` and the effect states its cost in its own words.
  A `Cost?` beside the flag is the channel if the corpus needs it; no pilot face does.
- **`CostChoice.Cost` is read as prose.** The interpreter puts it in the question's prompt
  ("Choose what pays that spell's *sacrifice a creature*."), so it should read as the cost by the
  name a player would recognise, not as a type name. Nothing else reads it.
- **The ask-and-announce-again loop is capped** at 16 named objects (`MaxCostObjects`). Each
  question adds at least one object, so the cap is what makes the loop finite when a casting system
  refuses a command however it is answered; past it the casting system's own refusal is relayed.
