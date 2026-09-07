# E1c — seam requests

`IZoneSystem.CopyInPlace` is implemented in `Zones/ZoneSystem.Copies.cs`, so **no new seam is
asked for**: the seam and its throwing default were opened after E9h and the shape is unchanged.
What follows is the half another folder owes, and one assertion that has to flip.

## 1. What CR 704.5e reads to recognise a copy of a card (E5f)

A copy made by `CopyInPlace` is marked with a **designation**, not a new state field, because
nothing on `GameObject` said "copy of a card" and adding a field is a `State/*` change nobody in
this wave owns:

```csharp
// MtgEngine.Rules/Zones/ZoneSystem.Copies.cs
public const string CopyOfCard = "copy-of-card";
```

So the state-based action of CR 704.5e is:

```csharp
// 704.5e ... If a copy of a card is in any zone other than the stack or the battlefield, it ceases to exist.
if (obj.Designations.Contains(ZoneSystem.CopyOfCard) && zone is not (Zone.Stack or Zone.Battlefield))
    verdicts.Add(new Verdict(id, Action.Cease, Ceased));
```

beside the arm already there for a copy of a *spell* (`obj.Stack is { IsCopy: true } && zone !=
Zone.Stack`). Three things about the mark are load-bearing:

- **It is not `IsToken`.** A copy of a card is not a token (CR 111.1), and CR 704.5d would remove
  it from a graveyard the instant it was created, which is the wrong rule and the wrong sentence in
  the log. `CopyInPlace` passes `isToken: false` to `CopyRules.NewCopy`.
- **It is not `StackEntry.IsCopy`.** That says a copy of a *spell* is on the stack; a copy of a card
  in a graveyard has no stack entry at all, and giving it one to be found by would make CR 601.2's
  reader believe it was cast.
- **It survives nothing.** CR 400.7 builds a new object on every zone change and `Reborn` copies no
  designations, so a copy that is cast (CR 707.12: the copy moves to the stack) arrives on the stack
  without the mark and CR 704.5e stops applying to it, which is what the rule wants. The mark only
  ever describes the object created in place.

The literal belongs in `Query/Designations.cs` with the other designation names; that file is not
mine this wave, so the constant lives on `ZoneSystem` and the string is `"copy-of-card"` either way.
Moving it later is a rename, not a behaviour change — read the constant, not the literal.

## 2. `IsCopy` on a permanent is now answerable (Query, not asked for)

`Query/Query.Predicates.cs` refuses `IsCopy` outside the stack with "no state records that a
permanent is a copy (CR 707.10)". That is still true of a copy **token** (`CreateToken(CopiableValues …)`
marks nothing), but a copy of a card created by `CopyInPlace` now carries the designation, so a
future arm can answer it for that case. Nothing is changed here: half an answer is worse than a
named refusal, and no pilot face asks.

## 3. One assertion flips with this landing

`tests/MtgEngine.Rules.Tests/InterpreterTests.Chosen.cs`,
`A_may_cast_of_a_copy_is_refused_by_name_when_no_zone_system_can_make_one` (line 165) builds an
`InterpreterTable` with the **real** `Zones.ZoneSystem` and asserts the CR 707.12 refusal that the
wave-10 decision recorded as standing "until a zone builder implements `CopyInPlace`". It now fails
with `Assert.Throws() Failure: No exception was thrown` — the copy is made and the may-cast
announces it. The test is the interpreter's file, not mine, so it is left failing rather than edited
from outside its owner, exactly as E4e's priority assertion was flipped at the wave-10 join
(`44c0ac2`). Its sibling
`A_may_cast_of_a_copy_announces_the_copy_the_zone_system_made` keeps the fake zone system and still
passes, so the interpreter's half stays proved from both sides; the refusal test should be deleted
or rewritten to the real system's behaviour when it is merged.
