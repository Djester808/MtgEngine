# E9x — wave 29: the discard, the token's size and the per-type edict; two asks left open

Anchor `InterpreterTests`: **143 green** (134 before), nine new tests in
`tests/MtgEngine.Rules.Tests/InterpreterTests.Wave29.cs`. Engine changes in
`MtgEngine.Rules/Interpreter/EffectInterpreter.Wave29.cs` (new) and three dispatch edits in
`EffectInterpreter.Actions.cs`. `dotnet format --verify-no-changes` clean on both projects.
No seam moved: nothing outside `Interpreter/**` was touched and no interface changed.

## Done — item 2, CR 701.9a/b: `Discard.What` and `DiscardChooser` are read

The old arm ignored both fields. Three behaviours, one arm (`Discarding`):

- **`What` narrows the hand before the count is taken.** The filter is read with
  `what with { In = Zone.Hand }` — `Query.cs:26` defaults a filter with no zone to the
  **battlefield**, so a translated "discard a land card" whose filter names no zone would have
  matched nothing at all. CR 701.9a: a discarded card is in a hand by definition, so the hand is
  the zone the filter is read in. A hand holding none of the kind discards nothing and asks
  nothing (CR 101.4), rather than taking whatever was there.
- **`By: Random` never asks.** `Rng.Below(run.State.Rng, pool.Count)` picks the card and
  `RngAdvanced(next)` is emitted before it moves, exactly as `ZoneSystem.Shuffle` does, so the
  log replays the same discard. E9w's finding was right and no seam was needed. Two tests: one
  that no question is raised and the advance is logged, one that the same seed takes the same
  card twice.
- **`By: Controller` asks a different seat and empties the discarding player's hand.** The
  progress through the players now rides on the question as a **loop mark** (`Mark`/`Marked`)
  instead of being recovered with `discarding.IndexOf(answer.Player)`: with another player
  choosing, the seat that answers is not the seat that discards, and the old line restarted the
  loop at the wrong player (CR 101.4). The owner-chooser road is unchanged in behaviour.

## Done — item 3, CR 111.3 and CR 701.21

- **`TokenSpec.PowerAmount`/`ToughnessAmount`** are evaluated as the token is created and written
  into the spec handed to `IZoneSystem.CreateToken` (`Sized`), so the numbers are the token's own
  copiable text (CR 111.3) — not a layer-7b `SetPt` a copy would not carry, a second setting would
  overwrite, and CR 704.5f would collect a 0/0 in the window before. A spec with printed numbers
  and no amounts is passed through untouched.
- **`Sacrifice.KeepOnePerPermanentType`** (`KeepingOnePerType`): every named player chooses
  before anything is sacrificed, and the sacrifices then happen at once (CR 101.4) — the kept ids
  ride on the question in the loop mark, and a test asserts the first seat's answer leaves the
  board untouched while the second seat chooses. The count asked for is a **maximum**, computed
  as how many of CR 110.4's six permanent types the player's permanents cover between them, never
  a fixed six and never an exact number: an artifact creature answers two types at once
  (CR 205.2b) so a legal kept set can be smaller.

## Ask 1 (`MtgEngine.Rules/Engine/Seams.cs` and `Replacement/**`) — CR 614.5 across a halt

**Not built. Both ends of this are outside `Interpreter/**` and the interpreter half cannot be
written first**, which corrects E9w ask 2's expectation that the field would land in `State/**`:

- `EffectContext` is in `Engine/Seams.cs:314`, not `State/**`. It has no member for the used keys
  and it is the only thing `ReplacementSystem.Applying` hands the interpreter.
- `ReplacementSystem.Replace` (`ReplacementSystem.cs:37`) builds `new HashSet<string>(...)` per
  call and `Continue` is private, so there is no way in from this folder.
- The interpreter *can* derive the key on its own — `Run(state, effect, context, abilityIndex)`
  with a `ReplacementAbility` at that index is exactly the `$"o{id}:a{index}"` that
  `Applicable` (`ReplacementSystem.cs:126`) skips — but it has nowhere to put it: `Propose` calls
  `replacements.Replace(state, proposed, affected)` and the signature takes no seed. Carrying the
  keys on `EffectRun` alone would be a field nothing reads, so it is not committed here.

Two lines close it, in this order:

1. `Engine/Seams.cs`: one optional parameter on the seam and one field on the context —
   `Outcome Replace(GameState state, GameEvent proposed, PlayerId affected, IEnumerable<string>? used = null);`
   and `ImmutableHashSet<string>? UsedReplacements = null` appended last to `EffectContext`.
2. `Replacement/**`: `Replace` seeds its `HashSet` from `used`, and
   `ReplacementSystem.Applying.Instead` puts the key it is applying under into the
   `EffectContext` it builds for the interpreter (it already knows the key — `Continue` adds it to
   `used` at `ReplacementSystem.cs:79` immediately before `Apply`).

Then `Interpreter/**` is one wave-30 task and I will take it: carry `Context.UsedReplacements` on
the `EffectRun`, write the keys into the question's `ChoiceContext.Extra` under a `used:` prefix
beside `RememberedPrefix` (`EffectInterpreter.Resume.cs:31`), restore them in `Bind`/`Outside`, and
pass them from `Propose`. Mox Diamond (`f3c5978a`) is the reader: the may-pay is paid, the `Then`
is `Move(Self → Battlefield)`, and the same `ReplacementAbility` matches the new `ObjectMoved` with
an empty `used` set and asks again, once per land, until the hand is empty.

## Ask 2 (`Choices/**` or `Engine/**`) — a card-name choice with no printed options, CR 201.3

**Not investigated inside the box** — the four items did not fit thirty minutes and this one was
last. Petrified Hamlet (`78a2972c`) refuses with *"a choice of CardName has nothing to choose
from: the effect names no options and the game defines no set for it (601.2b)"*. What the choice
road needs is stated here so wave 30 does not have to re-derive it: the set of legal answers to a
name-a-card choice is **every card name the game knows** (CR 201.3 — a card's name is what is
printed on it), which is the definition store, and no subsystem in `Interpreter/**` can reach one:
the interpreter holds `GameState`, and `GameState` holds `GameObject.Definition` for objects that
exist, never a catalogue of names. Either the choice system gains a name source, or the arm is
handed the names it may offer. Whoever takes it should say which, because the "offer the names"
arm in `Interpreter/**` is small once the source exists.

## Not run — Gamble (`a54f0869`), P27's open question

P27 asked the next agent to run the one scenario in-process and say what moved. **I did not get to
it**, so P27's `ZoneCount was 0` is still unexplained and I am not claiming otherwise. What this
wave changes about it, and what the next agent should check first: P27 reasoned from the arm
computing `count = Math.Min(hand.Count, Number(run, discard.N))` with `whenMissing: 1`, which is
still true, **but Gamble's discard is a random one and the old arm ignored `By` entirely** — it
asked the owner a question. If the scenario's first-legal answer took a card the harness then
counted differently, the count and not the arm was the reader. With this wave the discard takes one
card off the seeded generator and never asks, so the play is a different one; re-run
`verify cards --only a54f0869-…` against this tip before diagnosing anything further.
