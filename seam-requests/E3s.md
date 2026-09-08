# E3s — morph's face-down half, the target range, and what is still owed on Flusterstorm

Wave 30. Owns `MtgEngine.Rules/Casting/**`, `MtgEngine.Rules/Keywords/**`,
`tests/MtgEngine.Rules.Tests/Casting*.cs`, `tests/MtgEngine.Rules.Tests/Keyword*.cs`. Anchor
`Casting|Keyword`: **196 green**, up from 190 at the wave-29 tip; six new tests in
`tests/MtgEngine.Rules.Tests/CastingTests.Wave30.cs`. `MtgEngine.Rules` and
`tests/MtgEngine.Rules.Tests` build clean with 0 warnings.

## 1. Done — CR 702.37c: a face-down spell resolves into a face-down permanent

E1g's recorded item, taken as recorded. `CastingSystem.ResolveTop` reads the resolving stack
object's own `FaceDown` and passes it to the wave-29 `IZoneSystem.Move` overload:

```csharp
var moved = obj.FaceDown
    ? zones.Move(state, top, battlefield, Placement.Default, false, controller, null, faceDown: true)
    : zones.Move(state, top, battlefield, controller: controller);
```

- The **branch is deliberate and not a single call with a variable flag.** The eight-argument
  default member delegates to the seven-argument one when `faceDown` is false, and that one is
  `NotImplementedException` on every zone-system fake in the test project — the first shape of this
  change broke two unrelated casting tests with "Moving with a chosen face is not built in this zone
  system". A face-up spell now takes exactly the call it always took, so nothing about CR 608.3's
  ordinary resolution moved.
- The flag is read off the spell, so the casting system still knows nothing about morph: CR 702.37c
  says the permanent "enters the battlefield with the same characteristics the spell had", and
  CR 400.7 gives the new object no memory of the old, which is why the move has to carry it.
- A null face index keeps the face the object shows; the face-down face is the layer system's
  (`Layers.cs:107` reads `FaceRules.FaceDown`), not the move's.

**Not asked, not done.** There is still no road that *casts* a card face down — CR 702.37c's "turn
it face down and announce that you're using a morph ability", with `{3}` as an alternative cost.
`CastPlan` has no morph arm and `KeywordKind.Morph` is still in `KeywordSemantics.Unsupported.cs`.
The test therefore turns the spell face down on the stack with an `ObjectChanged` after an ordinary
cast, which is exactly the state a morph cast would leave. Until the cast road exists, morph is
**unverified through a real game**; this half is proved by two unit tests only.

## 2. Done — CR 601.2c: `Target.Min` is the low end of an announced range

V18 row 1. `CastingSystem.Wave30.cs` adds `RequireTargetRange`, called from `CastCore` beside the
other announcement checks, so it is asked on **both** roads (an instruction to cast, CR 608.2g,
still announces targets).

- The slots are walked exactly the way `EffectInterpreter.RememberTargets` fills them — the
  announced list read in `EffectWalk.TargetsIn` order, each selector taking what its count works out
  to now, the **last taking everything left**. That last rule is what makes a range announceable at
  all: the last selector's size *is* the number the player announced, which is CR 601.2c's "the
  player announces how many targets they will choose before they announce those targets".
- **Only a selector carrying a `Min` is checked.** A bare `Target` is every stored card's shape and
  nothing in `Casting/**` counted one before; reading a bare `Count` as a required number here would
  start refusing casts the engine has always allowed. A test pins that (`A_target_without_a_range_is
  _not_counted`).
- Both ends refuse with **601.2c**: fewer than `Min` ("that spell needs more targets than were
  announced" — choosing zero is `UpTo`, which L049 refuses beside a range), more than `Count`
  ("takes fewer targets than were announced"). Neither message carries a number, so nothing here
  needs a culture.

**Ask 1 (State/** or Interpreter/**), CR 601.2c — a range on a selector that is not the last one
cannot be announced.** The flat `CastSpell.Targets` list has no slot boundaries in it, so the
interpreter's layout gives every non-last selector exactly its `Count` and the last one the
remainder. A card printing "one or two target creatures **and** target land" therefore cannot say
which of the announced objects were the range's — the range would have to be the last selector to be
readable. My reader mirrors the layout rather than inventing a second one, so it is exact for the
one-range-selector shape (every card the corpus prints today) and silent for the other. The fix is a
per-slot announcement — target counts on the command, or a `Chosen` entry keyed by
`Query.TargetSlot(n)` written at announcement — which is `State/**`'s record and the interpreter's
reader, not mine. Narrow: nothing in the corpus asks for it yet.

**Ask 2 (same owners), CR 115.4 — an `AnyTarget` that took a player shortens the object list.**
The same walk counts object slots only, so a tree with an `AnyTarget` before a ranged `Target` mis-
aligns when the "any target" was a player (the choice rides in `TargetPlayers`). This is
`RememberTargets`' existing imprecision, not one this file adds; recorded so the per-slot
announcement above closes both at once.

## 3. Flusterstorm (`86bf58f2`) — not diagnosed; the box ran out

Item 3 of my brief is **not done**. I read the card file and the stored scenario but the box expired
before the run, so I have no measurement and will not guess at one.

What the stored scenario is, for whoever picks it up:

| | |
|---|---|
| steps | B casts a filler bolt targeting **B**; A casts Flusterstorm targeting the bolt; resolve all |
| expect | the bolt and Flusterstorm both in graveyards, A's graveyard 1, **B at 20**, stack empty |
| stored verification | `runId verify-20260908T083154-f39c64`, passed 0 of 1, engine `0f74a03de66e7d38` |

So the failure "Life was 19" is the bolt **resolving** and dealing its damage — the counter did not
happen — and the board does cast an earlier spell, so the storm count in this scenario is one other
spell and the trigger should make exactly **one** copy. The three questions the brief asks (does the
trigger fire on the cast at all; how many copies; does each copy ask its own unless) each need the
run, and each has a different owner if the answer is no: the trigger firing is `Triggers/**`, the
copy count is `Interpreter/**` (`EffectInterpreter.Faces.cs`, where the wave-29 clamp change landed),
and the per-copy unless is `Interpreter/**`'s `Unless` arm. The one thing that is certainly mine —
the storm expansion itself — is unchanged from E3r and its four keyword tests are green.

The command to run from a worktree root with the corpus reachable:

```
dotnet run --project MtgEngine.Cards.Pipeline --no-build -- verify cards --only 86bf58f2-7f25-4e10-b797-25e0e8e67769
```

## 4. Not reached — E3r ask 3's casting read

The permission identity on `SpellCastRecord` waits on E9y's state fields, as the brief said. Nothing
in `Casting/**` changed for it.

## Not done in this box

- **No `dotnet format --verify-no-changes` run** on either project. Both build clean with analyzers
  and code style enforced in-build (0 warnings), but the format gate itself was not run — the box
  expired first. Two files are new (`MtgEngine.Rules/Casting/CastingSystem.Wave30.cs`,
  `tests/MtgEngine.Rules.Tests/CastingTests.Wave30.cs`); both were written with the Write tool, CRLF,
  no BOM.
- **The full Rules test project was not run**, only the `Casting|Keyword` anchor filter (196 of the
  1235). Nothing outside `Casting/**` was touched, and the one cross-cutting risk — `ResolveTop`'s
  move — is why the face-up branch was kept identical.
- **Flusterstorm**, above.
