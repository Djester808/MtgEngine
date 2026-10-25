using MtgEngine.Cards;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 702.26b at the state-based actions. They walk <c>snapshot.Objects</c> directly rather
/// than through a filter, so the query layer's guard does not cover them and they read the flag
/// themselves: a permanent that is phased out does not exist, so 704.5 has nothing to judge.
/// </summary>
public sealed partial class StateBasedActionTests
{
    [Fact]
    [RuleRef("702.26b")]
    public void A_phased_out_creature_with_no_toughness_does_not_die_while_it_is_gone()
    {
        var board = new Board();
        var doomed = board.Put(Board.Creature(2, 2, "sba phased"), counters: (Counters.MinusOneMinusOne, 2));
        board.Fold(new ObjectChanged(doomed, PhasedOut: true));

        var settled = sba.Check(board.State);

        Assert.Contains(settled.State.Objects.Values, o => o.Face.Name == "sba phased");
    }

    [Fact]
    [RuleRef("704.5f")]
    public void And_dies_the_moment_it_phases_back_in()
    {
        var board = new Board();
        var doomed = board.Put(Board.Creature(2, 2, "sba returning"), counters: (Counters.MinusOneMinusOne, 2));
        board.Fold(new ObjectChanged(doomed, PhasedOut: true));
        board.Fold(new ObjectChanged(doomed, PhasedOut: false));

        var settled = sba.Check(board.State);

        Assert.Equal(Zone.Graveyard, Zoned(settled.State, "sba returning"));
    }
}
