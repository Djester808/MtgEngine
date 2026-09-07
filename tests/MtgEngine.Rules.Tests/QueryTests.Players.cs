using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The player half of the memory an effect keeps, CR 608.2h, and the direction the
/// <see cref="AttachedTo"/> predicate reads. The world these are built in is the one QueryTests
/// declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("608.2h")]
    public void A_remembered_player_is_read_back_from_the_context()
    {
        // "Target player draws two cards and loses 2 life": the first step names the player under
        // a key and the second reads that key, so both halves reach the same player even though
        // the board could name several. CR 608.2h — the answer is determined once.
        var world = World.New().RememberPlayer("k1", Bob);

        Assert.Equal(Bob, Assert.Single(world.Players(new RememberedPlayer("k1"))));
    }

    [Fact]
    [RuleRef("608.2h")]
    public void A_remembered_player_key_that_names_nobody_is_an_answer_and_not_a_gap()
    {
        // "Up to one target player": the step ran and chose no one, so the key exists and names
        // an empty list. That is an answer, and the later step rightly affects nobody.
        var world = World.New().RememberPlayer("k1");

        Assert.Empty(world.Players(new RememberedPlayer("k1")));
    }

    [Fact]
    [RuleRef("608.2h")]
    public void A_player_key_no_step_wrote_is_refused_rather_than_read_as_nobody()
    {
        // Two shapes of nothing: a context that remembers no players at all, and one that
        // remembers a different key. Reading either as "nobody" would drop the rest of the effect
        // in silence, so the query refuses and names what is missing.
        var world = World.New();
        var other = World.New().RememberPlayer("k2", Bob);

        Assert.Throws<RefusedException>(() => world.Players(new RememberedPlayer("k1")));
        Assert.Throws<RefusedException>(() => other.Players(new RememberedPlayer("k1")));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void Choosing_a_player_and_a_players_linked_choice_are_still_refused()
    {
        // The query reads and never chooses (CR 608.2), and the values an object keeps from a
        // linked ability are strings (CR 607.2d), so neither record has an answer here yet.
        var world = World.New().RememberPlayer("k1", Bob);

        Assert.Throws<RefusedException>(
            () => world.Players(new ChoosePlayer(new PlayerFilter(), new You(), "k1")));
        Assert.Throws<RefusedException>(() => world.Players(new ChosenPlayer("k1")));
    }

    [Fact]
    [RuleRef("301.5c")]
    public void Attached_to_names_the_attachment_and_never_its_host()
    {
        // Object 2 is attached to creature 1. Read as the vocabulary's doc comment writes it —
        // "attached to the given object: Auras attached to it" — the candidate is the attachment
        // and the selector names the host, so asking it from the host's point of view finds the
        // attachment.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Enchantment)
            .Attach(2, 1)
            .As(1);

        Assert.True(world.Query.Matches(world.State, new ObjectFilter(Also: new AttachedTo(new Self())), new ObjectId(2), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(Also: new AttachedTo(new Self())), new ObjectId(1), world.Context));
        Assert.Equal(1, world.Count(new ObjectFilter(Also: new AttachedTo(new Self()))));

        // The same question against a filter rather than Self: "an Aura attached to a creature".
        Assert.Equal(1, world.Count(new ObjectFilter(Also: new AttachedTo(new Each(ObjectFilter.Creature)))));
    }

    [Fact]
    [RuleRef("301.5c")]
    public void Attached_to_is_not_the_equipped_creature_and_says_so_by_answering_none()
    {
        // The other direction, from the attachment's point of view: "the object this is attached
        // to" is the Attached *selector*, not this predicate. Asked as the attachment, AttachedTo
        // finds nothing — the host is not attached to anything — while the selector finds the
        // host. A filter that wants the equipped creature therefore cannot be written today;
        // seam-requests/E0g.md records the gap.
        var world = World.New()
            .Creature(1, Alice, 2, 2)
            .Creature(2, Alice, 1, 1, types: CardType.Enchantment)
            .Attach(2, 1)
            .As(2);

        Assert.Equal(0, world.Count(new ObjectFilter(Also: new AttachedTo(new Self()))));
        Assert.Equal(new ObjectId(1), Assert.Single(world.Query.Resolve(world.State, new Attached(), world.Context)));
    }
}
