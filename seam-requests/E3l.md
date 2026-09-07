# E3l — the casting system reads `ChooseMode.MaxIf` (CR 700.2, 601.2b)

The casting half is done in `MtgEngine.Rules/Casting/ModeAnnouncement.cs` and is wired into both
roads the casting system owns: a cast spell (`CastingSystem.Instructed.cs`, `CastCore`, CR 601.2b)
and an activated ability (`CastingSystem.cs`, `Activate`, CR 602.2b). Three things are outside
`Casting/**` and are asked for here; nothing below blocks the casting half.

## What the casting system now does

- `ModeAnnouncement.Announce(state, query, bodies, context, modes)` finds the spell's or ability's
  `ChooseMode` (at the root of the text or among a `Sequence`'s steps), reads the ceiling once —
  `MaxIfMet` when `MaxIf` holds in the announcing player's context, else `Max` (CR 700.2 with
  601.2b) — and holds the announced modes to it: too many or too few, or an index that names no
  mode, is refused citing 700.2; the same mode twice without `SameModeTwice` citing 700.2d. A
  command that names no modes is not refused (the resolution's question still asks, as today).
- When the ceiling was conditional, the number it came to rides on the stack entry as
  `StackEntry.Chosen["modes.max"]` (`ModeAnnouncement.CeilingKey`). Nothing is recorded for an
  ordinary modal spell, so its entry is what it always was.
- The condition is read at the announcement and never again: `ResolveTop` hands the interpreter
  the entry as announced (`CastingTests.Modes.cs` proves the query is asked exactly once across a
  cast and its resolution).

## Asks

1. **E9-series (interpreter)** — `EffectInterpreter.Structure.cs`, the `ChooseMode` arm (line ~114):
   when no `"modes"` answer is in the context and it asks the mode question, take the ceiling from
   `run.Context.Chosen[ModeAnnouncement.CeilingKey]` when that key is present (parse with
   `CultureInfo.InvariantCulture`) and from `modal.Max` otherwise. `ContextFor` already folds
   `entry.Chosen` into the context, so no other change is needed. When `"modes"` is present the arm
   runs them as it does today, without re-reading `Max` or `MaxIf` — that is already the case and
   is the behaviour V10 asked for; do not add a re-check there.
2. **E-series (trigger system)** — `TriggerSystem.cs`, `Create` (line ~317), CR 603.3c with 700.2b:
   a triggered ability's modes are chosen as it is put on the stack, so the ceiling is read there.
   Call `ModeAnnouncement.Announce(state, query, [ability.Do], context, [])` with the trigger's
   controller as the context's controller and fold the result into the entry's `Chosen` beside
   `firing.Chosen` (the casting system's `Carried` helper shows the merge; `Announce` returns null
   when nothing was conditional). The trigger system has no channel for announced modes, so the
   empty list is right: the ceiling is recorded and the interpreter's question (ask 1) offers it.
3. **P-series / coordinator (card file)** — `cards/fd949f82-fc10-4e37-8aa9-6c7569fe3c55.json` is
   still spelled as an `If` around two `ChooseMode`s. `ModeAnnouncement.ModalIn` does not look
   through an `If` (a branch resolution decides, CR 608.2c, is not the announcement's to count), so
   that file keeps today's behaviour — the condition read at resolution — until it is redone with
   `MaxIf`/`MaxIfMet` as V10 says. Re-verify it after the redo.

## Not asked

- A generic `EffectWalk.ModalIn` was considered and not requested: the bulleted list CR 700.2
  describes is the ability's own text or a step of it, which two shapes cover; a walk that found
  a modal under `If`, `May`, `ForEach` or `Repeat` would count modes a branch may never run.
- No lint that a command's modes are announced at all (an empty list is left to the resolution's
  question, as before this wave).
