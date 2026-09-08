# E9q — attaching (CR 701.3), and the refusals that replaced the crashes

Wave 22. Owned: `MtgEngine.Rules/Interpreter/**`, `tests/MtgEngine.Rules.Tests/InterpreterTests*.cs`.

## 1. `Attach` and `AttachAction` are one meaning — keep `AttachAction`

`MtgEngine.Cards/Effects.Verbs.cs:234` and `MtgEngine.Cards/Effects.Actions.cs:8` are the same
record, to the parameter names, with the same `[RuleRef("701.3")]`:

```
public sealed record Attach(Selector What, Selector To) : Effect;        // Effects.Verbs.cs
public sealed record AttachAction(Selector What, Selector To) : Effect;  // Effects.Actions.cs
```

There is no second reading hiding between them. CR 701.3 has one keyword action, "attach", and it
does one thing — take an Aura, Equipment or Fortification from where it is and put it onto the
named object — so two records for it is a spelling the translators can disagree about for no gain,
which is what a canonical rule exists to stop.

**Asked of the linter: keep `AttachAction`, refuse `Attach`, and add the canonical rewrite
`Attach → AttachAction`.** `AttachAction` is the one in `Effects.Actions.cs`, whose header says it
holds "the keyword actions of CR 701, one record each, in rule order"; attaching is a keyword
action, not a verb the vocabulary invented, so the file that says so is where it belongs. The
`-Action` suffix is also the file's existing answer to a name that would collide with something
else (`Cards.Attach` reads as an attachment, not as the act).

Until that lands **both records reach one arm** — `EffectInterpreter.Actions.cs` dispatches each to
`Attaching` — so no card is blocked either way and the rewrite is a pipeline change with no engine
change behind it. `cards/3dc364f3-3094-4660-80ca-9418588c7fde.json` (the card that exposed this)
spells it `Attach`, so the rewrite has at least one file to move.

## 2. What the arm does, and what it deliberately does not

`MtgEngine.Rules/Interpreter/EffectInterpreter.Attach.cs`. It emits the same
`ObjectChanged(id, AttachedTo: host)` the casting system writes at `CastingSystem.cs:156` when an
Aura spell resolves attached to what it was cast at (CR 303.4f), so there is one stored attachment
and one reader of it.

Legality, CR 701.3b — an attachment the rules do not allow does nothing, and the walk carries on:

| Kind of permanent | Legal host | Rule |
|---|---|---|
| Equipment | a creature | CR 301.5c |
| Fortification | a land | CR 301.6b |
| Aura | what its `Enchant` filter admits (none: anything; `OfPlayer`: no object) | CR 303.4, 702.5b |
| anything else | nothing — it is not one of CR 701.3a's three | CR 701.3a |

Both permanents must be on the battlefield, and nothing is attached to itself.

**Not this arm's**, and not built here: an Equipment whose host stops being a creature falling off
is CR 704.5n's, in `StateBasedActions.Permanents.cs`, which already has it, as it has CR 704.5m for
an Aura. Attaching to a *player* is not in CR 701.3 (the rule attaches to an object); an Aura
reaches a player through the casting system's `AttachedToPlayer`, and no card in the corpus asks an
effect to attach one there.

**One duplicated reading, offered rather than taken.** `Enchantable` here and
`StateBasedActions.AttachedLegally` / `HostIsLegal` ask CR 702.5b and CR 301.5c the same question of
the same two objects, from two files that cannot see each other's privates. That is two copies of a
rule, which is the shape wave 10's E5d seam request called out. **Asked for: one public reading —
`IQuery.CanAttach(state, what, host, context)` or an `Attachment` static beside `FaceRules`** — and
then both sites call it. Not taken in this box: the seam is not mine and CR 704.5n's arm is not
mine, so a half-done move would leave the copies *and* a new indirection.

## 3. Every `NotImplementedException` under `Interpreter/**` is now a refusal

None is thrown from the interpreter any more. What each site refuses, and with which rule:

| Site | Refuses | Rule cited |
|---|---|---|
| `EffectInterpreter.cs:236` (dispatch fallback) | "There is no interpreter arm for *Record*." — the vocabulary record's type name, which is engine data, never a card's | 608.2 |
| `EffectInterpreter.Faces.cs:116` (melding) | a melded permanent needs `GameObject.Components`: CR 701.42a makes it one object represented by two cards, CR 712.4b unmelds them when it leaves — the seam is asked for in `seam-requests/E9b.md` | 701.42a |
| `EffectInterpreter.Retarget.cs:230` (`Copy`) | unchanged in what it refuses ("A copy of that card is not this card being cast"); only what it *catches* changed | 707.12 |

`InterpreterTests.cs`'s `An_effect_with_no_arm_names_the_record_it_could_not_run` asserted the old
`NotImplementedException` and now asserts the refusal and its rule.

### The one thing the catch could not narrow — asked of whoever owns `Engine/Seams.cs`

The brief asked the `Copy` catch to catch the refusal *instead of* the
`NotImplementedException`. It catches **both**, because the throw is not under `Interpreter/**`:

```
MtgEngine.Rules/Engine/Seams.cs:65-66
    Outcome CopyInPlace(GameState state, ObjectId original, PlayerId owner) =>
        throw new NotImplementedException("CopyInPlace is not built in this zone system: wave 10.");
```

That default interface implementation is what a zone system that has not built the seam still
throws. Narrowing the catch to `RefusedException` alone would turn a copy-cast against such a zone
system into a crash out of `Game.Apply` — exactly the outcome this task exists to remove — so the
catch is `when (thrown is RefusedException or NotImplementedException)` and the interpreter refuses
either way.

**Asked for: make `Seams.cs`'s unbuilt defaults throw `RefusedException` naming the seam**, not
`NotImplementedException`; then this catch narrows to the refusal in one line.
`tests/MtgEngine.Rules.Tests/WaveTenSeamTests.cs:62-64` and `ArchitectureTests.cs:57` assert the
current exception type on three of those defaults (`PlayerMatches`, `PrintedOf`, `CopyInPlace`,
`Draw`), so the change is one edit in `Seams.cs` and those assertions — neither file is mine.

## 4. Verify road — not run in this box

`verify cards --only 3dc364f3-3094-4660-80ca-9418588c7fde` was **not run**. The box's rules forbid
verify and corpus loading inside an agent worktree, and the pipeline builds against `MtgEngine.Rules`,
so a run from the main checkout would exercise the pre-merge engine and prove nothing about this
change. **Owed by the coordinator after the merge**: `verify cards --only
3dc364f3-3094-4660-80ca-9418588c7fde`, then `git checkout HEAD -- cards/*.json cards/.lint`. The
card is Mithril Coat: an Equipment whose enters-trigger attaches it to a targeted legendary
creature, which is the arm's straight line.
