using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The two questions the query layer used to answer out of its own copy of the rules: what marks
/// an object as a copy (CR 707.10, CR 707.12), and which five words are the basic land types
/// (CR 205.3i). Neither answer is spelled in the engine any more — the first is read from the
/// marks the state carries, the second from the generated <c>MtgEngine.Cards.LandTypes</c>.
/// </summary>
public sealed partial class QueryTests
{
    /// <summary>
    /// CR 707.10 with CR 707.12: the state marks a copy in two ways and the predicate reads both.
    /// A copied spell carries the mark on its stack entry; a copy of a card made in place carries
    /// the zone system's designation, and CR 704.5e leaves such a copy on the battlefield, so the
    /// question is asked off the stack and answered there instead of refused.
    /// </summary>
    [Fact]
    [RuleRef("707.10")]
    public void A_copy_is_answered_from_whichever_mark_the_state_carries()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Alice, 1, 1)
            .CopyOfCard(2)
            .Spell(3, Alice, copy: true)
            .Spell(4, Alice, copy: false);

        Assert.False(IsACopy(world, 1));
        Assert.True(IsACopy(world, 2));
        Assert.True(IsACopy(world, 3, Cards.Zone.Stack));
        Assert.False(IsACopy(world, 4, Cards.Zone.Stack));
    }

    /// <summary>
    /// CR 205.3i: domain counts basic land types, and the five words are generated rules data
    /// (<c>MtgEngine.Cards.LandTypes</c>) rather than a literal the engine keeps. So the count
    /// with one land of every generated type is the size of that set, and it stays right when a
    /// new rules text is generated rather than needing an edit to C#.
    /// </summary>
    [Fact]
    [RuleRef("205.3i")]
    public void Domain_counts_the_generated_basic_land_types_and_nothing_else()
    {
        Assert.Equal(5, LandTypes.Basic.Count);

        var world = World.New();
        var id = 1;
        foreach (var basic in LandTypes.Basic)
            world = world.LandOf(id++, Alice, [basic]);

        // A land subtype the rules do not name as basic adds nothing (CR 205.3i, CR 205.4a).
        world = world.LandOf(id, Alice, ["Cave"]);

        Assert.Equal(LandTypes.Basic.Count, world.Value(new Cards.Domain(new You())));
        Assert.Equal(5, world.Value(new Cards.Domain(new You())));
    }

    private static bool IsACopy(World world, int id, Cards.Zone zone = Cards.Zone.Battlefield) =>
        world.Query.Matches(world.State, new ObjectFilter(In: zone, Also: new IsCopy()), new ObjectId(id), world.Context);
}
