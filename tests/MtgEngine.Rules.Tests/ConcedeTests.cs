namespace MtgEngine.Rules.Tests;

/// <summary>
/// Conceding (CR 104.3a).
/// </summary>
/// <remarks>
/// The one action with no timing rules attached: a player may concede at any time, whoever has
/// priority and whatever the game is in the middle of, and they leave immediately. Everything
/// else a player does is a game action taken with priority; this is a player walking away from
/// the table, so it is the one command that does not check for it.
/// </remarks>
public sealed class ConcedeTests
{
    [Fact]
    public void A_player_who_concedes_has_lost()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.Concede(alice);

        Assert.True(game.State.GetPlayer(alice).HasLost);
        Assert.Equal("conceded", game.State.GetPlayer(alice).LossReason);
    }

    [Fact]
    public void The_last_player_standing_wins()
    {
        // CR 104.2a: a player still in a two-player game when the other leaves wins it.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.Concede(alice);

        Assert.True(game.State.IsOver);
        Assert.Equal(bob, game.State.WinnerId);
    }

    [Fact]
    public void Conceding_needs_no_priority()
    {
        // The whole point of the rule. Whoever holds priority, either player may leave.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        var holder = game.State.Priority.Holder;
        var other = holder == alice ? bob : alice;

        game.Concede(other);

        Assert.True(game.State.GetPlayer(other).HasLost);
    }

    [Fact]
    public void Conceding_is_recorded_with_the_rule_that_allows_it()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.Concede(alice);

        var lost = game.Log.OfType<Events.PlayerLost>().Single(e => e.PlayerId == alice);
        Assert.Equal("104.3a", lost.Rule);
    }

    [Fact]
    public void A_question_put_to_a_player_who_leaves_goes_with_them()
    {
        // Otherwise the game sits forever holding a decision nobody can answer: the engine waits
        // on a choice, and the player it belongs to is no longer in the game.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: true);

        Assert.NotNull(game.State.Choice);
        var asked = game.State.Choice!.PlayerId;

        game.Concede(asked);

        Assert.Null(game.State.Choice);
    }

    [Fact]
    public void Conceding_twice_changes_nothing()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.Concede(alice);
        var after = game.Log.Count;
        game.Concede(alice);

        Assert.Equal(after, game.Log.Count);
    }

    [Fact]
    public void Nothing_happens_once_the_game_is_over()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.Concede(alice);
        var after = game.Log.Count;
        game.Concede(bob);

        Assert.Equal(after, game.Log.Count);
        Assert.Equal(bob, game.State.WinnerId);
    }

    [Fact]
    public void The_log_still_replays_to_the_same_state()
    {
        // The property the whole engine rests on: state is a fold of the log, so a game that
        // ended by concession has to rebuild from its own events like any other.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);
        game.Concede(alice);

        var replayed = Engine.GameReducer.Replay(game.Log);

        Assert.Equal(game.State, replayed);
    }
}
