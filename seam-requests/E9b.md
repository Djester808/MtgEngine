# E9b — interpreter resumption and the face verbs

Everything below is a change outside `MtgEngine.Rules/Interpreter/**`. Each one names the exact
shape wanted and what the interpreter does today without it.

## 1. `IZoneSystem.CreateToken` taking `CopiableValues` (E15's open request, E1's file)

`CreateToken(GameState, TokenSpec, PlayerId, bool tapped, bool attacking)` cannot express a token
that is a copy of a permanent, because such a token has no `TokenSpec` — it has the copiable
values of the thing it copies (CR 707.2, 111.4). The overload wanted:

```csharp
Outcome CreateToken(GameState state, Faces.CopiableValues values, PlayerId controller, bool tapped, bool attacking);
```

**Today** `EffectInterpreter.Faces.cs` builds the object itself with `CopyRules.NewCopy` and emits
`ObjectCreated`, which is correct as far as the object goes (the test asserts the definition, the
face that is up, no counters and no damage) but bypasses whatever `ZoneSystem.CreateToken` does
about a permanent entering the battlefield. Two spellings of "a token arrives" is the drift the
plan forbids, so this should become one call as soon as E1 can take it.

The same overload closes `CreateToken`'s `bool attacking` for copies: `GameObject` has no
attacking flag, so a copy token created attacking is created untapped and not attacking today.

## 2. `GameObject.Components` for a melded permanent (state; declined in wave 2 "for now")

CR 701.42a: a melded permanent is a single object represented by two cards, and CR 712.4b unmelds
them when it leaves the battlefield; CR 712.8g computes its mana value from both front faces.
`GameObject` holds one `CardDefinition`, so there is nowhere to put the second card.

**Today** the `Meld` arm calls `FaceRules.Meld`, does nothing when the two cards are not a meld
pair (CR 701.42c — tested), and throws `NotImplementedException` naming this seam when they are.
Smallest shape that closes it: `ImmutableList<ObjectId> Components { get; init; } = []` on
`GameObject`, set by the event that melds and read by CR 712.4b and 712.8g.

## 3. A `Flip` effect record in `MtgEngine.Cards` (vocabulary)

`Effects.Actions.cs` has `Transform` and `Convert` but no `Flip`; `FlipCoin` is a different thing.
CR 710.2's flip is a distinct game action from CR 701.27a's transform — one-way (CR 710.4), and it
reads a `FaceRole.Flipped` face rather than a back face.

**Today** the `Transform` arm dispatches on the definition's `Layout`: a `Layout.Flip` card goes
through `FaceRules.Flip` and emits `TurnKind.Flip`, everything else through `FaceRules.Transform`.
That is data-driven and card-name-free, and it is tested, but a translator reading "flip it" has to
emit `Transform` for it to work, which reads wrong. `public sealed record Flip(Selector What) :
Effect;` with the name-table entry and CR 710 would fix the vocabulary; the arm is one line.

## 4. A question asked outside a resolution cannot be resumed

`Resume` finds the effect again through the stack: the stack object whose entry names the source
and ability the `ChoiceContext` recorded, then a walk of that ability's tree back to the recorded
path. An effect run through `IInterpreter.Run` — a replacement's `With`, a delayed trigger's body,
a scenario step — has no stack object, so if it asks a question there is nothing to find and
`Resume` throws `InvalidOperationException` naming CR 101.4.

No card in the free slice reaches it, and closing it means putting the effect itself (or a handle
to it) somewhere the state can hold, which is a state change rather than an interpreter one. The
options, smallest first: give `ChoiceContext` a `DelayedTriggerId`/`ActiveEffectId` so the runner
can be found again; or let `GameState` hold the pending effect run's source effect. Filed so the
shape is decided before a card needs it rather than after.

## 5. A verb that halts part-way through its own loop re-runs its earlier steps

`DealDamage` proposes one `DamageDealt` per target and `Discard` asks one player at a time. The
resume locator is a path in the effect tree, and both of those loops sit at one path, so resuming
lands at the top of the loop. `Discard` is closed here — the answer carries the player who
answered, and the walk continues with the players after them — but a `DealDamage` whose second
proposal raises a CR 616.1 replacement-order question would, on resume, damage the first target
again.

Nothing raises that today: only `IReplacementSystem.Replace` can halt inside those loops and E8b's
`Resume` is not built. The general fix is a per-node progress marker in the `ChoiceContext`'s
`Extra` (an index the loop starts from), which is the interpreter's to write once there is a
second `Resume` implementation to agree with. Flagging it so E8b and E9b agree on the spelling
rather than inventing two.

## 6. `CopyRules.Become` has no caller

CR 707.2's "a permanent becomes a copy of another" is `BecomesCopyOf`, a `ContinuousEffect`, so it
is layer work (E7) and the interpreter only creates the effect through `Apply`. `Become` is
therefore unused until `Layers` applies `BecomesCopyOf` in layer 1. Not a request, just a note so
nobody adds a second reading of it in an interpreter arm.

## 7. The one line that turns this on: `Subsystems.Wave2()`

`MtgEngine.Rules/Engine/Game.cs` line 79 still reads

```csharp
var choices = new MtgEngine.Rules.Choices.ChoiceSystem(zones, turn);
```

with the comment above it saying wave 3 (E9b) passes the interpreter. It now implements
`IResumable`, so that line becomes `new ChoiceSystem(zones, turn, interpreter)` and the comment
goes. `Game.cs` is not E9b's file, so it is left as it is: until the coordinator changes it, a
card's question still reaches `UnbuiltResumable` through `Game.Apply` even though
`EffectInterpreter.Resume` is built and tested.
