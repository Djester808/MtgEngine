using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a filter's <c>permanent</c> field asks, zone by zone. On the battlefield it is CR 110.1's
/// question and the types do not come into it; against a card anywhere else the only meaning the
/// word has is CR 110.4a's "permanent card", so it is a question about the card's types. The
/// world these are built in is the one QueryTests declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("110.4a")]
    public void A_permanent_filter_read_against_a_library_asks_whether_the_card_is_a_permanent_card()
    {
        // Two cards revealed off the top: one that could be put onto the battlefield and one that
        // could not. The instant's power and toughness are never read; the World fixture wants a
        // pair and the filter asks about types.
        var world = World.New()
            .Creature(1, Alice, 1, 1, Cards.Zone.Library)
            .Creature(2, Alice, 0, 0, Cards.Zone.Library, types: CardType.Instant);
        var permanentCards = new ObjectFilter(In: Cards.Zone.Library, Permanent: true);

        Assert.True(world.Query.Matches(world.State, permanentCards, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, permanentCards, new ObjectId(2), world.Context));
        Assert.Equal(1, world.Count(permanentCards));

        // And the other way round: the instant is the nonpermanent card.
        var nonpermanent = new ObjectFilter(In: Cards.Zone.Library, Permanent: false);
        Assert.False(world.Query.Matches(world.State, nonpermanent, new ObjectId(1), world.Context));
        Assert.True(world.Query.Matches(world.State, nonpermanent, new ObjectId(2), world.Context));
    }

    [Fact]
    [RuleRef("110.4a")]
    public void Every_zone_off_the_battlefield_reads_the_permanent_types_the_same_way()
    {
        // CR 110.4a names six permanent types; a hand, a graveyard and exile ask the same question
        // of them as a library does.
        var world = World.New()
            .Creature(1, Alice, 0, 0, Cards.Zone.Hand, types: CardType.Land)
            .Creature(2, Alice, 0, 0, Cards.Zone.Graveyard, types: CardType.Battle)
            .Creature(3, Alice, 0, 0, Cards.Zone.Exile, types: CardType.Sorcery)
            .Creature(4, Alice, 0, 0, Cards.Zone.Graveyard, types: CardType.Instant | CardType.Artifact);

        Assert.True(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Hand, Permanent: true), new ObjectId(1), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Graveyard, Permanent: true), new ObjectId(2), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Exile, Permanent: true), new ObjectId(3), world.Context));

        // A card with a permanent type among its types is a permanent card, so the pair counts one.
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Graveyard, Permanent: true), new ObjectId(4), world.Context));
        Assert.Equal(2, world.Count(new ObjectFilter(In: Cards.Zone.Graveyard, Permanent: true)));
    }

    [Fact]
    [RuleRef("110.4c")]
    public void On_the_battlefield_permanent_is_still_the_zone_and_not_the_types()
    {
        // CR 110.1 with 110.4c: a permanent that has lost all its permanent types is still on the
        // battlefield and still a permanent, so the types cannot be what is asked here.
        var world = World.New().Creature(1, Alice, 1, 1, types: CardType.None);

        Assert.True(world.Query.Matches(world.State, new ObjectFilter(Permanent: true), new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(Permanent: false), new ObjectId(1), world.Context));
    }

    [Fact]
    [RuleRef("110.4b")]
    public void A_spell_on_the_stack_keeps_the_zone_reading()
    {
        // The stack is left alone on purpose: CR 110.4b's "permanent spell" is a set of its own and
        // no record asks for it, so an object there answers no as it always did.
        var world = World.New().Spell(1, Alice, copy: false);

        Assert.False(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Stack, Permanent: true), new ObjectId(1), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Stack, Permanent: false), new ObjectId(1), world.Context));
    }
}
