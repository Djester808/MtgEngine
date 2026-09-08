# E9y — wave 30: the used keys across the halt, the typed name choice, the permission's identity

Anchor `InterpreterTests`: seven new tests (five in
`tests/MtgEngine.Rules.Tests/InterpreterTests.Wave30.cs`, and one existing test re-pointed), plus
five in `tests/MtgEngine.Rules.Tests/StateRecordTests.Wave30.cs` for the state shape. Engine
changes in `MtgEngine.Rules/Interpreter/EffectInterpreter.Wave30.cs` (new), three edits in
`EffectInterpreter.cs`, one in `EffectInterpreter.Resume.cs`, one in
`EffectInterpreter.Structure.cs` and two in `MtgEngine.Rules/State/GameState.cs`. No seam moved:
nothing outside `Interpreter/**` and `State/**` was touched and no interface changed.

## Done — item 1, CR 614.5 across a halt (E9x ask 1, the interpreter half)

`Propose` now offers every event through
`replacements.Replace(state, proposed, affected, run.Context.UsedReplacements)`, and the keys get
there three ways:

- **Seeded at the door.** `Run(state, effect, context, abilityIndex)` calls `Seeded`: when the
  context's source holds a `ReplacementAbility` at that index, the walk *is* that effect's `With`
  (CR 614.1a), the effect has therefore already applied to the event, and its key —
  `o{source}:a{index}`, the same string `ReplacementSystem.Applicable` skips by — goes on the
  context. A triggered ability's body at the same index seeds nothing; CR 614.5 is about
  replacement effects only.
- **Carried over the halt.** `Ask` writes each key into the question's `ChoiceContext.Extra` under
  `used:` (so `used:o12:a0`), beside `RememberedPrefix`, and `Bind` puts them back through
  `Restored`. Both resumption roads pass through `Bind` — `Continue` for a question raised inside a
  resolution and `Elsewhere` for one raised outside — so the off-stack "instead" is covered without
  a second reader in `Outside`.
- **Passed on the re-proposal.** The resumed walk's `Propose` carries them, so the "instead" no
  longer matches the event it itself made. Mox Diamond's may-pay cranks once rather than once per
  land in hand.

### One thing a V- or E8-series box should still close

`ReplacementSystem.Context(...)` (`ReplacementSystem.cs:228`) does **not** set
`EffectContext.UsedReplacements`, so the authoritative key never reaches the interpreter and the
interpreter derives its own. That derivation is exact for the shape the corpus prints and can drift
in two places, both outside `Interpreter/**`:

1. `Applicable` keys on the index inside `AbilitiesOf(probe, obj)`, which for a **battlefield**
   object is `_layers.Of(state, obj.Id).Abilities` (CR 613, a granted replacement counts), while
   `IndexOf` — which supplies the `abilityIndex` I derive from — uses `obj.Face.AbilityList`. A
   printed-only object indexes the same either way; a granted ability ahead of the printed one
   would not.
2. `SourceFor` hands the walk `moved.From` when the arriving id no longer exists (CR 614.12), while
   the candidate's key was built from the probe's `moved.To.Id`. For an object that never leaves the
   stack these are the same id, which is Mox Diamond's case.

**The one-line close**: in `ReplacementSystem.Applying`, the `Instead`/`Choosing` arms already know
the key (`Continue` adds `tier[0].Key` to `used` at `ReplacementSystem.cs:79` immediately before
`Apply`); thread it into the `Context(...)` they build as `UsedReplacements`. The interpreter's own
seeding is a union, not an override, so it keeps working unchanged and the two agree.

## Done — item 2, CR 201.3 with 201.2 and 205.2a: the typed name choice (V18 row 8, E9x ask 2)

`ValueOptions` gained a `ChooseWhat.CardName` arm. With no printed `Options` the set offered is the
names of the definitions the **state** holds — every object in every zone, one entry per distinct
name, ordinal-sorted so the offer is the same on a replayed log. `ChooseValue.OfType` narrows it by
the *named card's* type line (CR 205.2a), never by anything on the board. A face-down object is not
offered: CR 708.2 leaves it with no name to name. Petrified Hamlet (`78a2972c`) has something to
choose from now; **unverified** through the runner — the corpus is not reachable from this worktree
(`MtgEngine.Cards.Pipeline verify` found no `oracle_cards.json` here).

`InterpreterTests.A_choice_with_no_set_and_no_options_is_refused_rather_than_asked` was re-pointed
from `CardName` to `ChooseWhat.Word`. The rule it protects is unchanged and still asserted — a
question with an empty option list is refused, never asked (CR 101.4) — but a card name is no
longer an example of a kind with no set, and `Word` is: nothing in the rules bounds "choose a word".

### Ask (a V-series default member and one line in `Engine/Game.cs`) — the wider name catalogue

The state's names are the widest set **this folder** can reach, and they are narrower than CR 201.3:
a name no object in the game currently carries cannot be named, which is wrong for "name a card"
on an empty board. `IDefinitionStore` has `ByName`/`ByOracleId` only, so it cannot be enumerated.
Not built against, as briefed. What would close it, exactly:

1. **`IDefinitionStore` gains a default member** (V-series):
   `IEnumerable<CardDefinition> All => throw new NotSupportedException("This definition store cannot be enumerated (CR 201.3).");`
   — a default so no existing store breaks, and a throw rather than an empty sequence so a store
   that cannot enumerate is a refusal with a rule on it and never a silently empty question.
2. **`Engine/Game.cs` hands the interpreter the store**, one assignment where the interpreter is
   constructed: `new EffectInterpreter(query, zones, replacements, characteristics, definitions)`,
   with the parameter `IDefinitionStore? definitions = null` appended last so every other
   construction — the tests' `InterpreterTable` included — is unchanged.

Then the arm is small and stays where it is: `Names` prefers `definitions.All` when a store is
present and falls back to the state's objects when it is absent or refuses enumeration. Whoever
takes it should decide whether the catalogue is the whole store or the cards in the game's decks
(CR 201.3 says every card; a four-player Commander store of 25,000 names is a question no client
can render as a list, so the choice road may want a typed answer rather than an option list — that
is a `Choices/**` call, not this folder's).

## Done — item 3, CR 116.2a: the permission's identity on the records (E3r ask 3, the state half)

Two records in `MtgEngine.Rules/State/GameState.cs`. **The names are exact, for E3s's next wave:**

- `SpellCastRecord(ObjectId Spell, PlayerId By, Zone From, int? Permission = null)` — `Permission`
  is the `ActiveEffect.Id` of the effect that allowed the cast (a `MayPlayFromZone`), none for an
  ordinary cast. Appended with a default, so every existing construction compiles unchanged.
- `LandPlayRecord(ObjectId Land, PlayerId By, Zone From, int? Permission = null)`, collected on
  `TurnHistory.LandsPlayed` (`ImmutableList<LandPlayRecord>`), an `init` property beside
  `TokensCreated` rather than a positional field for the same reason.

Why `int?` and not a string: `ActiveEffect.Id` is the only identity a continuous effect has, it is
already what `AppliesTo`/`Until` are keyed against, and a once-per-turn permission is one
`ActiveEffect`. Two permissions over one zone are now told apart, and a land played under one is
countable — neither was true before, because a land play is not a cast and `SpellsCast` never held
it.

**Two halves are not mine and are not built** (the fold is the reducer's, the read is E3s's):

1. **The fold.** Whatever event records a land play must add a `LandPlayRecord` to
   `TurnHistory.LandsPlayed`, and the casting system must fill `SpellCastRecord.Permission` when a
   `MayPlayFromZone` is what allowed the cast. Check while doing it that `TurnBegan` clears the
   history with `TurnHistory.Empty` rather than a `with`, or `LandsPlayed` will survive the turn.
2. **The read.** The once-per-turn limit counts `SpellsCast` and `LandsPlayed` together, each
   filtered by `Permission == thisEffect.Id`, and never by `From` alone.

## Not done in this box

- **Item 4, Frantic Search (`16e015b2`).** The corpus is not reachable from this worktree, so the
  per-card verify could not be run and I am not guessing at which question halted it: **unverified**.
  What the next agent should check first, and why this wave may have moved it: the failure is
  `InZone was Stack`, which means the spell never left the stack, which means a question was raised
  and not answered or a step refused. The card is draw two, discard two, untap up to three lands.
  The discard now reads `What` and `By` (E9x), so the arm that asks is a different one from the one
  P-series last measured; and if the untap step's "up to three" raised a question inside a walk that
  had already proposed an event, this wave changed what that question carries. Run
  `verify cards --only 16e015b2-…` at the wave-30 tip before diagnosing further, and read the
  pending question's `Kind` and `Context.EffectPath` from the run rather than the arm.
