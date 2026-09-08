using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>The wave-24 folds E2c asked of the events: the per-step draw record (CR 121.1) and a created token's entry (CR 111.1).</summary>
public sealed class EventFoldTests
{
    private static GameState Started() =>
        GameReducer.Replay([new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 42, StartingLife: 20, Format: "test")]);

    [Fact]
    [RuleRef("121.1")]
    public void A_draw_counts_for_the_turn_and_the_step_and_a_new_step_forgets_only_the_steps()
    {
        var a = new PlayerId(0);
        var state = new ObjectDrawn(a, new ObjectId(7)).Apply(Started());
        Assert.Equal(1, state.Player(a).ObjectsDrawnThisTurn);
        Assert.Equal(1, state.Player(a).ObjectsDrawnThisStep);

        // CR 500.1: what was drawn in the step that ended is not drawn in this one; the turn's count stays.
        state = new StepBegan(Step.PrecombatMain).Apply(state);
        Assert.Equal(1, state.Player(a).ObjectsDrawnThisTurn);
        Assert.Equal(0, state.Player(a).ObjectsDrawnThisStep);
        Assert.Equal(0, state.Player(new PlayerId(1)).ObjectsDrawnThisStep);
    }

    [Fact]
    [RuleRef("111.1")]
    public void A_token_created_on_the_battlefield_has_entered_it_this_turn()
    {
        var state = Started();
        var seat = new PlayerId(0);
        var id = new ObjectId(state.NextObjectId);
        var definition = new CardDefinition("Token", "Token", Layout.Normal, [new Face("Token", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, ["Beast"]))]);
        var token = new GameObject(id, definition, 0, seat, seat, ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp) { IsToken = true };
        state = new ObjectCreated(token, -1).Apply(state);

        // CR 111.1: a token created on the battlefield enters it, so the turn's record lists it once.
        Assert.Single(state.History.EnteredBattlefield, id);
    }
}
