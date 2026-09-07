# E6c — "whenever a permanent is tapped for mana"

`TappedForMana` and `ManaSpent` now have arms in `TriggerMatching`, both reading the same channel:
the `ManaChanged` event's `Delta.Provenance`, a positive block for a production (CR 106.12a) and a
negative block of the same key for a spend (CR 106.6a). Nothing else in the engine had to change.
Three things the folder does not own are owed, in the order they matter.

## 1. Nothing stores the colour a card chose, so `OfChosenColorKey` cannot be answered end to end

`ChooseValue(ChooseWhat.Color, key)` writes into the resolution's own `EffectContext.Chosen`
(`EffectInterpreter.Structure.cs`), and that context is gone the moment the resolution ends. Nothing
in `GameState`, `GameObject` or `ActiveEffect` keeps it. `IsChosenColor` and
`Devotion.ChosenColorKey` read `ctx.Chosen` too, so they have the same hole — they simply have not
been asked outside a resolution yet.

A trigger is asked outside one. `TriggerSystem.Context` builds the ability's context from the source
and the controller alone, with `Chosen` empty, so "whenever a land you control is tapped for mana of
the chosen color" cannot fire even when the card has chosen. The arm reads `ctx.Chosen` and is
correct; what it needs is something to read.

**Asked for**: a per-object store the choices a resolution made are kept in, so an object's later
abilities read what that object chose — `ImmutableDictionary<string, string> Chosen` on `GameObject`
beside `Designations`, folded by the event that answers a `ChooseValue` whose source is a permanent,
and seeded into the context by every subsystem that builds one for an object's ability
(`TriggerSystem.Context`, the layer system's, the query's). That is a `State/` record and an
`Events/` fold, so it is not mine.

Until it lands the arm is honest rather than wrong: a key with nothing stored answers "no colour" and
the trigger does **not** fire, because a card that has not chosen has no chosen colour to be tapped
for; firing for every colour would be a different card. The behaviour is covered by
`TriggeredAbilityTests.ATriggerRestrictedToTheChosenColourFiresForThatColourOnly`, which calls
`TriggerMatching` directly with a context that carries the choice — the only way to reach the arm
today, and the test that will keep passing unchanged once the store exists.

## 2. The mana produced is not on the trigger's context, so the effect cannot add "that color"

Brief §2. The triggering object is the permanent that was tapped, and that lands: the arm's
`TriggerSubject` is `block.Source`, so `TriggerSystem` remembers it under `Triggering` and puts it on
the stack object's `StackEntry.Triggering`. "That land" resolves.

**"That color" does not.** The other half of the printed line is "add an additional one mana of that
color", which the vocabulary spells `AddMana(ManaProduction.ChosenColorKey …)` — and the colour that
was produced is on the `ManaChanged` block, not in the context the stack object carries. `EffectContext`
has `Remembered` (objects), `RememberedPlayers`, `Stored` (ints) and `Chosen` (strings); a colour is a
string, so **`Chosen` is the channel with no new field needed** — what is missing is somebody writing
it. `StackEntry` would have to carry the chosen strings alongside `Remembered` for the interpreter to
read them back.

**Asked for**, and it is the same seam as §1 from the other end: `StackEntry.Chosen`
(`ImmutableDictionary<string, string>?`), set by `TriggerSystem.Create` from the firing, and seeded
into the resolution's context by the interpreter the way `Remembered` already is. Then the trigger
arm can put the produced colour in under a fixed key and `ManaProduction.ChosenColorKey` reads it.
I have not invented the key name, because both ends of it belong to other folders — E3g writes the
provenance, E9h reads the effect. Naming it here so they agree: **`"tapped-for-mana-color"`**, the
colour's enum name (`Green`), which is what `ManaSymbols.ColorFromChoice` already parses.

## 3. `ManaSpent.OnSpell` has nothing to match on (E3g and E9h: the channel, agreed)

Brief §3. The arm is written and the channel is the one the brief named:

- **Production**: a `ManaChanged` whose `Delta.Provenance` holds a block with `Amount > 0`, `Source`
  the permanent whose mana ability resolved, `Key` the `AddMana.Key` the instruction gave it.
- **Spend**: a `ManaChanged` whose `Delta.Provenance` holds a block with the **same `Key` and a
  negative `Amount`**. `ManaPool.FoldProvenance` already matches blocks on `(Color, Source, Key)` and
  drops them at zero, so the spend needs no new fold. `RestrictedPool.DeltaFor` does exactly this for
  the restricted blocks already; the provenance half is E3g's to emit from `ManaSymbols.Pay`.
- **One firing per producing permanent**, not one per mana. CR 106.6a says a separate delayed ability
  is created for each mana produced, so where a card wants one firing per mana the *effect* creates
  that many `DelayedTrigger`s; the matcher must not multiply them again.
- **Subject**: the permanent that produced the mana, and the player whose pool it left. Mana is not
  an object (CR 106.1), so there is nothing else to point at.

**Not answered**: `ManaSpent.OnSpell`. `ManaChanged(PlayerId, ManaPool, string Reason)` names no
spell — `Reason` is free text an engine must not parse. A `ManaSpent` carrying `OnSpell` therefore
does **not** fire, rather than firing on every spend. **Asked for**: `ObjectId? SpentOn` on
`ManaChanged`, set by the casting system where a payment is for a spell or an ability's source. That
is an `Events/` change, so E3g or the casting builder owns it; the arm is one added clause when it
lands.

## Nothing else

No interface in `Engine/Seams.cs`, no event, no state record changed. `TriggerMatching.cs` gained the
two arms and the colour reader; `TriggeredAbilityTests.cs` gained four tests.
