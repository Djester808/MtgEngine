# E0d — what the query now reads of V6, and the three readers that are not the query's

Wave 9. Owned: `MtgEngine.Rules/Query/**`, `MtgEngine.Rules/Characteristics/**`, and the query and
layer test files. Everything below that says "landed" is in this box's commit; everything that says
"asked" is a seam or another folder's file and is not.

## Landed

- **`CreatedTokenThisTurn` (CR 111.1)** reads `TurnHistory.TokensCreated`, which V6's state landing
  keyed by the creating player and filled with the token *as it was created*. `Who` resolves to the
  players asked about; `What` is matched against that record, so a Treasure sacrificed the instant it
  appeared still answers for its creator and never for the opponent. `Query.History.cs`.
- **`ObjectFilter.HasBasicLandType` (CR 205.3i)** is a clause in the filter matcher, reading the
  computed subtypes (so a type granted in layer 4 counts) through the same `HasSubtype` the other
  subtype questions use — which means an object that is every creature type (CR 205.3d) still has no
  land type, correctly. `Query.cs`.
- **`Devotion.ChosenColorKey` and `IsChosenColor`** were already read (wave 5's evaluator arms);
  `Devotion` was tested, `IsChosenColor` was not. It has a test now. Nothing was owed here.

## The one literal, and the generator that should own it

CR 205.3i's five land types were a `string[]` inside the domain count in `Query.Amounts.cs`. The
`HasBasicLandType` clause would have made that literal appear twice, so it is now one internal
`LandTypes.Basic` in `MtgEngine.Rules/Query/LandTypes.cs` and both readers take it from there.

**Asked**: emit it as generated rules data instead, the way CR 205.3m's creature types already are
(`tools/gen-vocabulary.js` → `MtgEngine.Cards/Generated/CreatureTypes.g.cs`). CR 205.3i names the
five in one sentence of the rules text. That is a `MtgEngine.Cards` change and a `tools/` change,
neither of them mine. Until it lands the engine spells them in exactly one place, which is the point
of the file; delete it in the same landing that generates them.

## `LayerSystem.PrintedCharacteristics(GameObject)`

A look-back reads an object the state no longer holds, so the layers of CR 613 cannot be asked about
it — they are computed from the objects that exist now, and a departed token has no id to hand them.
`Query.History.cs` already had this problem for morbid and revolt and answered it by reading
`departed.Face` directly (`WasCreature`). A filter needs the whole characteristic set, not one type,
so rather than duplicate layer 1b's face-down rules into the query, `LayerSystem` now exposes the
printed characteristics as a static: `Printed` no longer takes the state it never read, and
`PrintedCharacteristics` snapshots it.

**Asked**: this arguably belongs on `ICharacteristics` as `PrintedOf(GameObject)` — the query holds
the interface, not the class, and today reaches past it for this one call. It is a seam
(`Engine/Seams.cs`), so it is recorded here rather than taken. It is a one-method addition with one
implementation and one fake; nothing breaks if it never happens.

## V6 records whose reader is in a folder I do not own

Read against V6 §1–§6 and its "Already said" table. These are the ones a card can now carry, that
round-trip and lint clean, and that nothing reads:

| Record | CR | Whose reader |
|---|---|---|
| `MayPay(Paid, Then, Else, Who)` | 118.12, 118.12a | `Interpreter/` — the E9 series. The query answers nothing here; the identity with `Unless` is the interpreter's to keep. |
| `TappedForMana(What, OfColor, OfChosenColorKey)` | 106.12a | triggers plus the mana subsystem. The chosen-colour half is the arm this box just tested for `IsChosenColor`, so the trigger side is the only work left. |
| `ManaSpent(ManaKey, OnSpell)` and `AddMana.Key` | 106.6, 106.6a | the mana subsystem (per-mana provenance on the pool) and casting (`ManaSymbols.Pay` reporting which mana paid). |
| `ActivatedAbility.PayOnlyFrom` | 106.6b | casting / `CostPayment`, on the same per-mana provenance. V6 says an ability carrying it is a *different card* while it is payable with any mana; the casting builder should choose refusal over a wrong payment. |
| `KeywordAbility.OfPlayer` (the enchant restriction) | 303.4, 704.5m | `Sba/` — E5d's, as the brief says. Nothing in the query reads an enchant restriction; `IsAttached` and `AttachedTo` already answer both halves of CR 303.4f for the query's purposes. |
| `PlayerCant` / `PlayerProtectionFromEverything` in layers and SBA | 104.3, 800.4 | still owed since wave 5. `PlayerLimits.cs` is mine and reads what the layers give it; the arm that puts a `PlayerCant` into the layers is not in `Characteristics/`. |

Nothing in that table blocked this box.

## Not done

- `MayPlayFromZone.WithoutPayingManaCost` and cost objects on a may-cast (V6's "not asked for") are
  casting seams; unchanged by anything here.
- The colour-set reference (`ColorSetRef`) V6 recorded for wave 9 is a coordinator decision and would
  move `IrVersion`; the query has no arm for it and none is proposed. One pilot card wants it.
