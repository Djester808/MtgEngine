# E6f — the zone a move left, CR 603.6, and a modal trigger's announcement, CR 603.3c

Both halves of the box are done inside `MtgEngine.Rules/Events/**`, `MtgEngine.Rules/Triggers/**` and
the trigger and event tests. Nothing below blocks either half; the two asks are one line each and
each closes a hole a card can already fall into.

## What changed

- `ObjectMoved` gained a defaulted `ZoneKey? FromZone`, and a derived, `[JsonIgnore]`
  `LeftFrom => FromZone ?? Departed?.Zone`: the zone the object left, or none when the mover did not
  say. `Apply` is untouched.
- `TriggerMatching.Moved` reads `From` and `NotFrom` for **every** zone from `LeftFrom`, keeping the
  battlefield's look-back through `TurnHistory.LeftBattlefield` (CR 603.10) — the record is what
  makes the departed object readable at all, so it still answers the battlefield and `LeftFrom`
  answers the rest. A move that recorded no source zone is refused with `RefusedException`/603.6
  **only when the trigger asks where the object came from**; one that names only a destination reads
  the event as it always did.
- `TriggerSystem.Create` calls `ModeAnnouncement.Announce(state, _query, [ability.Do], context, [])`
  (CR 603.3c with 700.2b) and folds the result into the entry's `Chosen` beside the firing's, so a
  modal triggered ability with a conditional ceiling carries `modes.max` for the interpreter's
  question. `Create` is no longer static (it needs the system's `IQuery`); an ordinary trigger's
  entry is byte-for-byte what it was.

## No seam is needed for the zone system

The brief expected one. The zone system's move sites already pass the object they move as
`Departed` — `ZoneSystem.Proposal` (`Departed: moving`) and `ZoneSystem.Draw` (`Departed: drawn`) —
and that object carries its own `ZoneKey`, so `LeftFrom` answers for every move the zone system
makes with **no change at either site**. The replacement system only ever rewrites `To`
(`ReplacementSystem.Applying.cs`), so the departed object's zone survives replacement. `FromZone`
exists for the movers below, which have the zone in hand but carry no departed object.

## Asks

1. **Stack / casting** — `MtgEngine.Rules/Stack/StackZone.cs`, `MoveSpellToStack` (line ~53):
   `return new ObjectMoved(card.Id, spell, -1);` → `..., Departed: card);`. `card` is the object in
   the zone it is cast from, so this is the same one-line shape the zone system already has. Until
   then a spell cast from a graveyard, a hand or exile records no source zone, so "whenever a
   creature card leaves your graveyard" is **refused** on a flashback-style cast rather than firing.
   (`FromZone: card.Zone` is the equivalent if a departed object is unwanted on a cast; `Departed` is
   preferred because CR 603.10's look-back wants it too.)
2. **Engine** — `MtgEngine.Rules/Engine/Game.cs`, `Manual` (line ~235): the honour-system
   `ManualMove` arm has `obj` in hand already; add `Departed: obj` to the `ObjectMoved` it builds.
   Without it a hand-moved object refuses every zone-change trigger that names a source zone.

Neither is reachable from a scenario the gate runs today, which is why the suite is green without
them; both are one line and neither needs a test of its own beyond the ones already written.

## Not done in this box

- **`FromZone` is a `ZoneKey`, not a `Zone`.** The trigger vocabulary compares zones (`ZoneChange.From`
  is a `Zone`), but the key is what every mover has and it says whose graveyard as well as which,
  which a later "leaves **your** graveyard" refinement will want. The matcher compares `.Zone`.
- **No lint or migration.** `FromZone` is defaulted and `LeftFrom` is `[JsonIgnore]`, so every stored
  log still reads and writes identically; `events.txt` lists type names only, so `ObjectMoved`'s line
  is unchanged. `EventLogTests.A_move_round_trips_the_zone_it_left` is the round-trip proof, and it
  asserts the derived property is not written into the log.
- **`Apply` still takes the source zone from the state**, not from `LeftFrom`. The object is always
  in the state when its own move is folded, so the state is the authority and nothing about
  `LeftBattlefield` moved.
