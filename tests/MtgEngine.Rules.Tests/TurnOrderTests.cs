using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Turn order, and the absence of any two-player assumption (CR 101.4, 103.5).
/// </summary>
/// <remarks>
/// The engine this replaces asked <c>OpponentOf(playerId)</c> and branched on "am I the active
/// player". Both are meaningless past a duel, and priority was built on them, so supporting a
/// third player would have meant rewriting it. Two players is the first format and the seating
/// list is the only thing anything asks — these tests exist to keep it that way while there is
/// still nothing to unpick.
/// </remarks>
public sealed class TurnOrderTests
{
    private static Game FourPlayer(out Guid[] seats)
    {
        seats =
        [
            Guid.Parse("11111111-0000-0000-0000-000000000000"),
            Guid.Parse("22222222-0000-0000-0000-000000000000"),
            Guid.Parse("33333333-0000-0000-0000-000000000000"),
            Guid.Parse("44444444-0000-0000-0000-000000000000"),
        ];

        var setups = seats
            .Select((id, i) => new PlayerSetup(id, $"P{i + 1}", 40, TestCards.Deck(5, $"P{i + 1}")))
            .ToList();

        return Game.Start(Guid.NewGuid(), setups, new GameRandom(4), startingPlayerId: seats[1]);
    }

    [Fact]
    public void Seating_order_is_the_order_players_were_given()
    {
        var game = FourPlayer(out var seats);

        Assert.Equal(seats, game.State.TurnOrder);
    }

    [Fact]
    public void Apnap_order_starts_from_the_active_player_and_wraps()
    {
        // CR 101.4. Active player first, then everyone else in turn order.
        var game = FourPlayer(out var seats);

        Assert.Equal(
            [seats[1], seats[2], seats[3], seats[0]],
            game.State.ApnapOrder().ToArray());
    }

    [Fact]
    public void Turn_order_can_be_walked_from_any_player()
    {
        var game = FourPlayer(out var seats);

        Assert.Equal(
            [seats[3], seats[0], seats[1], seats[2]],
            game.State.PlayersFrom(seats[3]).ToArray());
    }

    [Fact]
    public void Walking_from_a_player_who_is_not_seated_is_an_error()
    {
        var game = FourPlayer(out _);

        Assert.Throws<InvalidOperationException>(
            () => game.State.PlayersFrom(Guid.NewGuid()).ToList());
    }

    [Fact]
    public void A_two_player_game_is_just_a_seating_list_of_two()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();

        Assert.Equal([alice, bob], game.State.ApnapOrder().ToArray());
    }

    [Fact]
    public void A_game_needs_at_least_two_players()
    {
        var solo = new List<PlayerSetup>
        {
            new(Guid.NewGuid(), "Alone", 20, TestCards.Deck(5)),
        };

        Assert.Throws<ArgumentException>(
            () => Game.Start(Guid.NewGuid(), solo, new GameRandom(1)));
    }

    [Fact]
    public void Players_who_have_lost_drop_out_of_the_active_list()
    {
        // CR 104.2. They stay seated so the log still makes sense, but they are not asked for
        // priority and do not take turns.
        var game = FourPlayer(out var seats);
        var loser = game.State.GetPlayer(seats[2]);
        var afterLoss = game.State.WithPlayer(loser with { HasLost = true });

        Assert.Equal(
            [seats[0], seats[1], seats[3]],
            afterLoss.ActivePlayers().ToArray());
    }
}
