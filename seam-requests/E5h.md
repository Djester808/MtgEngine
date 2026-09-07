# E5h — the query reads V9: `IsOneOf` lands, and `AttachedTo` was already the right way round

## 1. `AttachedTo` did not need flipping (for the coordinator, P18 and the next V pass)

`Query.Predicates.cs` has read `AttachedTo` candidate-first since E0 (wave 1). The arm is

```csharp
AttachedTo to => obj.AttachedTo is { } host && Resolve(state, to.To, ctx).Contains(host),
```

— `obj` is the candidate, `obj.AttachedTo` is what the candidate hangs on, and the question is
whether that host is among the objects `To` names. That is the doc comment's direction ("Auras
attached to it"). E0g (`d1a4b52`, wave 13) pinned it with two tests and changed only the comment;
its commit message says "no behaviour changed". V9's sentence "they verify only because
`Query.Predicates.cs:37` implements the inverted direction" and the wave-16 brief's item 2 describe
an inversion that is not there. Nothing was flipped in this box; a third test now pins the
direction without `Self` in the picture (`Attached_to_is_candidate_first_whichever_selector_names_the_host`),
and a fourth pins `IsOneOf(Attached)` and `AttachedTo(Self)` against each other as the two ends
of one link.

**What that means for the two Verified cards** (`65986c1b-8e51-…`, `d79cbc61-6c15-…`), which spell
"whenever equipped creature …" as `AttachedTo(Self)` inside the trigger filter: the engine has
been reading that as "an Aura or Equipment hanging on this Equipment" all along, so those triggers
have never fired for the equipped creature. They verify because their smoke scenarios never make
the equipped creature die, attack or deal damage and assert the firing, not because the engine
read the spelling their way. V9's ask 2 expects them to *fail* verify after this wave and pass
after P18's canonical rewrite; they will not fail — this box changed nothing they read — and they
will not play correctly until P18 respells the trigger filter as `IsOneOf(Attached)`. A scenario
that equips and then kills the creature would show the gap before the rewrite and the fix after;
the generator does not write one today (P16's area, not mine). `cards/` untouched.

**Item 3 of the brief — other readers of the predicate**: there are none. Every other
`AttachedTo` in `MtgEngine.Rules` is the `GameObject.AttachedTo` state field, the
`ObjectChanged.AttachedTo` event field, or the view's copy of it (`Events/GameEvent.cs:163,176`,
`Stack/StackZone.cs:50`, `Views/GameView.cs:49`, `Engine/Game.cs:235`, `State/GameObject.cs:36`).
The interpreter, the trigger system and the casting system reach the predicate only through
`IQuery.Matches`, so nothing outside the query relied on either direction. No seam change.

## 2. `IsOneOf` reads; `IsRemembered` had no arm at all (for the record)

- `IsOneOf(Of)` is `Resolve(state, Of, ctx).Contains(candidate)`: the selector is resolved in the
  context the filter is evaluated in. `TriggerMatching` hands every filter the ability's own
  context (source = the Equipment, CR 603.2 doc there), and a `Dies` trigger's state is the one
  the caller rebuilt before the object left (CR 603.10), so `Attached` still names the departed
  creature under its old id there and `IsOneOf(Attached)` admits it. The trigger road is not
  tested from this box (`Triggers/**` is not mine); `QueryTests.IsOneOf.cs` pins the query half:
  the equipped creature and nothing else, three shapes of nothing (unattached source, no source,
  unwritten key) that admit nothing without refusing, and `IsOneOf(Triggering)` for a delayed
  trigger's "that creature". A `Choose` with no key inside `IsOneOf` refuses exactly as the
  `Choose` selector does (the choice has not been made, CR 608.2).
- `IsRemembered(key)` (V8, wave 13) had never been given an arm: `grep IsRemembered
  MtgEngine.Rules` found nothing, so every card that used it fell to the query's "no arm in the
  query layer" refusal. V9's "matches nothing, as `IsRemembered` does" described an arm that did
  not exist. It is added beside `IsOneOf` (CR 603.10) with a test that `IsRemembered(key)` and
  `IsOneOf(Remembered(key))` admit the same objects and that a key no step wrote admits nothing
  rather than refusing — off a resolution there is no memory, and a static filter that reads one
  describes nothing.

## 3. Nothing asked of a seam

No decision needed. The one thing owed elsewhere is in section 1: the coordinator's verify
expectation for the two `AttachedTo(Self)` cards (fail now, pass after P18) should read "verify
either way today; play correctly only after P18".
