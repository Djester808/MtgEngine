# E6h — the monarch is CR 725, not CR 724, everywhere the engine says so

Wave 25. The trigger side is done inside `MtgEngine.Rules/Triggers/**`: `Searches` matches
`LibrarySearched` (CR 701.23f), `SparedByTheirDrawStep` reads the per-step draw record
(CR 121.1 with CR 504.1) and the batch walk is gone, and `BecomesMonarch` matches
`MonarchChanged`. **One ask**, and it is a citation, not a behaviour.

## 1. Every monarch citation is off by one (rules data, many folders)

The brief said to read the rule number from `MtgEngine.Api/Knowledge/comprehensive-rules.txt`
rather than trust it, and that is exactly the E1e case again. In the shipped text:

```
723. Controlling Another Player
724. Ending Turns and Phases
725. The Monarch
726. The Initiative
```

`724` is the end-the-turn rule (Time Stop). The monarch is **725**, and the pieces the engine
already spells out are:

- **725.1** — the designation exists once an effect instructs a player to become the monarch.
- **725.2** — the monarch's two inherent triggered abilities, with no source: the end-step draw and
  "whenever a creature deals combat damage to the monarch, its controller becomes the monarch".
  Both of those are one rule, not the `724.3`/`724.4` the code cites — the shipped text has no
  724.4 for combat damage at all.
- **725.3** — only one player is the monarch at a time; the current one ceases to be as another
  becomes it.

`TriggerMatching.Wave25.cs` cites 725.1 and 725.3. Everything else still says 724, so the two
halves of the same rule now disagree in the log's own doc comments. The corrections, by owner:

| File | Today | Should be |
|---|---|---|
| `MtgEngine.Cards/Triggers.cs:277` `BecomesMonarch` | `[RuleRef("724")]` | `725` |
| `MtgEngine.Cards/Triggers.cs:280` `TakesInitiative` | `[RuleRef("725")]` | `726` (The Initiative) |
| `MtgEngine.Cards/Conditions.cs:161`, `Effects.Verbs.cs:227` and `:287`, `Players.cs:61` | `[RuleRef("724")]` | `725` |
| `MtgEngine.Rules/Events/GameEvent.cs:219-220` `MonarchChanged` | `724.1` | `725.1` |
| `MtgEngine.Rules/State/GameState.cs:44` `Monarch` | `[RuleRef("724")]` | `725.1` |
| `MtgEngine.Rules/Query/Query.Conditions.cs:86` `YouAreMonarch` | `// CR 724.2` | `725.1` |
| `MtgEngine.Rules/Query/Query.Selectors.cs:131` `Monarch` reference | `// CR 724.2` | `725.3` (at most one) |
| `MtgEngine.Rules/Interpreter/EffectInterpreter.Backlog.cs:70,75` `BecomeMonarch` | `724.1` | `725.1`, and the skip is `725.3` |
| `MtgEngine.Rules/Turn/TurnSystem.Wave24.cs:8,14,19,30` the end-step draw | `724.3` | `725.2` |
| `seam-requests/E9r.md`, and the `724.4` combat-damage line wherever it was copied | `724.3`/`724.4` | `725.2` for both halves |

Nothing about the behaviour changes; every one of these is a `[RuleRef]` or a comment. It is worth
doing in one pass rather than a file at a time, because the value of a citation is that a reader can
open the document at it, and a reader who opens 724 finds Time Stop.

## 2. Not a seam — recorded so the next reader does not go looking

- **`Searches` reads `Searcher` and ignores `Player`**, exactly as E1e § 3 asked. The arm is
  `TriggerMatching.Wave25.cs`'s `Searched`, and a test sends seat 1 into seat 0's library and
  asserts the firing is still seat 1's. `Searches.Who` is a `PlayerFilter`, so the arm answers it
  with `IQuery.PlayerMatches` and not `IQuery.Players`; no new seam member.
- **The window carries the state after the whole batch.** `TriggerSystem.Collect` builds every
  `TriggerWindow` from the state it was handed, which is post-batch, so
  `window.State.Player(p).ObjectsDrawnThisStep` has the draw being matched folded in and the
  comparison is `== 1` — E2c ask 2 asked which of `== 1` and `== 0` it is, and it is `== 1`. The
  draws the batch took *after* this event come back off, the same subtraction
  `TriggerWindow.DrawsSoFar` already makes for the turn's count; without it a batch that draws two
  cards reads 2 for both of them and spares neither.
- **No `Events/**` change was needed for either arm.** `LibrarySearched` and `MonarchChanged` both
  exist, are both in `events.txt`, and both fold correctly; the trigger system only had no arm.
