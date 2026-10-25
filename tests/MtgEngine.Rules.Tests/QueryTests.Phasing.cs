using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 702.26b: "a permanent that's phased out is treated as though it doesn't exist."
///
/// <c>GameObject.PhasedOut</c> was written by the interpreter's phasing arm and read by NOTHING
/// — not a query, not a layer, not combat, not a state-based action, not targeting. The flag
/// replaced a dictionary whose removal note predicted this exactly: "a filter would have needed
/// adding at each of those places, and the one that got missed would be the bug." It is read at
/// the one filter choke point now, so every one of those places is covered by the same line.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("702.26b")]
    public void A_phased_out_permanent_matches_no_filter()
    {
        var world = World.New().Creature(1, Alice, 2, 2);
        var beast = new ObjectId(1);
        Assert.True(world.Query.Matches(world.State, ObjectFilter.Creature, beast, world.Context));

        var phased = new ObjectChanged(beast, PhasedOut: true).Apply(world.State);

        Assert.False(world.Query.Matches(phased, ObjectFilter.Creature, beast, world.Context));
    }

    [Fact]
    [RuleRef("702.26b")]
    public void It_is_not_counted_among_the_permanents_either()
    {
        var world = World.New().Creature(1, Alice, 2, 2).Creature(2, Alice, 2, 2);
        Assert.Equal(2, world.Count(ObjectFilter.Creature));

        var phased = new ObjectChanged(new ObjectId(1), PhasedOut: true).Apply(world.State);

        Assert.Equal(1, (world with { State = phased }).Count(ObjectFilter.Creature));
    }

    [Fact]
    [RuleRef("702.26d")]
    public void Phasing_back_in_makes_it_findable_again()
    {
        var world = World.New().Creature(1, Alice, 2, 2);
        var beast = new ObjectId(1);
        var phased = new ObjectChanged(beast, PhasedOut: true).Apply(world.State);

        var back = new ObjectChanged(beast, PhasedOut: false).Apply(phased);

        Assert.True(world.Query.Matches(back, ObjectFilter.Creature, beast, world.Context));
    }
}
