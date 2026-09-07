# E6d — the trigger carries the colour, reads the object's choices, matches the spell

**Nothing new is asked for.** The three seams E6c asked for landed before this box opened
(`GameObject.Chosen`, `StackEntry.Chosen`, `ManaChanged.SpentOn`, `IQuery.PlayerMatches`) and all
four are now used. No interface in `Engine/Seams.cs`, no event, no state record, nothing in
`MtgEngine.Cards` changed. What follows is what other folders now depend on, and one test that has
to change when its owner lands.

## The key both ends of "that color" agree on

`TriggerSystem.TappedForManaColor` is `"tapped-for-mana-color"`, the constant, so E9i reads the key
from the engine rather than retyping the string. The value is the colour's **enum name**
(`"Green"`), which `ManaSymbols.ColorFromChoice` parses; a test asserts the round trip.

Where it is written: `TriggerMatching.TappedFor` puts it on the `TriggerSubject`, `TriggerSystem`
merges it over the source object's `GameObject.Chosen` and writes the result to
`StackEntry.Chosen`, null when empty. So a stack object from a trigger now carries **both** what
its card chose earlier and what this firing produced, the firing's value winning on a clash. E9i's
read is `entry.Chosen` straight into the resolution context's `Chosen` — `TriggeredAbilityTests`
builds exactly that context and asserts the colour comes back out.

The state the object's choices are read from is the one the match ran against, which is CR 603.10's
look-back for a leaves-the-battlefield trigger: a permanent that chose a colour and then died still
answers with it.

## `IQuery.PlayerMatches` is now load-bearing for triggers (E0e)

`TriggerMatching.PlayerMatches` is deleted; `DealsDamage(ToPlayer: …)` goes through
`IQuery.PlayerMatches`. The interface default still throws, so **until E0e lands, a damage trigger
restricted to a player throws instead of answering**. No test in the rules suite reaches it — the
new test drives the arm through a fake query that answers — and the whole 660 are green, but the
merge order matters for a real game: E0e before any card with such a trigger is played.

Two follow-ons that are not mine to touch:

- `tests/MtgEngine.Rules.Tests/WaveTenSeamTests.cs` asserts `PlayerMatches` throws with "E0e" in
  the message. E0e has to rewrite that test when it implements the seam.
- `Sba/StateBasedActions.Permanents.cs` still has its own private `PlayerMatches` (E5e deletes it).
  Until it does, the two readings still disagree exactly as `E5d.md` reported — the trigger half is
  now the seam's, the state-based-action half is not.

## `ManaSpent.OnSpell`

`Ev.ManaChanged.SpentOn` is the object the payment was for; the filter is asked about it in the
ability's own context. A spend that names nothing — a pool emptying at end of step, a payment the
casting system did not attribute — fires nothing for a trigger that carries `OnSpell`, rather than
firing on every spend. A card writes the filter with `In: Zone.Stack`, because a spell is on the
stack and `Query` defaults a filter's zone to the battlefield; that is the same shape every other
spell filter uses, so nothing new is owed to the vocabulary.

Whoever fills `SpentOn` (E3h) should set it for an ability's source as well as a spell, which is
what CR 106.6a's "spent to activate an ability" needs; the arm reads whatever object is there.
