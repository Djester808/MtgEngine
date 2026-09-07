# E1b — the zone system proposes its events, and the two overloads it now carries

Everything below is a line in a file E1b does not own. The behaviour is built and tested on the
class today (`ZoneReplacementTests`, 11 tests); each request is the line that lets the rest of
the engine reach it through `IZoneSystem` rather than through a concrete `ZoneSystem`.

## 1. `Subsystems.Wave2()` sets the zone system's replacement handle — CR 614.1c, 616.1

This is the EINT2 defect: `IReplacementSystem.Replace` was called for damage and nothing else, so
`WouldEnterBattlefield`, `WouldDie`, `WouldBePutIntoZone`, `WouldBeExiled`, `WouldDraw`,
`WouldCreateTokens` and the rest were dead code at a table.

`ZoneSystem` now has a settable `IReplacementSystem? Replacements` (`Zones/ZoneSystem.Replacing.cs`),
the same shape as `ForwardingInterpreter` and for the same reason: the zones are built before the
replacement system, which is built with the interpreter, which moves cards through the zones. Null
means "no replacement system in this game", which is exactly right for `Wave1()` — leave that
composition alone.

In `Engine/Game.cs`, `Subsystems.Wave2()`, immediately after `handle.Target = interpreter;`
(line 70), add one line:

```csharp
        // CR 614.1c and 616.1: every zone change, draw and token creation is offered to the
        // replacement effects in force before it happens. The zones are built first, so the
        // handle is set here, the same way the interpreter's is.
        zones.Replacements = replacement;
```

`zones` is already `var zones = new Zones.ZoneSystem();` on line 62, so nothing else moves. With
that line, `WaveTwoIntegrationTests.AssertEnteringTappedIsTheRule` becomes a played land rather
than a direct call on the replacement system, and `EntersTapped` / `EntersWithCounters` /
`EntersAs` / "exile it instead" / "if you would draw" reach a real game.

**What it changes for callers.** Nothing about the signatures. `CastingSystem.PlayLand`,
`CastingSystem.ResolveTop`, `EffectInterpreter.Actions`, `ChoiceSystem.Resume`, the SBAs and the
turn system all keep calling `Move`/`MoveAll`/`Draw`/`CreateToken`; what comes back is now what
actually happens. Three consequences those callers should know about, all tested:

- A move can produce **no events at all** (prevented) — an `Outcome` with an empty `Events` is a
  legitimate answer, not a failure.
- A move can produce a **`ChoiceRequested`** and leave the object where it was (CR 616.1). A
  caller that assumed "after `Move` the object is in `to`" has to check. `MoveAll` and a
  multi-card `Draw` stop the rest of the batch at that point.
- A move can land the object in a **different zone** than the one asked for ("exile it instead"),
  so read the destination off the returned `ObjectMoved`, not off the request.

## 2. `IZoneSystem.Move` gains `int? faceIndex` — CR 712.12 (E3b's request)

Built as an overload with all seven parameters positional:

```csharp
// MtgEngine.Rules/Zones/ZoneSystem.cs, today
public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex)
```

It cannot carry `= null` while the six-parameter `Move` still exists: two overloads that differ
only in a trailing optional parameter make `Move(state, what, to)` ambiguous (CS0121). So the
seam change is a **replacement**, not an addition. In `Engine/Seams.cs`:

```csharp
    Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default,
                 bool tapped = false, PlayerId? controller = null, int? faceIndex = null);
```

and in `ZoneSystem.cs`, delete the six-parameter overload (it is a one-line delegate) and put the
defaults on the seven-parameter one. Every existing call site is unchanged, because they all pass
six arguments or fewer. `null` means "the face the object already had"; an index the card does not
have throws `ArgumentOutOfRangeException`, which is a composition bug rather than a rules
situation (`ZoneReplacementTests.A_face_index_the_card_does_not_have_is_refused`).

With that in place E3b's `PlayLand.FaceIndex` half can land, and
`FaceCastingTests.A_land_face_of_a_modal_card_is_not_cast` becomes the play.

## 3. `IZoneSystem.CreateToken` taking `CopiableValues` — CR 707.2, 111.4 (E9b's request)

Built on the class exactly as E9b asked; no ambiguity, because the second parameter's type
differs from `TokenSpec`. The interface line to add in `Engine/Seams.cs`:

```csharp
    /// <summary>A token that is a copy of something, CR 707.2 and 111.4: it has the copiable values and nothing else.</summary>
    [RuleRef("707.2")]
    Outcome CreateToken(GameState state, Faces.CopiableValues values, PlayerId controller, bool tapped, bool attacking);
```

(`Faces` is `MtgEngine.Rules.Faces`; `Engine/Seams.cs` has no `using` for it today.) Once the line
is on the seam, `EffectInterpreter.Faces.cs`'s copy-token arm can replace its own
`CopyRules.NewCopy` + `ObjectCreated` with the call, and the two spellings of "a token arrives"
become one. Both overloads now share the same arrival path, so a copy token is offered to the
replacement system and can be put onto the battlefield attacking (CR 506.3c) like any other.

## 4. Noted, not requested: `EffectInterpreter.Propose` never sets `Halted`

`Interpreter/EffectInterpreter.cs:248` — `Propose` calls `Merge`, which copies events and state
but leaves `Halted` alone, so the `if (run.Halted) return run;` guards after each `Propose` in
`EffectInterpreter.Verbs.cs` (lines 38 and 45) can never fire. A damage replacement that asks
(CR 616.1) therefore raises the question and the interpreter walks on into the next target. The
zone system reads `state.Pending` instead, which is the signal that survives a replay. E9c's file,
not E1b's; the one-line shape would be

```csharp
    private EffectRun Propose(EffectRun run, GameEvent proposed, PlayerId affected)
    {
        var merged = Merge(run, replacements.Replace(run.State, proposed, affected));
        return merged.State.Pending is null ? merged : merged with { Halted = true };
    }
```
