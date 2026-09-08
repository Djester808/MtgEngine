using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>The per-turn counts the state keeps for the query (CR 121.1), after the wave-25 join E5j asked for.</summary>
public sealed class TurnCountTests
{
    private static GameState Started() =>
        GameReducer.Replay([new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 42, StartingLife: 20, Format: "test")]);

    [Fact]
    [RuleRef("111.7")]
    public void A_token_land_that_stopped_existing_still_counts_as_having_entered_this_turn()
    {
        var state = Started();
        var seat = new PlayerId(0);
        var id = new ObjectId(state.NextObjectId);
        var definition = new CardDefinition("Token", "Token", Layout.Normal, [new Face("Token", FaceRole.Main, new TypeLine(CardType.Land, Supertype.None, ["Forest"]))]);
        var token = new GameObject(id, definition, 0, seat, seat, ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp) { IsToken = true };
        state = new ObjectCreated(token, -1).Apply(state);
        Assert.Equal(1, state.LandsEnteredThisTurn(seat));

        // CR 111.7 with 121.1: the token entered this turn even though nothing in the state holds it
        // now — it is in the turn's entry list and its token record, and in neither Objects nor LeftBattlefield.
        state = new ObjectCeasedToExist(id).Apply(state);
        Assert.Equal(1, state.LandsEnteredThisTurn(seat));
        Assert.Equal(0, state.LandsEnteredThisTurn(new PlayerId(1)));
    }
}
