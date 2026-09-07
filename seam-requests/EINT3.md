# EINT3 — the wave-8 joins, played through Game.Apply

Four games on `Subsystems.Wave2()`, made-up definitions only, every move a command through
`Game.Apply`, every log written, read back and replayed to the same state
(`WaveEightIntegrationTests`, `WaveEightIntegrationTests.Fixture.cs`). Two joins hold end to end.
Two do not, and the tests assert what the engine really does with the gap named in a comment.

## 1. Holds: an Aura that enchants a player (CR 303.4f, 704.5m)

`KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter())`, cast with `TargetPlayers`,
resolves attached to that player (`AttachedToPlayer`, and the `ObjectChanged` that says so) and is
still on the battlefield a whole turn of state-based actions later. E3f's event and E5c's CR 704.5m
arm meet correctly.

**Which road the object-restricted twin takes, for E3f § 2 and E5c § 1:** an Aura whose enchant
ability names an `ObjectFilter`, cast at a player, is **buried, not refused**. The announcement is
accepted — nothing on the casting side rules a player target out — and CR 704.5m puts the permanent
into its owner's graveyard on the next state-based action check. Both builders predicted their own
half; this is the first place the two halves are seen together in a game.

## 2. Holds: the CR 616.1 halt inside a combat damage batch (CR 510.2)

Two `PreventDamage(Fixed(1), ToPlayersToo: true)` shields on the defending player, two attackers.
The first hit raises the replacement-order question, the game stops there (a `PassPriority` is
refused naming CR 101.4), and the answer resumes the batch: both shields spend against the first
hit, the second attacker's 2 damage is dealt, the life total is right. E4b's gap is closed by E4c.

### Gap 2a (E4d — combat and choices): the resumed step never re-opens

**After the resumption nobody holds priority**, and the step is still `CombatDamage`. The batch is
finished and correct, but the game cannot be walked on: `PassTo` throws "Nobody holds priority in
turn 7 CombatDamage". CR 510.3 gives every player priority once combat damage is dealt, and the
ordinary batch does; the resumed one hands back its events without asking the turn system to open
the step again. Smallest reproduction is the test itself — the assertion is
`Assert.Null(table.State.Priority.Holder)` with `Step.CombatDamage`, so the day it is fixed that
line fails and says so.

## 3. Gap (E3g — casting): a may-cast can never be cast, CR 608.2g vs 117.1a

`MayCast` with no `Until` asks its yes/no question correctly (kind `"may-cast"`, two options, only
its player may answer, CR 101.4 refuses everything else). **Answering yes is refused with
`("You do not have priority.", "117.1a")`.** `CastingSystem.RequireCastingTiming`
(`MtgEngine.Rules/Casting/CastingSystem.cs:404`) refuses every cast whose caster is not
`state.Priority.Holder`, and a spell cast *during a resolution* has no priority holder by
definition.

CR 608.2g is the rule the check is missing: a spell cast because an effect instructs a player to
cast it is cast as part of that resolution and ignores timing. Until a "this cast is instructed"
channel exists — the same `CastSpell` the interpreter builds already carries `WithoutPayingManaCost`
and `CostObjects`, so a flag beside them, or a member on `ICastingSystem` — **no `MayCast` without a
duration can ever cast anything in a real game**, and E9g's cast-cost question (CR 601.2c) is
unreachable: `Check` fails on the timing before it ever looks at the cost.

The CR 601.2c half is proved separately in the same test, at a moment when a player does hold
priority: with two lands on the battlefield and a face whose additional cost is "sacrifice a land",
`Check` refuses naming 601.2c and accepts the same command once `CostObjects` names one land. The
cost seam is right; only the road to it is blocked.

## 4. Gap (E3g — casting): `WithoutPayingManaCost` on the command bypasses CR 601.3

A card in a graveyard with no permission is refused naming CR 601.3 — correct. **The same command
with `WithoutPayingManaCost: true` is accepted**, from the graveyard, with no permission of any
kind. CR 118.9 is a permission an *effect* gives (and E3f's `CastPlan.Permission` reads
`MayPlayFromZone` for it); it is not something a player may assert on the command, and CR 601.3's
zone check has to hold whatever the mana half says. Reproduction: cast any instant, let it resolve,
then send `CastSpell(player, thatCardInTheGraveyard, WithoutPayingManaCost: true)`. The test
asserts the acceptance (`Assert.IsType<Applied>`), so the fix will fail that line.

With a real `MayPlayFromZone { WithoutPayingManaCost = true }` in play the join is otherwise right:
the permission is created by the resolution, applies to that card and that player, the free cast
records `"free"` on the stack entry, emits no `ManaChanged`, and spends nothing from an empty pool.

## 5. Nothing asked for on a seam

No interface, event, state record or vocabulary record needs to change for these four tests; every
gap above is behaviour inside a folder that already owns it.
