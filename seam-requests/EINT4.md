# EINT4 — the wave-9 and wave-10 joins played through `Game.Apply`

Eight games in `tests/MtgEngine.Rules.Tests/WaveNineIntegrationTests*.cs`, all on
`Subsystems.Wave2()` with made-up definitions, every command through `Game.Apply` and every game
ending with the log written, read back and replayed to the same state. Three of the six joins hold
end to end. Five defects are below, each with the smallest reproduction that shows it; every test
asserts what the engine actually does, with the gap in a comment beside it, so none of them has to
be deleted when a fix lands — the assertion flips and the comment goes.

**Holds**: CR 106.6b (`PayOnlyFrom`: an artifact's mana pays, a land's is refused naming 106.6b),
CR 613.10 (`PlayerCant(LifeTotalChanges)` takes the burn spell's damage to nothing while the same
spell at the other player deals it), CR 118.12 answered no (nothing paid, nothing drawn, the pool
untouched).

## 1. Nothing fires a `ManaSpent` delayed trigger, and the ability is dropped with the pool (CR 106.6a)

`interpreter` + `casting`. E9h § 1 asked for the firing side and wave 10 did not land it, so the
first half of this is a restatement — but the state is worse than E9h described.

A land with `{T}: Sequence[AddMana({G}, Key: "k"), DelayedTrigger(ManaSpent("k"), Draw(1))]`:

- tapping it writes exactly the block E9h and E6c agreed on — `ManaProvenance(Green, 1, land,
  "k#0")` — and one entry in `GameState.DelayedTriggers`. Both halves of the interpreter's work
  are visible in a real game. Good.
- casting a `{G}` creature with that mana draws **nothing**: no subsystem reads `ManaSpent`.
- **and `GameState.DelayedTriggers` is empty afterwards.** E9h said the ability would "sit in
  `DelayedTriggers` until the pool empties", which is honest state; it does not. The spend (or the
  pool emptying) removes it, so a firing channel landing later in the same turn would find nothing
  to fire. The card's second sentence is silently dropped rather than deferred.

Reproduction: `WaveNineIntegrationTests.Mana_with_a_key_fires_its_delayed_trigger_when_it_pays_for_a_spell`.
Either channel E9h named would close it; whichever lands must also stop the ability being discarded
before it can fire.

## 2. A `ChooseValue(Color)` question is asked with no options, and every answer is refused (CR 601.2b)

`interpreter` + `choices`. This is the one that stalls a game rather than dropping a line.

An artifact with `TriggeredAbility(EntersBattlefield(self), ChooseValue(ChooseWhat.Color, key))`
resolves, and the game stops on a `value` question, prompt "Choose Color", **`Options` empty**. The
answer check refuses everything: `AnswerChoice(P0, id, ["Green"])` and `["green"]` are both refused
naming CR 601.2b, "That is not one of the options". There is no answer that is one of the options,
because there are none, so CR 101.4 holds the game there for ever — no priority, no later turn, no
way out. `MEMORY: unanswered question stalls the game` is exactly this shape.

Asked for: the interpreter fills a `ChooseValue`'s question with its options — the five colours for
`ChooseWhat.Color`, the `Options` list where the effect carries one, and the corresponding sets for
`CreatureType`, `CardType`, `BasicLandType` and the rest. `ChooseValue.Options` already exists on
the record for the cases a card narrows.

**This is what blocks CR 106.12a end to end**, so § 2 and § 3 of E6c are not testable through the
front door yet: no colour is ever stored (`GameObject.Chosen` stays empty for the key), so a
`TappedForMana(..., OfChosenColorKey: key)` cannot match and the additional mana is never added.
The store and the trigger arm may both be right; nothing can reach them.

Reproduction: `WaveNineIntegrationTests.A_tapped_for_mana_trigger_reads_the_colour_the_permanent_chose`.

## 3. Nobody holds priority after a question-resumed resolution (CR 117.3b)

`interpreter` + `priority`. The same shape EINT3 reported for a resumed combat damage batch and E4e
fixed there, on the other resumption road.

"When this enters, you may pay {2}. If you do, draw a card": the game stops on a `may-pay`
question, the answer pays the two mana and draws the card correctly — and then the stack is empty,
nothing is pending, and `State.Priority.Holder` is `null`. CR 117.3b gives the active player
priority after a spell or ability resolves, and a resolution finished by an answer is still a
resolution. The game cannot be walked on, so a second half of the same test had to become its own
game.

Reproduction: `WaveNineIntegrationTests.A_may_pay_inside_a_resolution_spends_the_pool_when_it_is_answered_yes`
(the assertion at the end is `Assert.Null(State.Priority.Holder)`, which is the bug written down).

## 4. A player with protection from everything can still be targeted at announcement (CR 115.6)

`casting` + `query`. `ApplyToPlayer(PlayerProtectionFromEverything, You, Permanently)` is active on
P1; P0 announces a `DealDamage(2, ToPlayer: TargetPlayer(PlayerFilter.Any))` naming P1 and the
announcement is **accepted** — `SpellCast` is emitted with P1 on the stack object.

CR 115.6 makes P1 an illegal target for an opponent's spell, so CR 601.2c should refuse the
announcement, exactly as E3h now refuses an Aura announced at a player its enchant ability cannot
name. What saves the game today is the resolution: CR 702.16j prevents the damage, so P1's life
never moves. The observable result is right and the road to it is wrong — a spell that should never
have been cast is on the stack, and any other effect of it would happen.

The same check is presumably missing for `PlayerCant(BeTargeted)`, which is the same rule written
the narrower way; that arm was not exercised here.

Reproduction: `WaveNineIntegrationTests.A_player_with_protection_from_everything_cannot_be_targeted_by_an_opponents_spell`.

## 5. `MayCast` of a card in a graveyard is refused for the zone (CR 601.3a)

`casting` + `interpreter`. A sorcery whose effect is
`MayCast(Each(ObjectFilter(In: Graveyard, Types: Instant, OwnedBy: You)))` asks its `may-cast`
question with the graveyard card as the option; answering **yes** is refused naming CR 601.3, "A
card in that zone cannot be cast without permission."

CR 601.3a is explicit that an effect saying a player may cast a card *is* the permission CR 601.3
asks for. The instructed cast reads the zone as though nothing had said otherwise, so the only road
to a graveyard cast is still the lasting `MayPlayFromZone` permission wave 8 built — a `MayCast`
naming a graveyard card can never be taken up.

Consequence for wave 10's own work: the E9i + E3h + E3g join — a may-cast whose card carries an
object-naming additional cost, asked for as a `cast-cost` question and answered — is **unverified
through the front door on this road**. The card in the reproduction has "as an additional cost,
sacrifice a land", and the refusal lands before the cost is ever asked for. The hand road (E9i's
`MayCast` of a card in hand) was left to EINT3's and the wave-8 test's coverage.

Reproduction: `WaveNineIntegrationTests.An_instructed_cast_asks_for_its_additional_cost_and_sacrifices_what_is_named`.

## Not asked for, recorded instead

- **A land-only opponent runs into CR 514.1 every turn.** Their hand outgrows the maximum, the
  cleanup step asks them to discard, and the question stops any priority walk. The fixture's
  `PassTo` answers a `discard` question and carries on rather than treating it as the destination;
  a wave-8-style land-only deck for the other seat needs that or it never reaches turn 5.
- **`ChoiceKinds` has no constant for `"may-pay"`, `"may-cast"` or `"cast-cost"`**, so the tests
  match them as string literals. Three kinds the interpreter raises are spelled in two places.
