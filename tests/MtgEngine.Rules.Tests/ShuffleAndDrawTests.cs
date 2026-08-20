using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Shuffling, drawing, and the loss that a draw does not cause.
/// </summary>
public sealed class ShuffleAndDrawTests
{
    [Fact]
    public void The_same_seed_produces_the_same_shuffle()
    {
        var a = TestCards.TwoPlayer(deckSize: 40, seed: 12345);
        var b = TestCards.TwoPlayer(deckSize: 40, seed: 12345);

        Assert.Equal(
            Names(a.Game, a.Alice),
            Names(b.Game, b.Alice));
    }

    [Fact]
    public void Different_seeds_produce_different_shuffles()
    {
        var a = TestCards.TwoPlayer(deckSize: 40, seed: 1);
        var b = TestCards.TwoPlayer(deckSize: 40, seed: 2);

        Assert.NotEqual(
            Names(a.Game, a.Alice),
            Names(b.Game, b.Alice));
    }

    [Fact]
    public void A_shuffle_keeps_every_card()
    {
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40, seed: 7);

        var names = Names(game, alice);

        Assert.Equal(40, names.Count);
        Assert.Equal(40, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_shuffled_order_is_recorded_rather_than_the_seed()
    {
        // A seed only reproduces a shuffle while the algorithm never changes; the resulting
        // order reproduces it forever, which is what a stored game needs.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40, seed: 3);

        var shuffle = game.Log.OfType<LibraryShuffled>().First(s => s.PlayerId == alice);

        Assert.Equal(game.State.GetPlayer(alice).Library, shuffle.Order);
    }

    [Fact]
    public void Drawing_takes_the_top_card_of_the_library()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var top = game.State.GetPlayer(alice).Library[0];
        var expected = game.State.GetObject(top).Card.Name;

        var drawn = game.Draw(alice);

        Assert.NotNull(drawn);
        Assert.Equal(expected, game.State.GetObject(drawn.Value).Card.Name);
        Assert.Equal([drawn.Value], game.State.GetPlayer(alice).Hand);
    }

    [Fact]
    public void Drawing_from_an_empty_library_does_not_lose_the_game_on_the_spot()
    {
        // CR 121.4 and 704.5b. The draw does not happen and the attempt is remembered; the loss
        // is a state-based action checked later, so an effect has a window to replace it.
        //
        // The engine this replaces wrote `Library.IsEmpty && false` here with a comment saying
        // the real check lived in the rules engine. It lived nowhere, and a player could deck
        // out and keep playing.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 1);
        game.Draw(alice);

        var drawn = game.Draw(alice);

        Assert.Null(drawn);
        Assert.True(game.State.GetPlayer(alice).HasAttemptedDrawFromEmptyLibrary);
        Assert.False(game.State.GetPlayer(alice).HasLost);
        Assert.Contains(game.Log, e => e is DrawFromEmptyLibraryAttempted);
    }

    [Fact]
    public void Life_changes_do_not_kill_a_player_on_the_spot_either()
    {
        // CR 704.5a. Zero or less life is a loss at the next state-based action check, not here.
        var (game, alice, _) = TestCards.TwoPlayer();

        game.ChangeLife(alice, -25);

        Assert.Equal(-5, game.State.GetPlayer(alice).Life);
        Assert.False(game.State.GetPlayer(alice).HasLost);
    }

    private static List<string> Names(Game game, Guid playerId) =>
        [.. game.State.GetPlayer(playerId).Library.Select(id => game.State.GetObject(id).Card.Name)];
}
