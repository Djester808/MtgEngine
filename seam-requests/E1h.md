# E1h — wave 30: CR 608.3e, and Plaguecrafter is the scenario's defect and not the engine's

Owned: `MtgEngine.Rules/Zones/**`, `MtgEngine.Rules/Events/**`, `tests/MtgEngine.Rules.Tests/Zone*.cs`,
`tests/MtgEngine.Rules.Tests/Event*.cs`, `tests/MtgEngine.Rules.Tests/events.txt`. Landed:
`Zones/ZoneSystem.Resolving.cs` (new partial) and five tests in
`tests/MtgEngine.Rules.Tests/ZoneChangeTests.Wave30.cs` — anchor `ZoneChangeTests` **51**, up from
46. No event was added, so `events.txt` is unchanged.

## What was built (mine, no ask)

`ZoneSystem.ResolveOntoBattlefield(GameState, ObjectId what, PlayerId controller, bool faceDown = false)`
— the resolution of a permanent spell, CR 608.3a, followed by CR 608.3e when the move did not happen:
the card goes to its **owner's** graveyard (CR 400.3), and that move is offered to the replacement
effects like any other (CR 614.1) rather than forced.

Three edges the five tests pin, each of which the naive shape gets wrong:

- **A resolution that stopped to ask is returned as it is** (CR 616.1). A halted offer has decided
  nothing; sending the card to a graveyard there answers the question by guessing.
- **A refused graveyard move leaves the card where it was.** CR 608.3e says where the card goes, not
  that it must arrive.
- **An object that is no longer on the stack moves nothing.** There is no owner to read and CR 608.3e
  is about a controller who can't put a card onto the battlefield, not a card that stopped existing.

`faceDown` is the parameter E1g's "recorded" section wants for morph (CR 702.37c): a face-down spell
is already face down on the stack, and CR 400.7 drops the status unless the move carries it.

## Ask 1 — `MtgEngine.Rules/Engine/Seams.cs` (Engine/**): one default member on `IZoneSystem`

`ZoneSystem` already implements this exact signature, so the member binds to it with no other change.
Add it after the `faceDown` overload:

```csharp
    /// <summary>
    /// A resolving permanent spell becomes a permanent, CR 608.3a, and CR 608.3e when its
    /// controller can't put it onto the battlefield: that player puts it into its owner's
    /// graveyard. The replacement system can only refuse the entry — it does not know the move was
    /// a resolution and must not invent a second move — so only the subsystem that proposed the
    /// move can follow the refusal. The default is the entry alone, which is what every caller did
    /// before the rule was read.
    /// </summary>
    [RuleRef("608.3")]
    Outcome ResolveOntoBattlefield(GameState state, ObjectId what, PlayerId controller, bool faceDown = false) =>
        Move(state, what, ZoneKey.Of(Zone.Battlefield, controller), Placement.Default, false, controller, null, faceDown);
```

The default is deliberately **not** the whole rule. A test fake whose `Move` emits no `ObjectMoved`
at all would read every entry as refused and send the object to a graveyard, which is a worse
failure than the one being closed; the seven fakes keep the behaviour they have, and the real
`ZoneSystem` — the one every game plays through — carries the rule.

## Ask 2 — `MtgEngine.Rules/Casting/**` (E3-series): the one line that reaches it

`CastingSystem.ResolveTop` (`Casting/CastingSystem.cs:152`) is the permanent-spell arm:

```csharp
            var moved = zones.Move(state, top, ZoneKey.Of(CardZone.Battlefield, controller), controller: controller);
```

becomes

```csharp
            var moved = zones.ResolveOntoBattlefield(state, top, controller);
```

Nothing else in the arm changes shape: `NewIdOf(moved.Events, top)` already reads the id off the
events, and a spell that went to a graveyard instead has an `ObjectMoved` whose `To` is the card in
the graveyard, so the `SummoningSick` `ObjectChanged` that follows is applied to a graveyard object.
**That is the one thing the join has to look at** — if the arm should skip the `sick` event when the
permanent did not enter, it is a two-line change there and not mine to make. Today the arm emits it
against an object that never entered either way, so the join does not make anything worse.

Morph's half (`CastingSystem.cs:303`, E1g's recorded note) is the same call gaining
`faceDown: <the resolving stack object's FaceDown>`; the parameter is there waiting for it.

## 3. Filed, not fixed — Plaguecrafter `2e3ee458` is the scenario's defect (CR 701.21a)

Reproduced at the wave-29 tip from this worktree:

```
FAILED  Plaguecrafter (2e3ee458-...) 0/1: smoke-...: InZone was Graveyard
```

**What moved, and why the engine is right.** The smoke scenario gives A an empty battlefield and
Plaguecrafter in hand, and B an empty battlefield; the only nonland cards anywhere are the one
Plaguecrafter and six Scenario Filler Lands. Plaguecrafter enters, its trigger resolves, and "each
player sacrifices a creature or planeswalker of their choice" is a requirement, not a choice to
decline. CR 701.21a: a player can't sacrifice something that isn't a permanent they control — so the
one permanent A controls that the filter admits **is Plaguecrafter itself**. A sacrifices it and it
is in the graveyard; B controls none, can't, and discards (the trigger's `else` arm). The scenario
then asserts `InZone Plaguecrafter Battlefield`, which the card's own text makes impossible on this
board. `expect` is wrong, not the play.

**Not the simultaneity.** The brief's guess was that `MoveAll` and the simultaneous sacrifice were
the road (CR 101.4's APNAP order, then the actions happen at once; CR 608.2c for the order written —
the brief's CR 701.16 is Investigate, and sacrifice is **CR 701.21**). Simultaneity cannot change
this outcome: B controls no creature or planeswalker whether A's sacrifice happened first or beside
it, and A's only legal choice is fixed by the board. Nothing was changed in `MoveAll` on that guess.

**Ask (P-series).** The smoke generator must not assert the cast card is on the battlefield when the
card's own resolution can move it — this trigger's filter matches its own source. Either the
expectation is dropped for such a card, or the setup gives each player a second creature so the
sacrifice has somewhere else to go, which is also the board that would exercise the trigger properly.
A generated `InZone ... Battlefield` on a self-sacrificing permanent is a false failure, and the
verify count has been carrying it since slice 010.

## Not done in this box

- **No mutation run.** The graveyard test asserts an `ObjectMoved` into the owner's graveyard that
  the plain `Move` road cannot emit under the refusing fake (it returns an outcome with no events at
  all), so it cannot pass without the new arm — but it was not watched failing first.
- **The join is unverified end to end.** Nothing calls `ResolveOntoBattlefield` until ask 2 lands, so
  no corpus card exercises CR 608.3e yet. What is proved is the zone system's five behaviours.
- **`MoveAll` is untouched.** It was the brief's road to Plaguecrafter and Plaguecrafter turned out
  not to need it; widening it on speculation would be a change no test could justify.
