using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A move is not a creation.
///
/// <c>ObjectMoved.Apply</c> composes <c>ObjectCreated.Apply</c> to place the arriving object,
/// and inherited its CREATION history with it: a token that merely changed zone was written
/// into <c>TurnHistory.TokensCreated</c> as though it had just been made — so "if you created a
/// token this turn" answered true on a turn when the only thing that happened to a token was
/// dying — and its battlefield entry was appended twice, once by the nested create and once by
/// the move.
/// </summary>
public sealed class ObjectMovedHistoryTests
{
    private static readonly PlayerId Alice = new(0);

    private static (GameState State, ObjectId Id) TokenOnBattlefield()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(1, 1, "moved soldier"), token: true);
        return (board.State, id);
    }

    [Fact]
    [RuleRef("111.1")]
    public void Moving_a_token_does_not_record_it_as_newly_created()
    {
        var (state, id) = TokenOnBattlefield();

        // The turn rolls over, so the creation this token really did have is forgotten.
        state = state with { History = TurnHistory.Empty };
        Assert.Empty(state.History.TokensCreated);

        var token = state.Objects[id];
        var gone = token with { Id = new ObjectId(state.NextObjectId), Zone = ZoneKey.Of(Zone.Graveyard, Alice) };
        state = new ObjectMoved(id, gone, -1, token).Apply(state);

        Assert.Empty(state.History.TokensCreated);
    }

    [Fact]
    [RuleRef("400.7")]
    public void An_object_arriving_on_the_battlefield_is_recorded_once()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(2, 2, "moved beast"), Zone.Hand);
        var state = board.State with { History = TurnHistory.Empty };

        var card = state.Objects[id];
        var arrived = card with { Id = new ObjectId(state.NextObjectId), Zone = ZoneKey.Shared(Zone.Battlefield) };
        state = new ObjectMoved(id, arrived, -1, card).Apply(state);

        Assert.Single(state.History.EnteredBattlefield);
        Assert.Equal(arrived.Id, state.History.EnteredBattlefield[0]);
    }

    [Fact]
    [RuleRef("111.1")]
    public void A_token_that_is_really_created_is_still_recorded()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1, "made soldier"), token: true);

        var made = Assert.Single(board.State.History.TokensCreated);
        Assert.Equal(Alice, made.Key);
        Assert.Single(made.Value);
        Assert.Single(board.State.History.EnteredBattlefield);
    }
}
