using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// One reading of a <see cref="PlayerFilter"/>, CR 702.5b: the query answers it for every caller,
/// so the trigger system and the state-based actions stop keeping private copies that disagree
/// (seam-requests/E5d.md). The world these are built in is the one QueryTests declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("702.5b")]
    public void A_player_filter_reads_you_and_opponent_from_the_contexts_controller()
    {
        // CR 109.5 with 102.1: "you" is whoever controls the ability asking, and an opponent is
        // any other player still in the game. The context's controller is Alice.
        var world = World.New();

        Assert.True(Matches(world, new PlayerFilter(IsYou: true), Alice));
        Assert.False(Matches(world, new PlayerFilter(IsYou: true), Bob));
        Assert.True(Matches(world, new PlayerFilter(Opponent: true), Bob));
        Assert.False(Matches(world, new PlayerFilter(Opponent: true), Alice));

        // The two halves are nullable, so "not you" and "not an opponent" are separate questions
        // from leaving the half unasked; a filter with neither admits everyone.
        Assert.False(Matches(world, new PlayerFilter(IsYou: false), Alice));
        Assert.True(Matches(world, new PlayerFilter(IsYou: false), Bob));
        Assert.False(Matches(world, new PlayerFilter(Opponent: false), Bob));
        Assert.True(Matches(world, PlayerFilter.Any, Alice));
        Assert.True(Matches(world, PlayerFilter.Any, Bob));

        // Both halves named must both hold: "an opponent who is you" names nobody.
        Assert.False(Matches(world, new PlayerFilter(Opponent: true, IsYou: true), Alice));
        Assert.False(Matches(world, new PlayerFilter(Opponent: true, IsYou: true), Bob));
    }

    [Fact]
    [RuleRef("702.5b")]
    public void A_player_filters_condition_is_asked_as_the_candidate()
    {
        // The point of the whole seam. A Condition has no record for "the player being filtered",
        // so the only PlayerRef that can name them is You — which means the condition has to be
        // evaluated with the context rebound to the candidate, or Also stops being a filter and
        // becomes a board question. Here Bob controls a creature and Alice controls none, and the
        // context's controller is Alice: under the ambient reading both answers would be false.
        var world = World.New().Creature(1, Bob, 2, 2);
        var controlsACreature = new PlayerFilter(Also: new Controls(new You(), ObjectFilter.Creature, Ge(1)));

        Assert.True(Matches(world, controlsACreature, Bob));
        Assert.False(Matches(world, controlsACreature, Alice));

        // The rebinding does not reach the other two halves: they were already answered against
        // the ambient controller, so "an opponent who controls a creature" is still Bob only.
        var opponentWhoControls = controlsACreature with { Opponent = true };
        Assert.True(Matches(world, opponentWhoControls, Bob));
        Assert.False(Matches(world, opponentWhoControls, Alice));
        Assert.False(Matches(world, controlsACreature with { IsYou = true }, Bob));
    }

    [Fact]
    [RuleRef("702.5b")]
    public void A_player_filters_condition_is_asked_of_each_candidate_separately()
    {
        // Two candidates, one condition, two answers: the amount is re-evaluated per player the
        // way Query.Selectors.Extreme already does for "the player with the most". A creature
        // each makes both answers yes, which the ambient reading cannot distinguish from the
        // previous test's board and so cannot be told apart by luck.
        var world = World.New().Creature(1, Bob, 2, 2).Creature(2, Alice, 2, 2);
        var controlsACreature = new PlayerFilter(Also: new Controls(new You(), ObjectFilter.Creature, Ge(1)));
        var controlsTwo = new PlayerFilter(Also: new Controls(new You(), ObjectFilter.Creature, Ge(2)));

        Assert.True(Matches(world, controlsACreature, Alice));
        Assert.True(Matches(world, controlsACreature, Bob));
        Assert.False(Matches(world, controlsTwo, Alice));
        Assert.False(Matches(world, controlsTwo, Bob));
    }

    private static bool Matches(World world, PlayerFilter filter, PlayerId candidate) =>
        world.Query.PlayerMatches(world.State, filter, candidate, world.Context);
}
