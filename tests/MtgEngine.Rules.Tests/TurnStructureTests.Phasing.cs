using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 702.26d: "at the beginning of each player's untap step, that player's phased-out
/// permanents phase in."
///
/// Nothing brought one back before this — no code anywhere emitted <c>PhasedOut: false</c> —
/// so a permanent that phased out stayed out for the rest of the game. It is read from the
/// state rather than through a filter, because a phased-out permanent is precisely what no
/// filter will admit (CR 702.26b).
/// </summary>
public sealed partial class TurnStructureTests
{
    /// <summary>A table with one permanent belonging to <paramref name="controller"/>, phased out.</summary>
    private static (GameState State, ObjectId Id) PhasedOutPermanent(PlayerId controller)
    {
        var state = TurnFixture.Table(2);
        var id = new ObjectId(state.NextObjectId);
        var definition = new CardDefinition("phased", "phased", Layout.Normal,
            [new Face("phased", FaceRole.Main, new TypeLine(CardType.Creature))]);
        state = new ObjectCreated(
            new GameObject(id, definition, 0, controller, controller, ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp)
            {
                SummoningSick = false,
                PhasedOut = true,
            },
            -1).Apply(state);
        return (state, id);
    }

    [Fact]
    [RuleRef("702.26d")]
    public void A_permanent_phases_in_at_its_controllers_untap_step()
    {
        var (state, id) = PhasedOutPermanent(TurnFixture.P0);

        var untapped = Turns.Advance(state.At(Step.Cleanup, TurnFixture.P1));

        Assert.False(untapped.State.Objects[id].PhasedOut);
    }

    [Fact]
    [RuleRef("702.26d")]
    public void It_stays_out_through_somebody_elses_untap_step()
    {
        var (state, id) = PhasedOutPermanent(TurnFixture.P1);

        var untapped = Turns.Advance(state.At(Step.Cleanup, TurnFixture.P1));

        Assert.True(untapped.State.Objects[id].PhasedOut);
    }
}
