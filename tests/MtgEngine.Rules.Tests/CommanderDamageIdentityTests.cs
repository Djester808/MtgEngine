using MtgEngine.Cards;
using MtgEngine.Rules.Commander;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 903.10a counts commander damage "over the course of the game", and CR 400.7 makes a new
/// object every time a commander changes zone. Keyed by <see cref="ObjectId"/>, the running
/// total therefore started again on every recast: a player could take 14 from a commander,
/// kill it, take 14 more from the same card and never lose.
///
/// The key is now <see cref="CommanderIdentity"/> — owner plus card — which is what "the same
/// commander" means. The owner is part of it because two players may lead the same card.
/// </summary>
public sealed class CommanderDamageIdentityTests
{
    private static readonly PlayerId Alice = new(0);
    private static readonly PlayerId Bob = new(1);

    private static CardDefinition General(string name) =>
        Board.Definition(name, CardType.Creature, 3, 3, Supertype.Legendary);

    /// <summary>The failure this file exists for.</summary>
    [Fact]
    [RuleRef("903.10a")]
    public void Damage_from_the_same_commander_survives_a_recast()
    {
        var board = new Board(players: 2, life: 40);
        var card = General("cmdr general");

        // One card, two objects, because it died in between (CR 400.7).
        var first = board.Put(card, owner: 1);
        board.Fold(new DamageDealt(first, null, Alice, 14, Combat: true, FromCommander: true));
        board.Fold(new ObjectCeasedToExist(first));

        var second = board.Put(card, owner: 1);
        board.Fold(new DamageDealt(second, null, Alice, 14, Combat: true, FromCommander: true));

        var total = Assert.Single(board.State.Player(Alice).CommanderDamage);
        Assert.Equal(28, total.Value);
        Assert.NotNull(CommanderDamage.LethalSource(board.State.Player(Alice)));
    }

    [Fact]
    [RuleRef("903.10a")]
    public void Two_players_leading_the_same_card_are_counted_apart()
    {
        var board = new Board(players: 2, life: 40);
        var card = General("cmdr twin");

        var theirs = board.Put(card, owner: 1);
        board.Fold(new DamageDealt(theirs, null, Alice, 14, Combat: true, FromCommander: true));

        // The same CARD owned by a different player is a different commander, so its damage
        // must not join the first one's total.
        var mine = board.Put(card, owner: 0);
        board.Fold(new DamageDealt(mine, null, Alice, 14, Combat: true, FromCommander: true));

        Assert.Equal(2, board.State.Player(Alice).CommanderDamage.Count);
        Assert.Null(CommanderDamage.LethalSource(board.State.Player(Alice)));
    }

    [Fact]
    [RuleRef("903.10a")]
    public void Damage_that_is_not_from_a_commander_is_not_counted()
    {
        var board = new Board(players: 2, life: 40);
        var beast = board.Put(Board.Creature(3, 3, "cmdr beast"), owner: 1);

        board.Fold(new DamageDealt(beast, null, Alice, 30, Combat: true, FromCommander: false));

        Assert.Empty(board.State.Player(Alice).CommanderDamage);
        Assert.Null(CommanderDamage.LethalSource(board.State.Player(Alice)));
    }

    [Fact]
    [RuleRef("903.10a")]
    public void Twenty_one_from_one_commander_is_lethal_and_twenty_is_not()
    {
        var board = new Board(players: 2, life: 40);
        var card = General("cmdr lethal");
        var general = board.Put(card, owner: 1);

        board.Fold(new DamageDealt(general, null, Alice, 20, Combat: true, FromCommander: true));
        Assert.Null(CommanderDamage.LethalSource(board.State.Player(Alice)));

        board.Fold(new DamageDealt(general, null, Alice, 1, Combat: true, FromCommander: true));
        var who = Assert.NotNull(CommanderDamage.LethalSource(board.State.Player(Alice)));
        Assert.Equal(Bob, who.Owner);
        Assert.Equal("cmdr lethal", who.OracleId);
    }
}
