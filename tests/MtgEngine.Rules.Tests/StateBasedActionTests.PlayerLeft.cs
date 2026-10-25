using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 800.4a's object half: "all objects owned by that player leave the game".
///
/// The engine honoured the rule's other half everywhere — a lost player is no longer a player,
/// which Query.Selectors, Query.Targets, Retarget and the state-based actions all read — but
/// nothing removed the objects. CR 704.5 steps over a departed player's permanents on the
/// grounds that 800.4 takes them (StateBasedActions.Permanents.cs), so with 800.4 unimplemented
/// those permanents were both permanent AND exempt from every state-based action: a creature
/// with no toughness left could not die, and every "each creature" query still counted it.
///
/// These assert the BEHAVIOUR, not the flag. The gap survived because what tests existed
/// checked <c>HasLost</c> and stopped there.
/// </summary>
public sealed partial class StateBasedActionTests
{
    [Fact]
    [RuleRef("800.4a")]
    public void A_lost_players_permanents_leave_the_game_with_them()
    {
        var board = new Board(players: 3);
        board.Put(Board.Creature(2, 2, "sba departing"), owner: 1);
        board.Put(Board.Creature(2, 2, "sba staying"), owner: 0);

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.DoesNotContain(after.Objects.Values, o => o.Face.Name == "sba departing");
        Assert.Contains(after.Objects.Values, o => o.Face.Name == "sba staying");
    }

    [Fact]
    [RuleRef("800.4a")]
    public void Their_objects_leave_every_zone_and_no_zone_keeps_a_dangling_id()
    {
        var board = new Board(players: 2);
        board.Put(Board.Creature(1, 1, "sba in hand"), Zone.Hand, owner: 1);
        board.Put(Board.Creature(1, 1, "sba in graveyard"), Zone.Graveyard, owner: 1);
        board.Put(Board.Creature(1, 1, "sba on battlefield"), Zone.Battlefield, owner: 1);

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.DoesNotContain(after.Objects.Values, o => o.Owner == new PlayerId(1));
        foreach (var zone in after.Zones)
        {
            Assert.DoesNotContain(zone.Value, id => !after.Objects.ContainsKey(id));
        }
    }

    /// <summary>
    /// The failure this file exists for: the creature is at zero toughness and its owner loses.
    /// CR 704.5f cannot bury it, because 704.5 steps over a departed player's objects — so
    /// before 800.4a was implemented it stayed on the battlefield for the rest of the game,
    /// immune to every later check.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void A_dying_creature_does_not_outlive_its_owner()
    {
        var board = new Board(players: 3);
        board.Put(Board.Creature(2, 2, "sba doomed"), owner: 1, counters: (Counters.MinusOneMinusOne, 2));

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.DoesNotContain(sba.Check(after).State.Objects.Values, o => o.Face.Name == "sba doomed");
    }

    [Fact]
    [RuleRef("800.4a")]
    public void A_player_still_in_the_game_keeps_everything()
    {
        var board = new Board(players: 2);
        board.Put(Board.Creature(2, 2, "sba kept"), owner: 0);
        int before = board.State.Objects.Count;

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.Equal(before, after.Objects.Count);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 800.4a steps 2 to 4 — the control the departed player held, not the objects they owned.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// CR 800.4a step 4, and the rule's own Bribery example: a creature put onto the battlefield
    /// under the departed player's control, owned by somebody still playing, is EXILED. Step 1
    /// cannot take it — they do not own it — so with only step 1 built it stayed on the battlefield
    /// under a seat that is no longer a player, and CR 704.5 steps over it for ever.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void A_permanent_they_controlled_but_did_not_own_is_exiled()
    {
        var board = new Board(players: 3);
        board.Put(Board.Creature(2, 2, "sba bribed"), owner: 0, controller: 1);

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        var moved = Assert.Single(after.Objects.Values, o => o.Face.Name == "sba bribed");
        Assert.Equal(Zone.Exile, moved.Zone.Zone);
        Assert.Equal(new PlayerId(0), moved.Owner);
        Assert.Empty(after.In(ZoneKey.Shared(Zone.Battlefield)));
    }

    /// <summary>
    /// CR 800.4a step 2 before step 4, and the rule's Act of Treason example: the effect that GAVE
    /// them control ends first, so the creature goes back to the player who owns it rather than
    /// being exiled along with the seat that borrowed it.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void A_creature_they_only_borrowed_goes_back_instead_of_being_exiled()
    {
        var board = new Board(players: 3);
        var borrowed = board.Put(Board.Creature(2, 2, "sba borrowed"), owner: 0);
        board.Fold(new ContinuousEffectCreated(new ActiveEffect(
            1,
            new ControlBy(new You()),
            null,
            new PlayerId(1),
            [borrowed],
            [],
            new UntilEndOfTurn(),
            board.State.NextTimestamp,
            FromStaticAbility: false)));

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        var kept = Assert.Single(after.Objects.Values, o => o.Face.Name == "sba borrowed");
        Assert.Equal(Zone.Battlefield, kept.Zone.Zone);
        Assert.Empty(after.ActiveEffects);
    }

    /// <summary>
    /// CR 800.4a step 3: an ability of theirs waiting on the stack is not a card, so it ceases to
    /// exist rather than being exiled. The ability belongs to a permanent somebody else owns,
    /// which is the only way it survives step 1 at all.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void An_ability_of_theirs_on_the_stack_ceases_to_exist()
    {
        var board = new Board(players: 3);
        var source = board.Put(Board.Creature(2, 2, "sba source"), owner: 0);
        board.Put(
            Board.Creature(0, 0, "sba ability"),
            Zone.Stack,
            owner: 0,
            stack: new StackEntry(source, 0, [], [], [], null, false),
            controller: 1);

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.DoesNotContain(after.Objects.Values, o => o.Face.Name == "sba ability");
        Assert.Empty(after.In(ZoneKey.Shared(Zone.Stack)));
    }

    /// <summary>
    /// CR 800.4a step 4 on the stack: a SPELL they control but do not own is a card, so step 3
    /// leaves it and step 4 exiles it.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void A_spell_of_theirs_on_the_stack_is_exiled_rather_than_forgotten()
    {
        var board = new Board(players: 3);
        board.Put(
            Board.Creature(2, 2, "sba spell"),
            Zone.Stack,
            owner: 0,
            stack: new StackEntry(null, null, [], [], [], null, false),
            controller: 1);

        var after = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        var moved = Assert.Single(after.Objects.Values, o => o.Face.Name == "sba spell");
        Assert.Equal(Zone.Exile, moved.Zone.Zone);
    }

    /// <summary>
    /// CR 800.4a exiles more than one object at a time, and step 4 MINTS objects (CR 400.7). A
    /// fold that minted them in a different order on a replay would rebuild a different game, so
    /// the same events folded twice have to give the same ids.
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void Exiling_what_they_controlled_folds_the_same_way_twice()
    {
        var board = new Board(players: 3);
        board.Put(Board.Creature(2, 2, "sba one"), owner: 0, controller: 1);
        board.Put(Board.Creature(3, 3, "sba two"), owner: 2, controller: 1);
        board.Put(Board.Creature(4, 4, "sba three"), owner: 0, controller: 1);

        var once = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);
        var again = new PlayerLost(new PlayerId(1), "conceded").Apply(board.State);

        Assert.Equal(
            once.Objects.Values.Select(o => (o.Id, o.Face.Name, o.Zone)).OrderBy(o => o.Id.Value),
            again.Objects.Values.Select(o => (o.Id, o.Face.Name, o.Zone)).OrderBy(o => o.Id.Value));
        Assert.Equal(3, once.In(ZoneKey.Shared(Zone.Exile)).Count);
    }
}
