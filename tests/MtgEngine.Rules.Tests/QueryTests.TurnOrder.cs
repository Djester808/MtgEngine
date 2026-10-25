using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 101.4: "the active player makes any choices required, then each other player in turn
/// order does the same" — APNAP.
///
/// <c>EachPlayer</c> and <c>EachOpponent</c> returned seat order, so at a table whose active
/// player is not seat 0 an "each player" effect asked seat 0 first and the active player last —
/// the information order the rule exists to fix. The same file already had
/// <c>TurnOrderIndex</c> with the right semantics and used it for one selector only.
/// </summary>
public sealed partial class QueryTests
{
    /// <summary>A four-player table with the active player at seat 2, and a context to match.</summary>
    private static World FourWithActiveSeatTwo()
    {
        var world = World.New(4);
        var state = world.State with
        {
            Turn = world.State.Turn with { ActivePlayer = new PlayerId(2), TurnOrderIndex = 2 },
        };
        return world with { State = state, Context = world.Context with { Controller = new PlayerId(2) } };
    }

    [Fact]
    [RuleRef("101.4")]
    public void Each_player_is_asked_in_turn_order_from_the_active_player()
    {
        var world = FourWithActiveSeatTwo();

        var asked = world.Query.Players(world.State, new EachPlayer(), world.Context);

        Assert.Equal([new PlayerId(2), new PlayerId(3), new PlayerId(0), new PlayerId(1)], asked);
    }

    [Fact]
    [RuleRef("101.4")]
    public void Each_opponent_keeps_turn_order_too()
    {
        var world = FourWithActiveSeatTwo();

        var asked = world.Query.Players(world.State, new EachOpponent(), world.Context);

        Assert.Equal([new PlayerId(3), new PlayerId(0), new PlayerId(1)], asked);
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_player_who_has_left_is_not_asked_at_all()
    {
        var world = FourWithActiveSeatTwo();
        var state = new PlayerLost(new PlayerId(3), "conceded").Apply(world.State);

        var asked = world.Query.Players(state, new EachPlayer(), world.Context);

        Assert.Equal([new PlayerId(2), new PlayerId(0), new PlayerId(1)], asked);
    }
}
