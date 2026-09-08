using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 30's readings: the plural zone rider every reader was silently refusing (CR 400.1), and the
/// two V18 amounts — the names among a group counted once each (CR 201.2b) and the two numbers no
/// folder keeps yet (CR 119.3, CR 903.8). The world is the one <see cref="QueryTests"/> declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("400.1")]
    public void A_filter_naming_two_zones_admits_an_object_in_either_of_them()
    {
        // CR 400.1: an object is in exactly one zone, so "creature cards in graveyards and
        // libraries" is a disjunction. Before this the filter's only zone rider was In, so InAnyOf
        // fell through to the battlefield default and the filter matched nothing at all — a card
        // played wrongly with nothing in the log to say so.
        var world = World.New()
            .Creature(1, Alice, 1, 1, Cards.Zone.Graveyard)
            .Library(2, Alice)
            .Creature(3, Alice, 1, 1);

        var either = new ObjectFilter(Types: CardType.Creature, InAnyOf: [Cards.Zone.Graveyard, Cards.Zone.Library]);

        Assert.True(world.Query.Matches(world.State, either, new ObjectId(1), world.Context));
        Assert.True(world.Query.Matches(world.State, either, new ObjectId(2), world.Context));

        // The battlefield creature is in neither named zone, so the plural narrows as well as widens.
        Assert.False(world.Query.Matches(world.State, either, new ObjectId(3), world.Context));

        // Candidates walks every named zone, not the first: the count is the whole disjunction.
        Assert.Equal(2, world.Count(either));
    }

    [Fact]
    [RuleRef("400.1")]
    public void A_single_zone_and_no_zone_at_all_still_read_as_they_did()
    {
        // The plural is an addition, not a replacement: In still names one zone and a filter with
        // no zone rider is still the battlefield, CR 110.1 — the reading every stored card relies on.
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Alice, 1, 1, Cards.Zone.Hand)
            .Creature(3, Alice, 1, 1, Cards.Zone.Graveyard);

        Assert.Equal(1, world.Count(ObjectFilter.Creature));
        Assert.Equal(1, world.Count(new ObjectFilter(In: Cards.Zone.Hand, Types: CardType.Creature)));
        Assert.Equal(1, world.Count(new ObjectFilter(In: Cards.Zone.Graveyard, Types: CardType.Creature)));

        // A one-entry list is In written the long way (L047 refuses it in a card file) and reads the
        // same here, so a filter that reached the engine anyway is not silently emptied.
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, InAnyOf: [Cards.Zone.Hand])));
    }

    [Fact]
    [RuleRef("400.1")]
    public void The_rest_of_a_plural_filter_is_still_asked_of_each_object()
    {
        // The zone rider is one criterion among many: widening it must not admit an object the rest
        // of the filter refuses, which is the failure a "matches any of these zones, full stop"
        // shortcut would have.
        var world = World.New()
            .Creature(1, Alice, 1, 1, Cards.Zone.Graveyard)
            .Creature(2, Bob, 1, 1, Cards.Zone.Library)
            .Creature(3, Alice, 1, 1, Cards.Zone.Library, types: CardType.Artifact);

        var mine = new ObjectFilter(
            Types: CardType.Creature,
            OwnedBy: new You(),
            InAnyOf: [Cards.Zone.Graveyard, Cards.Zone.Library]);

        Assert.Equal(1, world.Count(mine));
        Assert.True(world.Query.Matches(world.State, mine, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, mine, new ObjectId(2), world.Context));
        Assert.False(world.Query.Matches(world.State, mine, new ObjectId(3), world.Context));
    }

    [Fact]
    [RuleRef("201.2b")]
    public void Distinct_names_counts_the_names_and_never_the_objects()
    {
        // CR 201.2b: "lands you control with different names" is how many names there are between
        // them. Two objects of one name are one name; a plain CountOf answers 3 here, which is the
        // card played wrongly, so the two readings must differ on this board.
        var lands = new ObjectFilter(Types: CardType.Land, ControlledBy: new You());
        var world = E5mNamed(World.New().Land(1, Alice).Land(2, Alice).Land(3, Alice), 2, "Object 1");

        Assert.Equal(2, world.Value(new DistinctNameCount(lands)));
        Assert.Equal(3, world.Value(new CountOf(lands)));

        // Three names is three, and an empty board is zero rather than a refusal — zero names is the
        // honest answer to a question about no objects.
        Assert.Equal(3, World.New().Land(1, Alice).Land(2, Alice).Land(3, Alice).Value(new DistinctNameCount(lands)));
        Assert.Equal(0, World.New().Value(new DistinctNameCount(lands)));
    }

    [Fact]
    [RuleRef("201.2a")]
    public void A_nameless_object_carries_no_name_for_the_count_to_find()
    {
        // CR 201.2a: an object with no name has the same name as nothing, including another nameless
        // object. It is counted by neither this nor a name comparison, so it contributes no name —
        // which is where this parts company with GreatestSameNameCount, where a nameless object is
        // still a group of one.
        var lands = new ObjectFilter(Types: CardType.Land, ControlledBy: new You());
        var one = E5mNamed(World.New().Land(1, Alice).Land(2, Alice), 2, string.Empty);
        Assert.Equal(1, one.Value(new DistinctNameCount(lands)));

        var none = E5mNamed(E5mNamed(World.New().Land(1, Alice).Land(2, Alice), 1, string.Empty), 2, string.Empty);
        Assert.Equal(0, none.Value(new DistinctNameCount(lands)));
        Assert.Equal(2, none.Value(new CountOf(lands)));
    }

    [Fact]
    [RuleRef("201.2b")]
    public void Distinct_names_reads_the_zone_the_filter_names()
    {
        // The amount is counted over Candidates, so it inherits both zone riders: the graveyard on
        // its own, and the two zones together as one group of names.
        var world = E5mNamed(
            World.New()
                .Creature(1, Alice, 1, 1, Cards.Zone.Graveyard)
                .Creature(2, Alice, 1, 1, Cards.Zone.Graveyard)
                .Library(3, Alice),
            2,
            "Object 1");

        Assert.Equal(1, world.Value(new DistinctNameCount(new ObjectFilter(In: Cards.Zone.Graveyard))));
        Assert.Equal(
            2,
            world.Value(new DistinctNameCount(new ObjectFilter(InAnyOf: [Cards.Zone.Graveyard, Cards.Zone.Library]))));
    }

    [Fact]
    [RuleRef("119.3")]
    public void The_life_lost_this_turn_is_refused_until_the_state_keeps_the_total()
    {
        // CR 119.3 with 119.4: damage dealt to a player and life paid as a cost are both life lost,
        // and the total is loss and not net change. Nothing in the state keeps it, and zero is a
        // legal answer to "did an opponent lose life this turn" — so answering zero would play every
        // such card as if nobody had, silently. The refusal names the missing field instead.
        var world = World.New();
        var lost = Assert.Throws<RefusedException>(() => world.Value(new LifeLostThisTurn(PlayerFilter.Opponents)));
        Assert.Contains(nameof(LifeLostThisTurn), lost.Message, StringComparison.Ordinal);
        Assert.Equal("119.3", lost.Rule);

        // A player who has actually lost life is refused the same way: what is missing is the
        // record of the loss, not the board, so no state a test can build makes this arm answer.
        var hurt = world with { State = new MtgEngine.Rules.Events.LifeChanged(Bob, -3, "test").Apply(world.State) };
        Assert.Equal(17, hurt.State.Player(Bob).Life);
        Assert.Throws<RefusedException>(() => hurt.Value(new LifeLostThisTurn(PlayerFilter.Opponents)));
    }

    [Fact]
    [RuleRef("903.8")]
    public void The_commander_cast_count_is_refused_until_a_folder_keeps_it_per_game()
    {
        // CR 903.8: the count the commander tax is built on is per game and never reset, so the
        // turn's history — which is cleared each turn — cannot stand in for it. Refused, naming the
        // count, rather than answered with the turn's casts or with zero.
        var world = World.New().Commander(1, Alice).As(1);

        var own = Assert.Throws<RefusedException>(() => world.Value(new CommanderCastsThisGame()));
        Assert.Contains(nameof(CommanderCastsThisGame), own.Message, StringComparison.Ordinal);
        Assert.Equal("903.8", own.Rule);

        Assert.Throws<RefusedException>(() => world.Value(new CommanderCastsThisGame(new Self())));
    }

    /// <summary>The name the layer system reports for one object, which is the name every count reads (CR 613.1).</summary>
    private static World E5mNamed(World world, int id, string name)
    {
        world.Layers.Set(new ObjectId(id), world.Layers.Of(world.State, new ObjectId(id)) with { Name = name });
        return world;
    }
}
