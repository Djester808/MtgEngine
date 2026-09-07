using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 1's exit criterion: the subsystems, composed, play a two-player game of made-up lands
/// and vanilla creatures to a win through <c>Game.Apply</c>. Every move is a command a player
/// could send — no state is hand-built, no subsystem is faked — so a rule that only holds
/// against a fake fails here.
/// <para>
/// Turns are game turns: turn 1 and turn 3 are the starting player's first and second, turn 5
/// their third. The game is played at two life so that one connected attack ends it, CR 704.5a.
/// </para>
/// </summary>
public sealed class IntegrationTests
{
    private static PlayerId P0 => IntegrationTable.P0;

    private static PlayerId P1 => IntegrationTable.P1;

    [Fact]
    [RuleRef("104.2a")]
    public void A_game_of_lands_and_creatures_is_played_through_commands_to_a_win()
    {
        var table = new IntegrationTable();

        // CR 103.8: the starting player takes turn 1, and CR 103.8a skips their draw step.
        Assert.Equal(1, table.State.Turn.Number);
        Assert.Equal(P0, table.State.Turn.ActivePlayer);
        Assert.Equal(7, table.State.In(ZoneKey.Of(Zone.Hand, P0)).Count);

        // --- turn 1: a land, CR 305.1, and only one, CR 305.2 ---
        table.PassTo(1, Step.PrecombatMain);
        var firstLand = PlayLand(table, P0);
        Assert.Equal(Zone.Battlefield, table.State.Objects[firstLand].Zone.Zone);
        Assert.Equal(1, table.State.Player(P0).LandsPlayedThisTurn);
        Assert.Equal("305.2", table.Refuse(new PlayLand(P0, table.InHand(P0, IntegrationTable.LandName))).Rule);

        // --- turn 2: the opponent plays a land too, so nobody is ever asked to discard, CR 514.1 ---
        table.PassTo(2, Step.PrecombatMain);
        PlayLand(table, P1);

        // --- turn 3: a second land, both tapped for mana, and a creature cast with it ---
        table.PassTo(3, Step.PrecombatMain);
        PlayLand(table, P0);
        var creature = CastCreature(table, P0);
        Assert.True(table.State.Objects[creature].SummoningSick);

        // CR 302.6: it cannot attack on the turn it entered.
        table.PassTo(3, Step.DeclareAttackers);
        var tooSoon = table.Refuse(new DeclareAttackers(P0, [Attack(creature)]));
        Assert.Equal("302.6", tooSoon.Rule);
        Assert.Null(table.State.Combat);

        // --- turn 4: the opponent's second land, again to stay under the hand size ---
        table.PassTo(4, Step.PrecombatMain);
        PlayLand(table, P1);

        // --- turn 5: it attacks ---
        table.PassTo(5, Step.DeclareAttackers);

        // CR 502.3 and 302.6: the untap step of its controller's next turn cleared the sickness.
        Assert.False(table.State.Objects[creature].SummoningSick);
        var life = table.State.Player(P1).Life;

        var declared = table.Do(new DeclareAttackers(P0, [Attack(creature)]));
        Assert.Equal(creature, Assert.Single(Assert.Single(declared.OfType<AttackersDeclared>()).Attackers).Key);
        Assert.True(table.State.Objects[creature].Tapped, "CR 508.1g: an attacker without vigilance taps.");

        table.PassTo(5, Step.DeclareBlockers);
        Assert.Empty(Assert.Single(table.Do(new DeclareBlockers(P1, [])).OfType<BlockersDeclared>()).Blockers);

        // Walking into the combat damage step is what deals the damage, CR 510.1 and 703.4m.
        table.PassTo(5, Step.PostcombatMain);

        var damage = Assert.Single(table.Log.OfType<DamageDealt>(), d => d.Combat && d.ToPlayer == P1);
        Assert.Equal(2, damage.Amount);
        Assert.Equal(2, life - table.State.Player(P1).Life);
        Assert.Equal(0, table.State.Player(P1).Life);

        // CR 704.5a: 0 life loses. CR 104.2a: the last player left wins.
        Assert.Equal(P1, Assert.Single(table.Log.OfType<PlayerLost>()).Player);
        var ended = Assert.Single(table.Log.OfType<GameEnded>());
        Assert.Equal(P0, Assert.Single(ended.Outcome.Winners));
        Assert.False(ended.Outcome.Draw);
        Assert.NotNull(table.State.Outcome);

        // CR 104.1: the game is over, and nothing more is accepted.
        Assert.Equal("104.1", table.Refuse(new PassPriority(P0)).Rule);

        AssertTheLogIsTheGame(table);
    }

    [Fact]
    [RuleRef("704.5g")]
    public void A_blocked_attacker_and_its_blocker_that_deal_each_other_lethal_damage_both_die()
    {
        var table = new IntegrationTable();

        table.PassTo(1, Step.PrecombatMain);
        PlayLand(table, P0);

        table.PassTo(2, Step.PrecombatMain);
        PlayLand(table, P1);

        table.PassTo(3, Step.PrecombatMain);
        PlayLand(table, P0);
        var attacker = CastCreature(table, P0);

        table.PassTo(4, Step.PrecombatMain);
        PlayLand(table, P1);
        var blocker = CastCreature(table, P1);

        table.PassTo(5, Step.DeclareAttackers);
        table.Do(new DeclareAttackers(P0, [Attack(attacker)]));

        // CR 509.1a: summoning sickness does not stop a creature blocking, only attacking and {T}.
        Assert.True(table.State.Objects[blocker].SummoningSick);
        table.PassTo(5, Step.DeclareBlockers);
        var blocks = table.Do(new DeclareBlockers(P1, [new BlockDeclaration(blocker, attacker)]));
        Assert.Equal(blocker, Assert.Single(Assert.Single(blocks.OfType<BlockersDeclared>()).Blockers).Value.Single());

        table.PassTo(5, Step.PostcombatMain);

        // CR 510.2: both assign damage, even though each kills the other.
        var combat = table.Log.OfType<DamageDealt>().Where(d => d.Combat).ToList();
        Assert.Equal(2, combat.Count);
        Assert.Contains(combat, d => d.Source == attacker && d.ToObject == blocker && d.Amount == 2);
        Assert.Contains(combat, d => d.Source == blocker && d.ToObject == attacker && d.Amount == 2);

        // CR 510.1c: a blocked creature assigns nothing to the player it was attacking.
        Assert.Equal(2, table.State.Player(P1).Life);
        Assert.DoesNotContain(table.Log.OfType<DamageDealt>(), d => d.ToPlayer is not null);

        // CR 704.5g: lethal damage destroys both, and CR 400.7 makes each a new object in a graveyard.
        Assert.Equal(2, table.Log.OfType<ObjectLeftBy>().Count(e => e.Verb == "destroyed"));
        Assert.DoesNotContain(attacker, table.State.Objects.Keys);
        Assert.DoesNotContain(blocker, table.State.Objects.Keys);
        Assert.Empty(table.Battlefield(P0, IntegrationTable.BeastName));
        Assert.Empty(table.Battlefield(P1, IntegrationTable.BeastName));
        Assert.Equal(1, InGraveyard(table, P0));
        Assert.Equal(1, InGraveyard(table, P1));

        // CR 104.2a: nobody has lost, so the game goes on.
        Assert.Null(table.State.Outcome);

        AssertTheLogIsTheGame(table);
    }

    private static AttackDeclaration Attack(ObjectId creature) => new(creature, new CombatTarget(P1, null));

    private static IEnumerable<ObjectId> Attackers(GameState state) =>
        state.Combat is null ? [] : state.Combat.Attackers.Keys.OrderBy(o => o.Value);

    private static int InGraveyard(IntegrationTable table, PlayerId owner) =>
        table.State.Objects.Values.Count(o =>
            o.Zone.Zone == Zone.Graveyard && o.Owner == owner && o.Definition.Name == IntegrationTable.BeastName);

    /// <summary>Play a land from hand and return the permanent it became, CR 400.7.</summary>
    private static ObjectId PlayLand(IntegrationTable table, PlayerId player)
    {
        var card = table.InHand(player, IntegrationTable.LandName);
        var played = table.Do(new PlayLand(player, card));
        return Assert.Single(played.OfType<LandPlayed>()).Land;
    }

    /// <summary>
    /// Tap every land for {G}, cast the creature with the mana in the pool, and pass until it
    /// resolves. CR 605.3a for the mana abilities, CR 601.2h for the payment, CR 608.3 for the
    /// permanent the spell becomes.
    /// </summary>
    private static ObjectId CastCreature(IntegrationTable table, PlayerId player)
    {
        var lands = table.Battlefield(player, IntegrationTable.LandName);
        Assert.Equal(2, lands.Count);
        foreach (var land in lands)
        {
            var activated = Assert.Single(table.Do(new ActivateAbility(player, land, 0)).OfType<AbilityActivated>());

            // CR 605.3a: a mana ability does not use the stack, so there is no object for it.
            Assert.Null(activated.StackObject);
        }

        Assert.Equal(2, table.State.Player(player).Mana.Green);
        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Stack)));

        var card = table.InHand(player, IntegrationTable.BeastName);
        var cast = table.Do(new CastSpell(player, card));
        var spell = IntegrationTable.Became(cast, card);
        Assert.Equal(spell, Assert.Single(cast.OfType<SpellCast>()).Spell);
        Assert.Equal(Zone.Stack, table.State.Objects[spell].Zone.Zone);

        // CR 601.2h: the cost was paid out of the pool the mana abilities filled.
        Assert.Equal(0, table.State.Player(player).Mana.Total);

        var resolved = table.PassRound();
        Assert.Equal(spell, Assert.Single(resolved.OfType<StackObjectResolved>()).StackObject);
        var permanent = IntegrationTable.Became(resolved, spell);
        Assert.Equal(Zone.Battlefield, table.State.Objects[permanent].Zone.Zone);
        return permanent;
    }

    /// <summary>
    /// The log is the game: written, read back, and folded from nothing, it is the state the
    /// engine is holding. Both halves matter — a log that will not round-trip cannot be stored,
    /// and a replay that disagrees means some subsystem changed state without an event.
    /// </summary>
    private static void AssertTheLogIsTheGame(IntegrationTable table)
    {
        // The fold: the whole log, replayed from nothing, is the state the engine is holding.
        Assert.Equal(Describe(table.State), Describe(GameReducer.Replay(table.Log)));

        // The store: written and read back, the whole log — combat declarations included, which
        // are keyed by object id — folds to the same game.
        var storable = table.Log;
        Assert.Contains(storable, e => e is AttackersDeclared);

        var json = EventLogSerializer.Write(storable);
        var read = EventLogSerializer.Read(json);

        Assert.Equal(storable.Count, read.Count);
        Assert.Equal(json, EventLogSerializer.Write(read));
        Assert.Equal(Describe(GameReducer.Replay(storable)), Describe(GameReducer.Replay(read)));
    }

    /// <summary>
    /// Everything about a state that a player could see or a rule could ask, as text, so a
    /// mismatch reads as a diff rather than as "objects are not equal".
    /// </summary>
    private static string Describe(GameState state) => string.Join(
        "\n",
        [
            $"turn {state.Turn.Number} {state.Turn.Step} active {state.Turn.ActivePlayer} order {state.Turn.TurnOrderIndex}",
            $"priority {state.Priority.Holder} passed [{string.Join(",", state.Priority.PassedInSuccession)}]",
            $"combat {state.Combat?.AttackingPlayer.ToString() ?? "none"} attackers [{string.Join(",", Attackers(state))}]",
            $"outcome {(state.Outcome is null ? "none" : $"{string.Join(",", state.Outcome.Winners)} draw {state.Outcome.Draw}")}",
            $"next object {state.NextObjectId} timestamp {state.NextTimestamp} choice {state.NextChoiceId}",
            .. state.Players.Select(p => $"player {p.Id} life {p.Life} lost {p.HasLost} mana {p.Mana.Total} lands {p.LandsPlayedThisTurn} drawn {p.ObjectsDrawnThisTurn}"),
            .. state.Objects.Values.OrderBy(o => o.Id.Value).Select(o =>
                $"object {o.Id} {o.Definition.Name} in {o.Zone.Zone}/{o.Zone.Player} owner {o.Owner} tapped {o.Tapped} sick {o.SummoningSick} damage {o.Damage}"),
            .. state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1).Select(z =>
                $"zone {z.Key.Zone}/{z.Key.Player} [{string.Join(",", z.Value)}]"),
        ]);
}
