# E0g — the remembered player, and the direction of `AttachedTo`

## 1. Nothing writes a player key at cast time (for E9l / the casting system)

`RememberedPlayer(key)` is now answered by the query layer: `Query.Players` reads
`EffectContext.RememberedPlayers[key]` (CR 608.2h — an effect determines its information once, so
a later step of the same resolution reads back the player an earlier step named). A key that is
present but empty is an answer ("up to one target player", none chosen); a key nobody wrote is
refused rather than read as "nobody", so an effect is never silently dropped.

**The write side is missing.** `StackEntry.RememberedPlayers` is filled in exactly one place —
`Triggers/TriggerSystem.cs:326`, under the reserved key `"triggering"`. The casting system puts
the chosen players in `StackEntry.TargetPlayers` (`Casting/CastingSystem.cs:109`,
`Casting/CastingSystem.Instructed.cs:72`) **with no key**, and
`Interpreter/EffectInterpreter.cs:141-152` (`ContextFor`) passes `entry.RememberedPlayers` through
unchanged. So a `TargetPlayer(Key: "k1")` announced on casting leaves nothing under `"k1"`.

Consequence, and the card that shows it: `cards/c6207f6a-a624-4754-88f5-dbe700c841ff.json`
("target player draws two cards and loses 2 life") is spelled `TargetPlayer(key)` then
`RememberedPlayer(key)`. Its verify refusal used to read *"EffectContext remembers objects by key
but not players"*, which was stale — the context has had `RememberedPlayers` since the wave-1
seams. The refusal it gets now names the real gap: no step wrote the key.

**Ask**: whoever owns `Casting/**` and `Interpreter/**` records the announced player under the
`TargetPlayer.Key` when a spell or ability is cast or activated (CR 601.2c), the same way the
object half is remembered — either onto `StackEntry.RememberedPlayers` at announcement, or in
`ContextFor` by walking the ability for a keyed `TargetPlayer` and seeding the key from
`entry.TargetPlayers`. The first is the one road; the second re-derives at every resolution.
No seam type changes: `StackEntry.RememberedPlayers` and `EffectContext.RememberedPlayers`
both already exist and are already threaded.

## 2. `ChosenPlayer` has nowhere to live (for the vocabulary / V-series)

`ChoosePlayer` and `ChosenPlayer` still refuse, now with their own reasons rather than a shared
one. `ChoosePlayer` is right to refuse forever: the query reads and never chooses (CR 608.2), and
the choice system asks the question. `ChosenPlayer(key)` (CR 607.2d, a linked ability's earlier
choice) is a **storage** gap: `GameObject.Chosen` maps a key to a `string`, so a name, a type or a
colour fits and a `PlayerId` does not. Either `ChosenPlayer` resolves through the same
`RememberedPlayers` channel as §1 once something writes it, or the object needs a chosen-players
map beside `Chosen`. Not urgent — no pilot card reaches it — but the record exists in
`MtgEngine.Cards/Players.cs:56` and is unanswerable, and the translator prompt
(`Cards.Pipeline/Translate/TranslationPrompt.cs:26`) offers it to translators.

## 3. `AttachedTo` reads as its doc comment writes it; one fixture spells it backwards (for V8)

The wave-10 note said the query reads `AttachedTo` in the opposite direction from the "equipped
creature" fixture. Decided from the vocabulary's own doc comment, which is **not ambiguous**:

> `MtgEngine.Cards/Selectors.cs:215` — `/// <summary>Attached to the given object: "Auras attached
> to it".</summary>`

The candidate is the attachment; `To` names the host. That is what `Query.Predicates.cs:37` does,
so **no behaviour changed** — the direction is now pinned by two tests
(`QueryTests.Players.cs`: `Attached_to_names_the_attachment_and_never_its_host` and
`Attached_to_is_not_the_equipped_creature_and_says_so_by_answering_none`) and stated in the arm's
comment. It is also the direction that keeps the vocabulary non-redundant: the *other* direction
already has a selector, `Attached` ("the object this is attached to"), and "Auras attached to it"
already has `Attachments`.

**The defect is the fixture, not the doc.** `tests/MtgEngine.Cards.Tests/Fixtures.cs:21` writes
"Whenever equipped creature dies, draw two cards" as
`Dies(new ObjectFilter(Also: new AttachedTo(new Self())))`. Under the settled direction that asks
whether the dying creature is attached to the Equipment, which no creature ever is, so the trigger
can never fire. Nothing plays wrong today — that fixture is only used by the round-trip and linter
tests — but a translator reading it will copy the shape onto a real Equipment.

**Ask (V8, whoever owns `MtgEngine.Cards/**` and the Cards fixtures)**: two things.
(a) Fix the fixture. (b) There is a genuine **vocabulary gap** behind it: no `Predicate` says "the
object this is attached to". `Attached` is a `Selector`, and an `ObjectFilter.Also` needs a
predicate, so "whenever equipped creature dies" cannot be written at all right now. The smallest
record that closes it is a predicate that tests the candidate against a selector — e.g.
`IsOneOf(Selector Of)` — which also gives `Remembered`, `Triggering` and `Attached` a filter
spelling; a second choice is `AttachedTo(Selector To, bool Inverted = false)`, but a flag that
flips the meaning of a record is the kind of second spelling this programme has been refusing.

## Changed here

- `MtgEngine.Rules/Query/Query.Selectors.cs` — the `RememberedPlayer` arm; `ChoosePlayer` and
  `ChosenPlayer` get their own refusal reasons and rule refs (601.2b, 607.2d); the stale
  `NoPlayerMemory` string is gone.
- `MtgEngine.Rules/Query/Query.Predicates.cs` — the `AttachedTo` arm's comment states the
  direction and names the missing predicate.
- `tests/MtgEngine.Rules.Tests/QueryTests.cs` — a `World.RememberPlayer` helper.
- `tests/MtgEngine.Rules.Tests/QueryTests.Players.cs` — six tests.
