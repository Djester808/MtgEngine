# E1g — wave 29: a move that puts the object down face down (CR 701.40a, 702.37c)

Built here, `Zone*` **88 green** (46 of them `ZoneChangeTests`, six new). The road E9w asked for
exists in `MtgEngine.Rules/Zones/**`; the one thing I could not write is the seam that lets a caller
holding an `IZoneSystem` reach it, because `Engine/Seams.cs` is not mine — the guard refused the
edit, so the default member below is the ask.

## What was built (mine, no ask)

- `MtgEngine.Rules/Zones/ZoneSystem.FaceDown.cs` — a new partial holding
  `public Outcome Move(GameState, ObjectId, ZoneKey, Placement, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown)`.
  Its body is the body the seven-argument overload used to have; that overload now forwards with
  `faceDown: false`, so every existing caller is unchanged.
- `ZoneSystem.Proposal` and `ZoneSystem.Reborn` take the flag and set `FaceDown` on the **arriving**
  object. That placement is the whole point: CR 701.40a turns the card face down *before* it is put
  onto the battlefield, so the flag has to be on the proposal that `Offer` hands the replacement
  system, not on a second event after the move. `Reborn` is also why it has to be an argument at
  all — CR 400.7 gives the new object no memory of the old one, so a face-down permanent that
  changes zones is face up on the other side unless the move itself says otherwise.
- `tests/MtgEngine.Rules.Tests/ZoneChangeTests.Wave29.cs` — six tests: the object arrives face down;
  **the replacement system is offered an `ObjectMoved` whose `To.FaceDown` is already true** (the
  defect E9w filed) while its `Departed` is still face up (CR 603.10); what lands reads through the
  layer system as a 2/2 with no name, no subtypes and no mana cost (CR 708.2a); a move that does not
  ask for it arrives face up; a face-down permanent put into a graveyard is face up there (CR 400.7);
  and `faceIndex` still chooses the face underneath (CR 712.12 with 701.40b).

`dotnet format --verify-no-changes` clean on `MtgEngine.Rules` and `tests/MtgEngine.Rules.Tests`.

## Ask 1 — `MtgEngine.Rules/Engine/Seams.cs` (Engine/**): one default member on `IZoneSystem`

`ZoneSystem` already implements this exact signature, so the member binds to it with no other
change. Every other implementation (`Engine/Stubs.cs` and the seven test fakes) keeps compiling on
the default, which performs the ordinary move when nothing asks for face down and refuses rather
than silently ignoring the flag when something does. Add it directly after the `faceIndex` overload:

```csharp
    /// <summary>
    /// The same move putting the object down face down, CR 701.40a and CR 702.37c. A manifested
    /// card is turned face down <i>before</i> it is put onto the battlefield, and a spell cast face
    /// down enters the battlefield with the characteristics the spell had; CR 400.7 gives the new
    /// object no memory of the old one, so face-down status survives a zone change only when the
    /// move itself says it. Only then does an entering replacement (CR 614.12) or a "whenever a
    /// creature enters" trigger see the 2/2 with no name rather than the card's own face.
    /// </summary>
    [RuleRef("701.40a")]
    Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown) =>
        faceDown
            ? throw new NotImplementedException("Moving an object onto the battlefield face down is not built in this zone system.")
            : Move(state, what, to, where, tapped, controller, faceIndex);
```

## Ask 2 — `MtgEngine.Rules/Interpreter/**` (E9-series): the one line the manifest arm changes

`EffectInterpreter.Wave28.ManifestOne` moves the card and then turns it over, and its own doc
comment names this as the known gap. With ask 1 in, the whole arm is one call:

```csharp
    private EffectRun ManifestOne(EffectRun run, ObjectId id)
    {
        (run, _) = Relocate(run, id, Zone.Battlefield, Placement.Default, tapped: false, controller: null, faceDown: true);
        return run;
    }
```

— that is, the `Relocate` call gains `faceDown: true`; the trailing
`Emit(run, new ObjectChanged(arrived, FaceDown: true))` is deleted, and with it the landed-and-still-
there guard, which existed only to decide whether to emit that second event. The "Known gap" half of
the doc comment goes too. `Relocate` (`EffectInterpreter.Actions.cs:328`) carries it through as one more
optional parameter, `bool faceDown = false`, passed to `zones.Move(..., faceIndex: null, faceDown)`;
its twenty-odd other callers are untouched. CR 701.40f still holds for free — a move the zone system
did not perform lands nothing, so the card stays where it was and is not manifested.

## Recorded — morph's half is `Casting/**`, and it is the same argument (CR 702.37c)

Not an ask from me; noting it so the flag is not built twice. A face-down spell is already face
down **on the stack**, and CR 702.37c says it "enters the battlefield with the same characteristics
the spell had" — so CR 400.7 is exactly what breaks morph today, and the fix is
`CastingSystem.cs:303` (CR 608.3, "a permanent spell becomes a permanent as it resolves") passing
`faceDown: <the resolving stack object's FaceDown>` through the same overload rather than a literal.
That reads the flag off the spell instead of hardcoding it, so an ordinary permanent spell is
unaffected and a morph resolves face down without the casting system knowing what morph is.

## Not asked, not done

`IZoneSystem.MoveAll` and `MoveRequest` have no face-down flag. Manifest does not need one — CR
701.40e manifests several cards **one at a time**, which is what the interpreter's arm already
does — so nothing was widened on speculation. `ZoneSystem.Proposal` passes `faceDown: false` there
explicitly, so the day a batch needs it the compiler points at the one line.
