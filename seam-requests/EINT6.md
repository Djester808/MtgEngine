# EINT6 — the wave-13 and wave-14 joins through Game.Apply

Anchor `WaveFourteenIntegrationTests`, files `tests/MtgEngine.Rules.Tests/WaveFourteenIntegrationTests.cs`
and `WaveFourteenIntegrationTests.Fixture.cs`. Five games, one test each, every one played through
`Game.Apply` on `Subsystems.Wave2()` with invented cards (`Wave Fourteen …`), and every game ends
with its log written, read back and replayed to the same state. No engine edits.

## Result: five green, no gap found

| Game | Rule | What the front door proved |
|---|---|---|
| 1. "target player loses 2 life and you gain 2 life" cast at the opponent, then "target player loses 1 life, then that player loses 1 life" | CR 601.2c, 608.2d, 608.2h, 119.3 | the announced player rides on the stack object; the resolution reads it back under the selector's key (18 / 22), and a later step's `RememberedPlayer(key)` names the same player, not the caster (16 / 22). The wave-13 E0g join holds. |
| 2. "exile target creature, then its controller draws a card" at the opponent's creature | CR 400.7, 608.2h | the exiled card is a new object and the targeted id is gone; the opponent's library falls by one and their hand rises by one, the caster's library is unchanged. The wave-13 E9l join (`ControllerOf` reads last-known information) holds. |
| 3. "enchant creature" Aura announced at a land, then at a creature | CR 601.2c, 702.5b, 303.4f, 704.5m | refused at announcement naming 601.2c with the card still in hand, the stack empty and the mana unspent; at the creature it resolves attached to it and to no player, and is still attached a turn later. The wave-13 E5g join holds. |
| 4. a land with "{T}: Add {C}", "as this enters, choose a creature type" (its second ability) and "whenever a creature of the chosen type enters under your control, you gain 1 life" | CR 614.1c, 614.12, 101.4, 608.2d, 603.2 | the question is the controller's, the land is on the battlefield while it stands, the question carries the replacement's index (1) and offers the creature types; the answer is written on the permanent; a Goblin entering fires the trigger (21 life) and an Ox does not (still 21). The wave-14 E8d join holds, and the trigger system's `ChosenFor` seeds the source's choice so `IsChosenType` reads it. |
| 5. "{T}: Add one mana of any color. When that mana is spent to cast a spell, draw a card" activated with "U" on a table of green lands | CR 106.6a, 608.2d, 603.7, 603.7a | after every resolution the pool holds one blue under `key#0` and a delayed ability exists; the blue pays for a {U} creature, the delayed ability fires and the card is drawn; plain green carries no key. Whether the ability used the stack is not asserted (E3j). |

The Rules project alone is green with the five added.

## Observations, not defects (no owner asked to act)

- **The keyed player is re-derived, not written.** `StackEntry.RememberedPlayers` stays null on a
  cast spell; `EffectInterpreter.ContextFor` walks the effect tree at every resolution and seeds the
  key from `entry.TargetPlayers` in selector order. That is the second of the two roads E0g named
  ("the second re-derives at every resolution"). It plays correctly in both games above; it would
  only diverge from the announcement if a later reader consumed `TargetPlayers` in a different
  order than the walk, which nothing does today. Left as the E3/E9 series' call.
- **Game 5 is written to survive E3j.** It asserts the pool, the `#0` key spelling, the delayed
  ability and the firing only after `PassUntilStackIsEmpty` (which returns at once when the stack
  is already empty), so the test holds whether the sequence resolves on the stack or at activation.
  E3j's immediate road must keep `ManaSymbols.AnnouncedColorsKey` readable by `PoolFor` and the
  `key#n` provenance spelling, or this test is the one that says so.
- **Game 4 pays every creature with the hall's own {C}** and walks the turns until each creature is
  in hand, because the shuffled deck (seed 7) did not deal the Goblin by turn 3 on the first run.
  The walk's discard answer takes the first options at the hand-size check, so a much unluckier
  shuffle could discard the creature it waits for; a deterministic library placement through the
  scenario runner would remove that, but the front-door pattern here is the wave-9/10 one.
