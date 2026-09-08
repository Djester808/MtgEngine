# E1e — a searched-library event (CR 701.23, the brief's 701.19)

Built in `MtgEngine.Rules/Events/LibraryEvents.cs` (a new file: one event, `LibrarySearched`),
tested in `tests/MtgEngine.Rules.Tests/EventLogTests.cs`
(`A_library_search_round_trips_and_folds_to_the_same_state`).

## Done here

`LibrarySearched(PlayerId Player, PlayerId Searcher)`: the record that a player searched a library.
`Player` owns the library, `Searcher` is the player performing the search and the player a
`Searches` trigger matches; they are the same on every printed search, but an effect can send a
player into another player's library, so neither is derived from the other. `Apply` returns the
state it was given — looking at a library changes nothing (CR 701.23a) — so the log line is the
whole of the event and a replay lands on the same state with or without it. The round-trip test
holds the serializer contract and asserts the identity fold (`Assert.Same`).

**The zone system does not search.** `ZoneSystem` has `Create`, `Move`, `MoveAll`, `Draw`,
`Shuffle`, `CreateToken` and the copy/enter/replace partials — no search road, and nothing in
`MtgEngine.Rules/Zones/**` scans a library for a card. So there is no site inside my box that can
emit this event, which makes the two asks below the whole of the wiring.

## Asks

1. **The events.txt line — a one-line edit somebody else has to make.** The brief grants
   `tests/MtgEngine.Rules.Tests/events.txt`, but the registered manifest's `owns` does not list it
   (`Event*.cs` does not match `events.txt`), so `agent-guard.js` refused the edit. Until the line
   lands, `EventLogTests.Every_event_type_is_listed_in_events_txt` fails — the new event type is in
   the assembly and not in the file. The edit, ordinally between `LandPlayed` and `LifeChanged`:

   ```
   LandPlayed
   LibrarySearched
   LifeChanged
   ```

2. **The interpreter's search arm must emit it (E9-series).** In
   `MtgEngine.Rules/Interpreter/EffectInterpreter.Actions.cs`, the `case Cards.Search search:` arm
   (currently around line 157): on the branch that **begins** the search — the one that computes
   `matching` and calls `Ask(run, searcher, "search", ...)`, not the `Answered(run) is { } found`
   resumption above it — write `new LibrarySearched(searcher, searcher)` before asking, once per
   library among `Searched(search)`. Two things the arm has to keep straight: the resumption branch
   must **not** write a second event (CR 701.23h: one instruction to search a library is one search,
   even when it is repeated before the shuffle), and the shuffle the arm does afterwards stays its
   own `ZoneReordered` (CR 701.23c is not this event). A search of a zone that is not a library
   writes nothing — the vocabulary's `Search.In` can name the graveyard or exile, and this event is
   about libraries only.

3. **For the E6-series `Searches` matcher (next wave).** Match `Searcher` against the trigger's
   `PlayerFilter Who`, and let `TriggeringPlayer` be `Searcher`. V13 gives `Searches` no zone field
   because every printed sentence of that shape is about the searcher's own library, so the matcher
   reads `Searcher` and ignores `Player` — it must not require `Player == Searcher`, or a card that
   sends a player into a library they do not own would silently stop firing. `Player` is on the event
   so a stored game says **which** library was searched; when a card that watches a search of
   somebody else's library is finally printed, the vocabulary's record gains the field and the event
   does not have to change.

## Note on the rule number

The brief, `V13.md` and `E6g.md` all cite **CR 701.19** for searching. In the shipped
`MtgEngine.Api/Knowledge/comprehensive-rules.txt`, 701.19 is **Regenerate**; **Search is 701.23**,
and 701.23f is the exact sentence this event exists for: an ability that triggers on a library being
searched will trigger. The `[RuleRef]` and the doc comments cite 701.23, matching the interpreter's
own search arm, which already says 701.23a. The same correction applies to the `Searches` record's
`RuleRef` in the vocabulary and to V13's row 1 if it was written as 701.19.
