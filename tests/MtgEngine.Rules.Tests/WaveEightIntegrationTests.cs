using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-8 joins, played through <see cref="Game.Apply"/> on <see cref="Subsystems.Wave2"/>:
/// an Aura that enchants a player (CR 303.4f and 704.5m), a lasting permission that lets a card
/// be cast from a graveyard for free (CR 601.3a and 118.9), an additional cost whose objects the
/// game asks for (CR 601.2c), and a combat damage batch halted by a replacement-order question
/// and resumed by its answer (CR 616.1 and 510.2). Each builder verified its own half against
/// its own fakes; what is asserted here is that the halves meet in a real game. Every game ends
/// with the log written, read back and replayed to the same state.
/// </summary>
public sealed class WaveEightIntegrationTests
{
    private static PlayerId P0 => WaveEightIntegrationTable.P0;

    private static PlayerId P1 => WaveEightIntegrationTable.P1;

    /// <summary>
    /// CR 303.4f: an Aura enters the battlefield attached to the object or player it was cast at,
    /// and CR 704.5m buries one attached to something it cannot legally enchant. An "enchant
    /// player" Aura cast at a player therefore stays: the state-based actions see the player
    /// attachment and let it be. The same Aura's object-restricted twin, cast at a player, is the
    /// other half of the rule and the test says which road the engine takes.
    /// </summary>
    [Fact]
    [RuleRef("303.4f")]
    public void An_Aura_that_enchants_a_player_resolves_attached_to_them_and_survives_the_settle()
    {
        var table = new WaveEightIntegrationTable(AuraDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);

        var card = table.InHand(P0, WaveEightIntegrationTable.PlayerAuraName);
        var cast = table.Do(new CastSpellCommand(P0, card, TargetPlayers: [P1]));
        var spell = Assert.Single(cast.OfType<SpellCastEvent>()).Spell;
        Assert.Equal([P1], table.State.Objects[spell].Stack!.TargetPlayers);

        var resolved = table.PassRound();
        var aura = WaveEightIntegrationTable.Became(resolved, spell);

        // CR 303.4f: attached to the player it was cast at, and to no object.
        Assert.Equal(CardZone.Battlefield, table.State.Objects[aura].Zone.Zone);
        Assert.Equal(P1, table.State.Objects[aura].AttachedToPlayer);
        Assert.Null(table.State.Objects[aura].AttachedTo);
        Assert.Equal(P1, Assert.Single(resolved.OfType<ObjectChanged>(), e => e.AttachedToPlayer is not null).AttachedToPlayer);

        // CR 704.5m: state-based actions run on every priority pass, and it is still there after
        // a whole turn of them — the join E3f wrote the event for and E5c taught the rule to.
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        Assert.Equal(aura, Assert.Single(table.Battlefield(P0, WaveEightIntegrationTable.PlayerAuraName)));
        Assert.Equal(P1, table.State.Objects[aura].AttachedToPlayer);

        // --- the object-restricted twin, cast at a player ---
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);
        var restricted = table.InHand(P0, WaveEightIntegrationTable.CreatureAuraName);
        // Which road the engine takes: it is buried, not refused. The announcement is accepted —
        // an ObjectFilter cannot describe a player, so nothing on the casting side rules the
        // player target out (seam-requests/E3f.md § 2 records that a player target is taken as
        // legal) — and CR 704.5m then puts the permanent into its owner's graveyard, because
        // StateBasedActions reads an enchant ability that names an object filter as a restriction
        // to objects (seam-requests/E5c.md § 1). The permanent existed for a moment, which is the
        // same graveyard by the longer road; both builders wrote the gap down and this is where
        // the two halves meet.
        var second = table.Do(new CastSpellCommand(P0, restricted, TargetPlayers: [P1]));
        var doomed = Assert.Single(second.OfType<SpellCastEvent>()).Spell;
        table.PassRound();

        Assert.Empty(table.Battlefield(P0, WaveEightIntegrationTable.CreatureAuraName));
        Assert.NotEmpty(table.Graveyard(P0, WaveEightIntegrationTable.CreatureAuraName));
        Assert.DoesNotContain(doomed, table.State.Objects.Keys);

        // And the player Aura is untouched by that: CR 704.5m buries the one it applies to, no more.
        Assert.Equal(aura, Assert.Single(table.Battlefield(P0, WaveEightIntegrationTable.PlayerAuraName)));

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 601.3 and 601.3a: a card is cast from its owner's hand unless an effect says otherwise,
    /// and an effect that says a player may cast it is that permission. CR 118.9: the same effect's
    /// "without paying its mana cost" half is what makes the announcement legal with an empty pool.
    /// The permission is a continuous effect that outlives the resolution that made it (E9f, E3f),
    /// and the casting system is the thing that reads it (E3f).
    /// </summary>
    [Fact]
    [RuleRef("601.3a")]
    public void A_lasting_permission_lets_a_card_be_cast_from_a_graveyard_with_no_mana()
    {
        var table = new WaveEightIntegrationTable(RecallDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);

        // An instant cast and resolved is a card in the graveyard, CR 608.2m, and a new object there.
        var spark = table.InHand(P0, WaveEightIntegrationTable.SparkName);
        var onStack = Assert.Single(table.Do(new CastSpellCommand(P0, spark)).OfType<SpellCastEvent>()).Spell;
        var inGraveyard = WaveEightIntegrationTable.Became(table.PassRound(), onStack);
        Assert.Equal(CardZone.Graveyard, table.State.Objects[inGraveyard].Zone.Zone);

        // CR 601.3: with no permission the graveyard is not a zone this card may be cast from.
        Assert.Equal("601.3", table.Refuse(new CastSpellCommand(P0, inGraveyard)).Rule);

        // GAP, reported in seam-requests/EINT3.md for the casting builder (E3g): the same
        // command with WithoutPayingManaCost set is accepted from the graveyard with no
        // permission at all. CR 118.9 is a permission an *effect* gives, not something a
        // player may assert on the command, and CR 601.3's zone check has to hold either way.
        // What is asserted here is what the engine does, so the day it is fixed this line fails.
        Assert.IsType<Applied>(table.Try(new CastSpellCommand(P0, inGraveyard, WithoutPayingManaCost: true)));

        // CR 611.1 and 601.3a: the sorcery's resolution creates the permission rather than casting.
        var recall = table.InHand(P0, WaveEightIntegrationTable.RecallName);
        var recallSpell = Assert.Single(table.Do(new CastSpellCommand(P0, recall)).OfType<SpellCastEvent>()).Spell;
        table.PassRound();
        var permission = Assert.Single(
            table.State.ActiveEffects,
            e => e.Effect is MayPlayFromZone { From: CardZone.Graveyard, WithoutPayingManaCost: true });
        Assert.Contains(inGraveyard, permission.AppliesTo);
        Assert.Contains(P0, permission.AppliesToPlayers);
        Assert.DoesNotContain(recallSpell, table.State.Objects.Keys);

        // CR 118.9: nothing is in the pool, and nothing is spent.
        Assert.Equal(0, table.State.Player(P0).Mana.Total);
        var drawnBefore = table.State.In(ZoneKey.Of(CardZone.Hand, P0)).Count;
        var free = table.Do(new CastSpellCommand(P0, inGraveyard, WithoutPayingManaCost: true));

        var freeSpell = Assert.Single(free.OfType<SpellCastEvent>());
        Assert.Equal(CardZone.Graveyard, freeSpell.From);
        Assert.Contains("free", table.State.Objects[freeSpell.Spell].Stack!.OptionalCostsPaid!);
        Assert.DoesNotContain(free, e => e is ManaChanged);

        table.PassRound();
        Assert.Equal(drawnBefore + 1, table.State.In(ZoneKey.Of(CardZone.Hand, P0)).Count);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 601.2b and 601.2c: an additional cost is announced with the spell and the objects that
    /// pay it are chosen then. A "you may cast that card" whose card carries such a cost therefore
    /// asks twice — the permission's yes/no, then the cost's objects — and the second answer is
    /// what makes the announcement complete. The casting system is the one place that knows a cost
    /// is unpaid (E3f) and the interpreter is the one that turns that into a question (E9g).
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void A_may_cast_of_a_card_with_a_sacrifice_cost_asks_which_permanent_pays_it()
    {
        var table = new WaveEightIntegrationTable(RiteDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        var lands = table.Lands(P0);
        Assert.Equal(2, lands.Count);

        // CR 601.2c, at the seam that decides it and with a player who holds priority: two lands
        // could pay a cost that wants one, so the payment is not forced and the announcement is
        // incomplete until the command names which. Naming one completes it. This is the refusal
        // the interpreter's cast-cost question is built from.
        var toll = table.InHand(P0, WaveEightIntegrationTable.TollName);
        Assert.Equal("601.2c", table.Engine.Casting.Check(table.State, new CastSpellCommand(P0, toll))?.Rule);
        Assert.Null(table.Engine.Casting.Check(table.State, new CastSpellCommand(P0, toll, CostObjects: [lands[0]])));

        var rite = table.InHand(P0, WaveEightIntegrationTable.RiteName);
        table.Do(new CastSpellCommand(P0, rite));
        table.PassRound();

        // CR 608.2 and 101.4: the permission itself is the first question.
        var may = Assert.IsType<PendingChoice>(table.State.Pending);
        Assert.Equal("may-cast", may.Kind);
        Assert.Equal(P0, may.Player);
        Assert.Equal("101.4", table.Refuse(new PassPriority(P0)).Rule);
        Assert.Equal(2, may.Options.Count);
        Assert.NotEmpty(table.Hand(P0, WaveEightIntegrationTable.TollName));

        // GAP, reported in seam-requests/EINT3.md for the casting builder (E3g): saying yes never
        // reaches CR 601.2c's cost question, because the cast is refused first. The spell is being
        // cast *during a resolution*, so nobody holds priority — and
        // CastingSystem.RequireCastingTiming refuses every cast whose caster is not the priority
        // holder (CR 117.1a). CR 608.2g is the rule the check is missing: a spell cast because an
        // effect instructs a player to cast it ignores timing, and the interpreter's may-cast arm
        // is exactly that instruction. Until it lands the join is unreachable in a real game, so
        // what is asserted is the refusal and its rule, not a cast that does not happen.
        var yes = table.Try(new AnswerChoice(P0, may.Id, [may.Options[0].Id]));
        var blocked = Assert.IsType<Refused>(yes);
        Assert.Equal("117.1a", blocked.Rule);
        Assert.Equal(may.Id, Assert.IsType<PendingChoice>(table.State.Pending).Id);
        Assert.NotEmpty(table.Hand(P0, WaveEightIntegrationTable.TollName));
        Assert.Equal(lands, table.Lands(P0));

        // The same check answers 117.1a here too, while the question stands: the timing is what it
        // fails on, before it ever looks at the cost, which is why the cast-cost question cannot be
        // reached from a resolution at all.
        Assert.Equal("117.1a", table.Engine.Casting.Check(table.State, new CastSpellCommand(P0, toll))?.Rule);

        // Declining leaves the game where it was, so the log still replays, CR 608.2.
        table.Do(new AnswerChoice(P0, may.Id, [may.Options[1].Id]));
        Assert.Null(table.State.Pending);
        Assert.Empty(table.State.In(ZoneKey.Shared(CardZone.Stack)));
        Assert.Equal(lands, table.Lands(P0));

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 616.1 and 510.2: two prevention shields on one player are two applicable replacement
    /// effects, so the affected player chooses which applies first — and the combat damage batch
    /// that raised the question stops there, mid-batch. Answering resumes the rest of the batch
    /// (E4b's gap, closed by E4c): the second attacker's damage is dealt, and the life total is
    /// the arithmetic of what each shield actually prevented.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_replacement_order_question_halts_a_combat_damage_batch_and_its_answer_finishes_it()
    {
        var table = new WaveEightIntegrationTable(AttackDeck(), ShieldDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        CastTheOx(table);

        CastAShield(table, 4);

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        CastTheOx(table);

        CastAShield(table, 6);

        // CR 615.1: two shields, each one damage, both applying to the same player.
        Assert.Equal(2, table.State.ActiveEffects.Count(e => e.Effect is PreventDamage && e.AppliesToPlayers.Contains(P1)));

        table.PassTo(7, Step.DeclareAttackers);
        var attackers = table.Battlefield(P0, WaveEightIntegrationTable.OxName);
        Assert.Equal(2, attackers.Count);
        Assert.All(attackers, id => Assert.False(table.State.Objects[id].SummoningSick));

        var life = table.State.Player(P1).Life;
        table.Do(new DeclareAttackers(P0, [.. attackers.Select(a => new AttackDeclaration(a, new CombatTarget(P1, null)))]));
        table.PassTo(7, Step.DeclareBlockers);
        table.Do(new DeclareBlockers(P1, []));

        // CR 510.2 with 616.1: walking into the combat damage step stops on the question, and the
        // player who is being dealt the damage is the one asked.
        var question = PassUntilQuestion(table);
        Assert.Equal(ChoiceKinds.ReplacementOrder, question.Kind);
        Assert.Equal(P1, question.Player);
        Assert.Equal(2, question.Options.Count);
        Assert.Equal("101.4", table.Refuse(new PassPriority(P1)).Rule);

        // CR 510.2: the answer finishes that hit and deals the rest of the batch.
        var answered = table.Do(new AnswerChoice(P1, question.Id, [question.Options[0].Id]));
        Assert.Contains(answered, e => e is ChoiceAnswered);

        // Each shield prevents one of the first attacker's two damage, so that hit deals nothing;
        // the second attacker meets no shield at all, CR 615.1 with 510.2.
        var dealt = table.Log.OfType<DamageDealt>().Where(d => d.Combat && d.ToPlayer == P1).ToList();
        Assert.Equal(2, Assert.Single(dealt).Amount);
        Assert.Equal(2, life - table.State.Player(P1).Life);
        Assert.DoesNotContain(table.State.ActiveEffects, e => e.Effect is PreventDamage);

        // GAP, reported in seam-requests/EINT3.md for the combat and choices builder (E4d): the
        // batch is finished but the step is not — nobody holds priority in the combat damage step
        // after the resumption, so the game cannot be walked on. CR 510.3 gives every player
        // priority after combat damage is dealt, and the ordinary batch does; the resumed one
        // hands back its events without asking the turn system to open the step again.
        Assert.Equal(Step.CombatDamage, table.State.Turn.Step);
        Assert.Null(table.State.Priority.Holder);
        Assert.Null(table.State.Pending);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>Cast the vanilla creature with the mana in the pool and let it resolve, CR 608.3.</summary>
    [RuleRef("608.3")]
    private static void CastTheOx(WaveEightIntegrationTable table)
    {
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveEightIntegrationTable.OxName)));
        table.PassRound();
    }

    /// <summary>The defender's turn: a land, and a sorcery that gives them a prevention shield, CR 615.1.</summary>
    [RuleRef("615.1")]
    private static void CastAShield(WaveEightIntegrationTable table, int turn)
    {
        table.PassTo(turn, Step.PrecombatMain);
        table.PlayALand(P1);
        table.TapForMana(P1, 1);
        table.Do(new CastSpellCommand(P1, table.InHand(P1, WaveEightIntegrationTable.ShieldName)));
        table.PassRound();
    }

    /// <summary>
    /// Pass priority until the game stops to ask something, CR 101.4. Unlike <c>PassTo</c> the
    /// question is the destination, because the batch that raised it is what is under test.
    /// </summary>
    [RuleRef("101.4")]
    private static PendingChoice PassUntilQuestion(WaveEightIntegrationTable table)
    {
        for (var guard = 0; guard < 200; guard++)
        {
            if (table.State.Pending is { } pending)
                return pending;
            if (table.State.Outcome is not null || table.State.Priority.Holder is not { } holder)
                break;
            table.Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Nothing was asked; the game reached turn {table.State.Turn.Number} {table.State.Turn.Step}.");
    }

    /// <summary>
    /// The log is the game: written, read back and folded from nothing, it is the state the engine
    /// is holding. A log that will not round-trip cannot be stored, and a replay that disagrees
    /// means some subsystem changed state without an event — which is exactly what a new join is
    /// most likely to do.
    /// </summary>
    private static void AssertTheLogIsTheGame(WaveEightIntegrationTable table)
    {
        var json = EventLogSerializer.Write(table.Log);
        var read = EventLogSerializer.Read(json);

        Assert.Equal(table.Log.Count, read.Count);
        Assert.Equal(json, EventLogSerializer.Write(read));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(table.Log)));
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(read)));
    }

    /// <summary>
    /// Everything about a state a player could see or a rule could ask, as text, so a mismatch
    /// reads as a diff. The wave-8 fields are in it: what a permanent is attached to, on the object
    /// side and on the player side, and the effects that are still running.
    /// </summary>
    private static string Describe(GameState state) => string.Join(
        "\n",
        [
            $"turn {state.Turn.Number} {state.Turn.Step} active {state.Turn.ActivePlayer} order {state.Turn.TurnOrderIndex}",
            $"priority {state.Priority.Holder} passed [{string.Join(",", state.Priority.PassedInSuccession)}]",
            $"pending {state.Pending?.Kind ?? "none"} {state.Pending?.Player.ToString() ?? string.Empty}",
            $"outcome {(state.Outcome is null ? "none" : $"{string.Join(",", state.Outcome.Winners)} draw {state.Outcome.Draw}")}",
            $"next object {state.NextObjectId} timestamp {state.NextTimestamp} choice {state.NextChoiceId}",
            .. state.Players.Select(p => $"player {p.Id} life {p.Life} lost {p.HasLost} mana {p.Mana.Total} lands {p.LandsPlayedThisTurn}"),
            .. state.Objects.Values.OrderBy(o => o.Id.Value).Select(o =>
                $"object {o.Id} {o.Definition.Name} in {o.Zone.Zone}/{o.Zone.Player} owner {o.Owner} tapped {o.Tapped} sick {o.SummoningSick} damage {o.Damage} on {o.AttachedTo} onPlayer {o.AttachedToPlayer}"),
            .. state.ActiveEffects.OrderBy(e => e.Id).Select(e =>
                $"effect {e.Id} {e.Effect.GetType().Name} by {e.Controller} objects [{string.Join(",", e.AppliesTo)}] players [{string.Join(",", e.AppliesToPlayers)}]"),
            .. state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1).Select(z =>
                $"zone {z.Key.Zone}/{z.Key.Player} [{string.Join(",", z.Value)}]"),
        ]);

    /// <summary>Lands and the two Auras. Weighted so the opening hand holds a land and a player Aura.</summary>
    private static ImmutableList<string> AuraDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 12),
        .. Enumerable.Repeat(WaveEightIntegrationTable.PlayerAuraId, 6),
        .. Enumerable.Repeat(WaveEightIntegrationTable.CreatureAuraId, 6),
    ];

    /// <summary>Lands, the instant that ends up in the graveyard, and the sorcery that gives the permission.</summary>
    private static ImmutableList<string> RecallDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 12),
        .. Enumerable.Repeat(WaveEightIntegrationTable.SparkId, 6),
        .. Enumerable.Repeat(WaveEightIntegrationTable.RecallId, 6),
    ];

    /// <summary>Lands, the sorcery that offers the cast, and the instant whose cost names a permanent.</summary>
    private static ImmutableList<string> RiteDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 12),
        .. Enumerable.Repeat(WaveEightIntegrationTable.RiteId, 6),
        .. Enumerable.Repeat(WaveEightIntegrationTable.TollId, 6),
    ];

    /// <summary>Lands and two creatures' worth of attackers.</summary>
    private static ImmutableList<string> AttackDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveEightIntegrationTable.OxId, 10),
    ];

    /// <summary>Lands and the shields the defender casts, one a turn.</summary>
    private static ImmutableList<string> ShieldDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveEightIntegrationTable.ShieldId, 10),
    ];

    /// <summary>Only lands: the opponent plays one every turn, so nobody is ever asked to discard, CR 514.1.</summary>
    private static ImmutableList<string> LandDeck() =>
        [.. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 24)];
}
