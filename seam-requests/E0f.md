# E0f — a copy is answerable off the stack; the engine's land-type copy is gone

Wave 12. Owns `MtgEngine.Rules/Query/**`, `MtgEngine.Rules/Characteristics/**` and the four test
anchors. **No seam is asked for.** Both items were readable from what waves 10 and 11 had already
landed; what follows is what changed and the two notes the next owner of a neighbouring folder
should read.

## 1. CR 707.10 with CR 707.12 — the predicate reads both marks

`Query/Query.Predicates.cs` refused `IsCopy` off the stack ("no state records that a permanent is
a copy"). That reading was true when a copy could only be a spell. E1c's `CopyInPlace` now marks a
copy of a card with the zone system's designation, so the arm is:

```csharp
IsCopy when obj.Zone.Zone == Cards.Zone.Stack =>
    obj.Stack?.IsCopy == true || obj.Designations.Contains(Zones.ZoneSystem.CopyOfCard),
IsCopy => obj.Designations.Contains(Zones.ZoneSystem.CopyOfCard),
```

The stack arm keeps both readings because CR 707.12's copy is made **in the zone the original is
in**, which is the stack when a card is copied there; a copied *spell* is the stack-entry mark and
the two are different objects with different marks, not two spellings of one.

This is the same pair `Sba/StateBasedActions.Permanents.cs` (`Ceases`) and
`Views/ViewProjector.cs` already read, so all three readers of "is a copy" now agree, and E5f's
§2 ("`IsCopy` as a predicate still throws for a permanent") is closed. **E5f's `GameObject.IsCopy`
stays unneeded**: the wave-11 decision declined it as a second spelling, and with the query layer
reading the designation there is no fourth reader that would want a field.

- **The literal is still `ZoneSystem.CopyOfCard`, not `Query/Designations.cs`.** E1c recorded that
  the name belongs with the other designation names, and `Query/Designations.cs` is mine — but the
  constant's *declaration* is in `Zones/`, which is not, and the three readers outside `Query/` are
  not mine either. Moving it is a four-file rename across three owners; whoever owns `Zones/**` and
  the two readers in one box should do it in one commit. Every reader already goes through the
  constant, so it is a rename and not a behaviour change.

## 2. CR 205.3i — `Query/LandTypes.cs` is deleted

V7 generated the basic land types into `MtgEngine.Cards/Generated/LandTypes.g.cs` and E0d's
decision was that the engine's copy goes when it lands. It is deleted. Both readers named in the
brief already spelled the name unqualified and both files carry `using MtgEngine.Cards;`, so they
re-bound to the generated set with no edit:

- `Query/Query.cs`, `HasAnyBasicLandType` (the `HasBasicLandType` filter).
- `Query/Query.Amounts.cs`, `BasicLandTypes` (the domain count).

A third reader, `Interpreter/EffectInterpreter.Structure.cs:216`
(`ChooseWhat.BasicLandType => Offer(LandTypes.Basic)`), was **never** reading the engine's copy —
that class was `internal` to `MtgEngine.Rules.Query` and the interpreter has no `using` for it, so
it already bound to `MtgEngine.Cards.LandTypes`. Nothing there changed either. `Offer` takes
`IEnumerable<string>` and sorts by default, so the generated `FrozenSet` in place of the old
`ImmutableArray` changes no offered order.

`grep -riE '"(plains|island|swamp|mountain|forest)"' MtgEngine.Rules/ --include=*.cs` is now empty:
the engine spells no land type anywhere, and a new rules text reaches the count by re-running the
generator.

## 3. Tests

New anchor file `tests/MtgEngine.Rules.Tests/QueryTests.Copies.cs` (`QueryTests` is partial;
`QueryTests.cs` was at 447 lines and the additions would have taken it past 500).

- `A_copy_is_answered_from_whichever_mark_the_state_carries` — false on an ordinary permanent, true
  on one carrying the designation, true on a copied spell, false on a plain spell. Every one of the
  four threw `RefusedException` before the change.
- `Domain_counts_the_generated_basic_land_types_and_nothing_else` — the generated set has five
  members, a board of one land per generated type counts five, and a land with a non-basic land
  subtype adds nothing (CR 205.4a).
- `QueryBacklogTests.A_question_the_state_still_cannot_answer_refuses_by_name` was the recorded
  refusal; it is now `A_copy_of_a_card_is_answered_from_the_designation_the_zone_system_set` and
  asserts the answer from the same `Designate` helper the rest of that class uses. Its second
  assertion (the turn's damage history) is unchanged.
- `QueryTests.A_question_the_state_cannot_answer_names_the_record_instead_of_guessing` keeps its
  `Commander` half — that selector still refuses by name (CR 903.3 has no store to read) — and
  lost only the `IsCopy` half.

765 tests in `tests/MtgEngine.Rules.Tests` green (763 at the wave-11 tip; two tests net).
