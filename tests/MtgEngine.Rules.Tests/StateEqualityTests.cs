using System.Collections.Immutable;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// That two game states compare by what they describe, not by which objects hold them.
/// </summary>
/// <remarks>
/// These are the negative controls for <see cref="EventLogTests.Replaying_the_log_reproduces_the_state"/>.
/// That test compares two states for equality, so an equality that answered "yes" to everything
/// would pass it — and the generated record equality, which compares immutable collections by
/// reference, answered "no" to everything instead and made it fail for the wrong reason. Both
/// directions have to be pinned or the replay guarantee is decoration.
/// </remarks>
public sealed class StateEqualityTests
{
    [Fact]
    public void A_state_equals_itself_rebuilt_from_its_own_log()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    [Fact]
    public void A_replay_that_stops_early_does_not_equal_the_finished_state()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);
        game.Draw(alice);

        var oneEventShort = GameReducer.Replay(game.Log.Take(game.Log.Count - 1));

        Assert.NotEqual(game.State, oneEventShort);
    }

    [Fact]
    public void Moving_one_card_makes_the_state_different()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.State;

        game.Draw(alice);

        Assert.NotEqual(before, game.State);
    }

    [Fact]
    public void A_different_life_total_makes_the_state_different()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.State;

        game.ChangeLife(alice, -1);

        Assert.NotEqual(before, game.State);
    }

    [Fact]
    public void The_order_of_a_zone_is_part_of_the_state()
    {
        // CR 400.5: order in a library, graveyard or on the stack is not free to change. Two
        // libraries holding the same cards in a different order are different positions.
        var (game, alice, _) = TestCards.TwoPlayer();
        var player = game.State.GetPlayer(alice);

        var reversed = game.State.WithPlayer(player with { Library = [.. player.Library.Reverse()] });

        Assert.NotEqual(game.State, reversed);
    }

    [Fact]
    public void A_tapped_permanent_is_not_equal_to_an_untapped_one()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var id = game.Move(game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);
        var before = game.State;

        var permanent = before.GetObject(id);
        var tapped = before.WithObject(
            permanent with { Permanent = permanent.Permanent! with { IsTapped = true } });

        Assert.NotEqual(before, tapped);
    }

    [Fact]
    public void Counters_are_compared_by_content()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var id = game.Move(game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);
        var permanent = game.State.GetObject(id);

        var one = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 2 }.ToImmutableDictionary() };
        var same = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 2 }.ToImmutableDictionary() };
        var other = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 3 }.ToImmutableDictionary() };

        Assert.Equal(one, same);
        Assert.NotEqual(one, other);
    }

    [Fact]
    public void Equal_states_agree_on_their_hash_code()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);

        var replayed = GameReducer.Replay(game.Log);

        Assert.Equal(game.State.GetHashCode(), replayed.GetHashCode());
    }
}
