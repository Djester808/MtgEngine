# E9w — wave 28: manifest built, and three findings (one of them corrects E9v)

Built here, `InterpreterTests` **134 green**: the **manifest** arm (CR 701.40 — the brief's
"CR 701.34" is Proliferate; 701.34 was read in `comprehensive-rules.txt` before citing).
`MtgEngine.Rules/Interpreter/EffectInterpreter.Wave28.cs` plus one dispatch case in
`EffectInterpreter.Actions.cs`; four tests in `tests/MtgEngine.Rules.Tests/InterpreterTests.Wave28.cs`.

## What morph shares with manifest (asked in the brief)

**There was no face-down road at all before this.** The only face-down verbs in the interpreter are
`TurnFaceUp`/`TurnFaceDown` (`EffectInterpreter.Faces.cs`), which turn a permanent that is *already
on the battlefield* over; nothing put a card onto the battlefield face down. What the two keyword
actions share is already built and is **not** duplicated by this arm:

- `Faces/FaceRules.FaceDown` — the 2/2 with no name, no subtypes and no mana cost. Its own comment
  names both CR 701.40a and CR 702.37b, and `Characteristics/Layers.cs` reads it off `obj.FaceDown`.
  Manifest sets the flag and nothing else; morph's face-down cast will set the same flag.
- `ZoneSystem`/`ReplacementSystem.EnteringAs` already knows `how.FaceDown` for an *entering
  replacement* that says "enters face down" (CR 614.12). That is a different road from a keyword
  action that manifests a card, but it is where morph's "cast face down" will land.

## Ask 1 (`MtgEngine.Rules/Zones/**`, E1-series) — a move that puts the object down face down

CR 701.40a turns the card face down **before** it is put onto the battlefield, so an entering
replacement and every "whenever a creature enters" trigger must see a face-down 2/2. `IZoneSystem.Move`
has no face-down flag and `ZoneSystem.Reborn` builds the arriving object face up (`FaceDown` defaults
to false; only `IsToken`, `IsCommander`, `Tapped` and `SummoningSick` are set). The interpreter's only
road is therefore **move, then turn over** — one `ObjectChanged(FaceDown: true)` after the
`ObjectMoved`. The resulting board is right and the card verifies; what watches the *entering itself*
sees the card's own face for that one event.

What closes it: the `int? faceIndex` overload's shape, one more optional argument —
`Move(..., int? faceIndex, bool faceDown = false)` reaching `Reborn`, exactly as `faceIndex` does for
CR 712.12. Then `ManifestOne` passes `faceDown: true` and deletes its second event. Morph's
face-down cast wants the same argument, so it is one change for two keyword actions.

## Ask 2 (`MtgEngine.Rules/State/**` with `Replacement/**`) — E8e ask 2, CR 614.5 across a halt

**Not built — the box ended first.** The half that is mine (the keys riding on the run and being
restored on resumption) cannot be written without the field they ride on, and that field is in
`State/**`. Confirmed from the code, not guessed:

- `EffectContext` (`Engine/Seams.cs:314`) has no member for it, and it is the only thing
  `ReplacementSystem` hands the interpreter (`ReplacementSystem.cs:211` `Context(...)`).
- `ReplacementSystem.Replace` builds a fresh `used` set per call, so a nested `Relocate` →
  `ZoneSystem.Replacing` → `Replace` inside a replacement's own `Then` starts empty and the same
  `ReplacementAbility` matches the modified event a second time. CR 614.5 says one opportunity per
  event **or any modified event that replaces it**.
- A halt makes it worse rather than causing it: the answer comes back through
  `EffectInterpreter.Resume`, and an instance field would be gone by then.

What closes it, in the order the layers allow: a `UsedReplacements` collection on `EffectContext`
(and whatever the pending resumption carries in `State/**`, beside `RememberedPrefix`), seeded into
`ReplacementSystem.Replace`'s `used` set and written back out with the outcome. `Replacement/**` and
`State/**` own both ends; `Interpreter/**` only has to thread it through `EffectRun` and restore it
on resumption, which is one wave-29 task once the field exists.

## Ask 3 (`cards/**`, P-series) — Walking Ballista `4b515bb0` is **not** the scenario's fault

The scenario is right: it casts with `x: 1` and A's mana pool holds 56 mana, so X=1 is paid for.
The road is wrong, and it is **not in `Interpreter/**`** — nothing in this folder is on the path.

`ReplacementSystem.cs:211 Context(source, controller, proposed)` passes `X: null` (the fifth
positional argument). The card's `EntersWithCounters { n: X }` is evaluated through
`_query.Evaluate(state, counters.N, Context(...))` (`ReplacementSystem.Applying.cs:59`), and
`Query.Amounts.cs:21` reads `X => context.X ?? 0` — "CR 107.3b: X is zero unless a value was chosen
for it". So the permanent enters with **zero** +1/+1 counters, is 0/0, and CR 704.5f puts it into the
graveyard. That is the whole of "InZone was Graveyard".

The chosen X is not lost — it is on the spell: `GameObject.Stack.X` (`State/GameObject.cs:76`), and
`ZoneSystem.Proposal` puts that very object on the event as `ObjectMoved.Departed`. **Folder
`MtgEngine.Rules/Replacement/**` (E8-series)**: when the proposed event is an `ObjectMoved` whose
`Departed` carries a `Stack` entry, `Context` must take its `X` (CR 601.2b with 107.3b — the value
chosen as the spell was cast is the value of X for the permanent's own replacement). One line, and it
is the whole class of "enters with X counters" cards, not one card.

## Finding — the interpreter **does** have a seeded random source (corrects E9v)

E9v filed "the interpreter has no seeded random source for a random discard — expose the scenario
seed's source (`IZoneSystem` or a small `IRandomness` seam)". **No seam is needed.** The seed is on
the state and the interpreter already holds the state:

- `GameState.Rng` (`State/GameState.cs:31`), a `RngState(Seed, Counter)` set from `GameStarted`
  (`Events/GameEvent.cs:53`), which the scenario runner seeds (`ScenarioRunner.cs:102`).
- `Engine/Rng.cs` is a pure `Below(RngState, exclusiveMax)` / `Draw` / `Shuffle`, and
  `RngAdvanced(RngState To)` (`Events/GameEvent.cs:465`) is the event that carries the advanced
  counter — exactly how `ZoneSystem.Shuffle` does it (`ZoneSystem.cs:220-227`).

So the random discard is a build in **this** folder and no ask at all: `Rng.Below(run.State.Rng, n)`
picks the card, `Emit(run, new RngAdvanced(next))` records the draw, and the discard proceeds without
a question. **Not built here** — the box ended. It is one arm in `EffectInterpreter.Actions.cs`:

- `Discard.What` (an `ObjectFilter`) is ignored today; the hand must be narrowed by it first.
- `Discard.By` (`DiscardChooser`) is ignored today; CR 701.9b is the rule — the default is the
  affected player's own choice, and a record that says otherwise means a random discard or another
  player choosing, neither of which is the owner's question the arm asks now.
- Gamble (`a54f0869`) is the reader, and its scenario needs E9v's `cards/**` regeneration either way.
