# E6e — the spend fires the delayed trigger, through the front door

No seam change is asked for. Everything below landed inside `MtgEngine.Rules/Triggers/**`.

## What EINT4 § 1 actually was

EINT4 read the game as "nothing fires a `ManaSpent` delayed trigger, **and** the ability is dropped
with the pool". Those are one defect, and it is not the matching.

The matching was already right: `TriggerMatching.SpentMana` matched the negative `"k#0"` block on
the spend's `ManaChanged`, `changed.SpentOn` satisfied `OnSpell`, and the ability **fired** — which
is why `GameState.DelayedTriggers` was empty afterwards. `TriggerFired.Apply` spends a delayed
trigger that fires (CR 603.7b), and it had fired. Nothing was dropped by the pool emptying and
nothing removes a delayed trigger it did not fire.

What was missing is one step later. `TriggerSystem.Create` stacked the firing with
`StackEntry.AbilityIndex = -1`, because a delayed ability is not one of the abilities its source
prints and no index into that source's list can name it (CR 603.7a). `EffectInterpreter.EffectOf`
reads `obj.Face.AbilityList[index]` and answers `null` for any index below zero, so the resolution
ran no tree at all and emitted only `StackObjectResolved`. The ability went on the stack, was spent,
and did nothing. That is why the draw never happened, and it was true of **every** delayed trigger
in the engine — a keyword-expanded "exile it at the beginning of the next end step" resolved to
nothing in exactly the same way.

## The fix

`TriggerSystem.Create` now gives a delayed firing's stack object a definition that carries the
ability: the face it resolves from with the `TriggeredAbility` appended, and the entry names that
appended position. Printed abilities keep the indexes they had, so nothing that reads the source's
list by index moves, and the source object's own definition is otherwise untouched — the stack
object still keeps its source's name and text (CR 602.2a). A delayed trigger whose source has left
the battlefield gets the same treatment on the empty definition, at index 0.

`TriggerMatching.SpentMana` grew a second arm for the key. The ordinary reading is unchanged and
exact: the resolution that produced the mana creates one delayed ability per single mana and names
that mana's own key, `"k#0"`. An ability whose linked mana was produced by another object (CR 106.6
lets the halves sit apart) has no per-mana key to name and is left holding the bare key the card
wrote; that ability now watches every single mana under it, and `"kish#0"` is still another key.

## What this flips

`WaveNineIntegrationTests.Mana_with_a_key_fires_its_delayed_trigger_when_it_pays_for_a_spell` is
EINT5's file and asserts the behaviour as it was. Three of its assertions are now the wrong way
round and the test fails on the first of them:

- `Assert.Equal(libraryBefore, …)` — the card **is** drawn now; it should be `libraryBefore - 1`.
- `Assert.Empty(table.State.DelayedTriggers)` still holds, but for the stated reason (CR 603.7b: it
  fired), not because the ability was dropped; the comment above it should go.
- the second half — the plain land's mana — is unchanged and still fires nothing.

I did not touch that file. The flip is EINT5's or the coordinator's.

## For E9j (wave 11, in parallel)

E9j has "1's firing" in the wave-10 decision table. There is nothing left to build there: the
interpreter's half (the keyed provenance block and one delayed ability per mana) already worked in a
real game, and the firing side is the `Create` change above. If E9j were to add a second channel —
the casting system handing the trigger system the keys a payment spent — the ability would fire
twice. Please read this first.
