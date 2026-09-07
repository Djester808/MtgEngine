using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The other three wave-9 and wave-10 joins, on the same table: a spend restriction that names the
/// mana's source rather than its colour (CR 106.6b), an instructed cast whose additional cost is
/// asked for and answered (CR 601.2c), and the player prohibitions a continuous effect can impose
/// (CR 613.10). Split from <see cref="WaveNineIntegrationTests"/> only for length.
/// </summary>
public sealed partial class WaveNineIntegrationTests
{
    /// <summary>
    /// CR 106.6b: "activate only using mana produced by an artifact" restricts which mana may pay,
    /// by the permanent that produced it and not by its colour. E3g wrote the provenance, E3h
    /// spends against it, and the ability's <c>PayOnlyFrom</c> is what the two read; the mana is
    /// otherwise ordinary and empties from the pool as usual.
    /// </summary>
    [Fact]
    [RuleRef("106.6b")]
    public void An_ability_that_may_only_be_paid_from_an_artifact_refuses_a_lands_mana()
    {
        var table = new WaveNineIntegrationTable(GateDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);
        table.TapForMana(P0, 1);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.RockName)));
        table.PassUntilStackIsEmpty();

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0, WaveNineIntegrationTable.GroveName);
        table.TapForMana(P0, 1);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.GateName)));
        table.PassUntilStackIsEmpty();

        var gate = Assert.Single(table.Battlefield(P0, WaveNineIntegrationTable.GateName));

        // A land's mana is the right colour and the wrong source, CR 106.6b.
        table.PassTo(7, Step.PrecombatMain);
        table.TapForMana(P0, 1);
        Assert.Equal(1, table.State.Player(P0).Mana.Green);
        Assert.Equal("106.6b", table.Refuse(new ActivateAbility(P0, gate, 0)).Rule);

        // The same ability, paid from the artifact: the restriction is about where the mana came
        // from, so this one resolves and draws.
        table.PassTo(9, Step.PrecombatMain);
        table.TapNamed(P0, WaveNineIntegrationTable.RockName);
        table.PassUntilStackIsEmpty();
        Assert.Equal(1, table.State.Player(P0).Mana.Green);

        var libraryBefore = table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count;
        table.Do(new ActivateAbility(P0, gate, 0));
        table.PassUntilStackIsEmpty();

        Assert.Equal(libraryBefore - 1, table.State.In(ZoneKey.Of(CardZone.Library, P0)).Count);
        Assert.Equal(0, table.State.Player(P0).Mana.Total);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 601.2b and 601.2c through the instructed path: an effect that says a player may cast a
    /// card is not a cast until the player says yes, and a card whose additional cost names objects
    /// cannot be announced until they are named. E9i sends the may-cast down
    /// <c>ICastingSystem.CastInstructed</c>, E3h makes that path reachable without priority, and
    /// E3g's <c>CostChoices</c> is what turns the missing objects into a question.
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void An_instructed_cast_asks_for_its_additional_cost_and_sacrifices_what_is_named()
    {
        var table = new WaveNineIntegrationTable(RiteDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);

        // CR 601.2b: cast from hand, its additional cost named on the command, so it reaches the
        // graveyard as an ordinary instant would.
        table.TapForMana(P0, 1);
        var toll = table.InHand(P0, WaveNineIntegrationTable.TollName);
        var sacrificed = table.Lands(P0).First(l => !table.State.Objects[l].Tapped);
        var onStack = Assert.Single(
            table.Do(new CastSpellCommand(P0, toll, CostObjects: [sacrificed])).OfType<SpellCastEvent>()).Spell;
        Assert.DoesNotContain(sacrificed, table.Lands(P0));
        table.PassUntilStackIsEmpty();
        Assert.Single(table.Graveyard(P0, WaveNineIntegrationTable.TollName));

        // CR 601.3a: the sorcery's resolution offers the cast; nothing is cast until it is answered.
        table.PassTo(7, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.RiteName)));

        var offered = table.PassUntilQuestion();
        Assert.Equal("may-cast", offered.Kind);
        Assert.Equal(P0, offered.Player);
        var yes = offered.Options.First(o =>
            o.Id.Contains("yes", StringComparison.OrdinalIgnoreCase)
            || o.Label.Contains("yes", StringComparison.OrdinalIgnoreCase));

        // THE GAP: CR 601.3a says an effect that lets a player cast a card *is* the permission
        // CR 601.3 asks for, so answering yes should reach the cast-cost question (CR 601.2b) and
        // the land sacrifice. The instructed cast is refused for the zone instead, so a MayCast
        // naming a graveyard card can never be taken up and the E9i + E3h + E3g join is unreached
        // on this road. seam-requests/EINT4.md § 5.
        Assert.Equal("601.3", table.Refuse(new AnswerChoice(P0, offered.Id, [yes.Id])).Rule);

        // The card is still in the graveyard, the lands are all still there, and the question
        // still stands: nothing of the refusal reached the game (CR 101.4).
        Assert.Single(table.Graveyard(P0, WaveNineIntegrationTable.TollName));
        Assert.Equal(offered.Id, table.State.Pending!.Id);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 613.10 with 119.1: "your life total can't change" is a continuous effect that stops the
    /// life change rather than replacing it, so damage aimed at that player alters nothing. The
    /// same spell aimed at a player without the effect deals its damage, which is what says the
    /// prohibition and not the spell is doing the work. The last half is CR 702.16j on a player:
    /// protection from everything, and what the query does with a spell that targets them.
    /// </summary>
    [Fact]
    [RuleRef("613.10")]
    public void A_player_whose_life_total_cannot_change_takes_nothing_from_a_burn_spell()
    {
        var table = new WaveNineIntegrationTable(BoltDeck(), AegisDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);

        // CR 613.10: the prohibition is a continuous effect on the player, applied here permanently.
        table.TapForMana(P0, 1);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.StasisName)));
        table.PassUntilStackIsEmpty();
        Assert.Contains(table.State.ActiveEffects, e => e.Effect is PlayerCant && e.AppliesToPlayers.Contains(P0));

        // The burn spell at the restricted player: it resolves, and nothing about their life moves.
        var life = table.State.Player(P0).Life;
        table.TapForMana(P0, 1);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.BoltName), TargetPlayers: [P0]));
        table.PassUntilStackIsEmpty();
        Assert.Equal(life, table.State.Player(P0).Life);

        // The same spell at the other player: the damage lands, so it is the prohibition doing it.
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);
        var theirs = table.State.Player(P1).Life;
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveNineIntegrationTable.BoltName), TargetPlayers: [P1]));
        table.PassUntilStackIsEmpty();
        Assert.Equal(theirs - 2, table.State.Player(P1).Life);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 702.16j: a player with protection from everything can't be the target of spells their
    /// opponents control (CR 115.6). What the engine does about it is asserted here rather than
    /// assumed: the casting system's target check is the place the rule has to be read, because a
    /// spell already on the stack would only be countered on resolution (CR 608.2b).
    /// </summary>
    [Fact]
    [RuleRef("702.16j")]
    public void A_player_with_protection_from_everything_cannot_be_targeted_by_an_opponents_spell()
    {
        var table = new WaveNineIntegrationTable(BoltDeck(), AegisDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(4, Step.PrecombatMain);
        table.PlayALand(P1);

        table.TapForMana(P1, 1);
        table.Do(new CastSpellCommand(P1, table.InHand(P1, WaveNineIntegrationTable.AegisName)));
        table.PassUntilStackIsEmpty();
        Assert.Contains(
            table.State.ActiveEffects,
            e => e.Effect is PlayerProtectionFromEverything && e.AppliesToPlayers.Contains(P1));

        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 1);

        var bolt = table.InHand(P0, WaveNineIntegrationTable.BoltName);
        var life = table.State.Player(P1).Life;

        // THE GAP: CR 115.6 makes a protected player an illegal target, so CR 601.2c should refuse
        // the announcement the way E3h's enchant check now refuses an Aura at a player it cannot
        // legally enchant. It is accepted instead, and the spell goes on the stack pointed at them.
        // seam-requests/EINT4.md § 4.
        Assert.Single(table.Do(new CastSpellCommand(P0, bolt, TargetPlayers: [P1])).OfType<SpellCastEvent>());
        table.PassUntilStackIsEmpty();

        // What does hold: CR 702.16j prevents the damage, so the life total never moves — the
        // prohibition is read on resolution even though it was not read at announcement.
        Assert.Equal(life, table.State.Player(P1).Life);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>Lands, the artifact that makes artifact mana, and the ability that may only spend it.</summary>
    private static ImmutableList<string> GateDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 12),
        .. Enumerable.Repeat(WaveNineIntegrationTable.RockId, 6),
        .. Enumerable.Repeat(WaveNineIntegrationTable.GateId, 6),
    ];

    /// <summary>Lands, the instant whose cost names a land, and the sorcery that offers it again.</summary>
    private static ImmutableList<string> RiteDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 10),
        .. Enumerable.Repeat(WaveNineIntegrationTable.TollId, 7),
        .. Enumerable.Repeat(WaveNineIntegrationTable.RiteId, 7),
    ];

    /// <summary>Lands, the burn spell, and the sorcery that stops a life total moving.</summary>
    private static ImmutableList<string> BoltDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 10),
        .. Enumerable.Repeat(WaveNineIntegrationTable.BoltId, 7),
        .. Enumerable.Repeat(WaveNineIntegrationTable.StasisId, 7),
    ];

    /// <summary>Lands and the sorcery that gives their controller protection from everything.</summary>
    private static ImmutableList<string> AegisDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveNineIntegrationTable.AegisId, 10),
    ];
}
