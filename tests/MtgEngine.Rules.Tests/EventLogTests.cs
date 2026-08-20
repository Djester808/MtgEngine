using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The log is the game and the state is a fold of it.
/// </summary>
/// <remarks>
/// This is the property the engine was rebuilt to have. If it holds, a game reported as broken
/// can be replayed exactly and pasted into a test; if it stops holding, every other guarantee
/// here is worth less, which is why it is asserted after a mixed run of actions rather than on
/// a fresh game.
/// </remarks>
public sealed class EventLogTests
{
    [Fact]
    public void Replaying_the_log_reproduces_the_state()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();

        game.Draw(alice);
        game.Draw(alice);
        game.Draw(bob);
        var creature = game.Move(
            game.State.GetPlayer(alice).Hand[0], Zone.Battlefield, MoveCause.Play);
        game.ChangeLife(bob, -3);
        game.Move(creature, Zone.Graveyard, MoveCause.Destroy);
        game.ChangeLife(alice, 2);

        var replayed = GameReducer.Replay(game.Log);

        Assert.Equal(game.State, replayed);
    }

    [Fact]
    public void A_log_has_to_begin_with_the_game_starting()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GameReducer.Replay([new LifeChanged(Guid.NewGuid(), -1, 19)]));

        Assert.Contains("GameStarted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_log_is_not_a_game()
    {
        Assert.Throws<InvalidOperationException>(() => GameReducer.Replay([]));
    }

    [Fact]
    public void A_rejected_action_leaves_no_trace_in_the_log()
    {
        // Events say what happened, never what was asked for. A move of something that is not
        // there fails before anything is recorded, so the log stays replayable.
        var (game, _, _) = TestCards.TwoPlayer();
        var before = game.Log.Count;

        Assert.Throws<InvalidOperationException>(() => game.Move(ObjectId.New(), Zone.Hand));

        Assert.Equal(before, game.Log.Count);
        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    [Fact]
    public void Changing_life_by_nothing_records_nothing()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.Log.Count;

        game.ChangeLife(alice, 0);

        Assert.Equal(before, game.Log.Count);
    }

    [Fact]
    public void Events_cite_the_rule_they_answer_to()
    {
        // The Comprehensive Rules are a live asset in this repo, so a log line can be traced to
        // the sentence that caused it rather than to a comment about it.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Move(game.State.GetPlayer(alice).Library[0], Zone.Hand, MoveCause.Draw);

        var moved = game.Log.OfType<ObjectMoved>().Last();

        Assert.Equal("400.7", moved.Rule);
    }

    [Fact]
    public void The_game_starts_with_libraries_dealt_and_shuffled()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 10);

        Assert.IsType<GameStarted>(game.Log[0]);
        Assert.Equal(2, game.Log.OfType<LibraryShuffled>().Count());
        Assert.Equal(10, game.State.GetPlayer(alice).Library.Count);
        Assert.Equal(10, game.State.GetPlayer(bob).Library.Count);
        // CR 103.5: opening hands are part of the mulligan procedure, which needs priority.
        Assert.Empty(game.State.GetPlayer(alice).Hand);
    }

    [Fact]
    public void The_game_has_not_reached_turn_one_until_a_turn_begins()
    {
        var (game, _, _) = TestCards.TwoPlayer();

        Assert.Equal(0, game.State.TurnNumber);
    }
}
