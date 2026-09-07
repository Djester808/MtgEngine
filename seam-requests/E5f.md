# E5f — CR 704.5e landed; what a copy of a card is marked with is still one flag short

Wave 11. Owns `MtgEngine.Rules/Sba/**`, `MtgEngine.Rules/Views/**` and the two test anchors.
Nothing here blocks: the rule is built and green. Both items are readers that will have to learn
about a mark someone else makes.

## 1. `StackEntry.IsCopy` is the only mark the state puts on a copy — not asked for, recorded

CR 704.5e is now a state-based action, sharing 704.5d's "ceases to exist" path
(`StateBasedActions.Permanents.Ceases`). It asks two things of an object: `IsToken`, and
`obj.Stack is { IsCopy: true }`. The second is the whole of what the state holds for "this is a
copy" — `GameObject` has no copy flag of its own, and `E1c`'s `CopyInPlace` was not on
`card-engine` when this was written, so which mark it sets could not be read.

If `CopyInPlace` (CR 707.12) sets a **`StackEntry` with `IsCopy: true`** on the copy it makes in
the original's zone, this rule is already correct for it and nothing is owed. If it sets anything
else — a `GameObject.IsCopy`, a designation — then **two readers have to be changed with it**, and
neither is mine after this box:

- `MtgEngine.Rules/Sba/StateBasedActions.Permanents.cs`, `Ceases` (mine; one line).
- `MtgEngine.Rules/Views/ViewProjector.cs`, the `IsCopy` the view now carries (mine; one line).

A `GameObject.IsCopy` beside `IsToken` is the shape that would make both readers honest, and it
is the shape `Query.Predicates.cs` already wants — see §2. That is a `State/` change, so it is
recorded here rather than made.

## 2. `IsCopy` as a predicate still throws for a permanent (CR 707.10)

`Query/Query.Predicates.cs:61-62` answers the `IsCopy` predicate on the stack and throws
`Unanswerable` off it: "no state records that a permanent is a copy (CR 707.10)". That reading was
right when a copy could only be a spell. It is now half wrong in the same way: CR 704.5e's second
sentence says a copy of a card may be on the battlefield, and this rule keeps it there, so a
permanent that is a copy is a state the engine can reach and a card may ask about. The fix is the
same flag as §1. Not mine — recorded for whoever owns `Query/`.

## 3. Behaviour that changed, for anyone reading the old arm

Before this box the arm read `obj.Stack is { IsCopy: true } && zone != Zone.Stack`, which removed a
copy from **the battlefield** as well. That is only 704.5e's first sentence; the second exempts the
battlefield, and `StateBasedActionTests.A_copy_of_a_card_on_the_battlefield_stays` fails against
the old spelling (checked by reverting the line and running the anchor: 1 failed, 58 passed).

A resolved copy of a permanent *spell* is not caught by the exemption, because `ZoneSystem.Reborn`
(CR 400.7) does not carry the stack entry onto the new object: such a copy arrives on the
battlefield unmarked, and CR 707.10a makes it a token in any case, which is 704.5d's arm.

## 4. The view carries `ObjectView.IsCopy` (CR 707.10)

`Views/GameView.cs` gained a trailing `bool IsCopy = false` and the projector fills it from the
same mark. CR 707.10 gives a copy the copied object's name and characteristics, so without it two
identical rows on the stack are indistinguishable and a client cannot say which one 704.5e is
about to take away. It is public (the stack is a public zone, CR 400.2) and it is withheld from a
face-down object with everything else CR 708.4 withholds, since a copy *is* the thing it copies.
Tested in `HiddenInformationTests.A_copy_on_the_stack_is_shown_as_a_copy_and_the_card_beside_it_is_not`.
The field is optional and trailing, so no existing construction of `ObjectView` changed.
