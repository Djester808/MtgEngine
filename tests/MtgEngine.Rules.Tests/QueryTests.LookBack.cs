using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The query reading what wave 9 added to the state and the vocabulary: the turn's record of the
/// tokens it made (CR 111.1), the basic land types of CR 205.3i, and the colour a card chose
/// rather than printed. The world these are built in is the one QueryTests declares.
/// </summary>
public sealed partial class QueryTests
{
    [Fact]
    [RuleRef("111.1")]
    public void A_token_created_this_turn_still_answers_once_the_token_has_gone()
    {
        // CR 111.1 with CR 111.7: the token stops existing the moment it is sacrificed, so the
        // object cannot be the answer — the turn's record of it as it was created is. The filter
        // is read against that record, which is why a Treasure that is already gone matches
        // "an artifact token you created" and never matches a creature.
        var world = World.New().Token(1, Alice, CardType.Artifact, ["Treasure"]).CeaseToExist(1);

        Assert.False(world.State.Objects.ContainsKey(new ObjectId(1)));
        Assert.True(world.Holds(new CreatedTokenThisTurn(new You())));
        Assert.True(world.Holds(new CreatedTokenThisTurn(new You(), new ObjectFilter(Types: CardType.Artifact, Subtypes: ["Treasure"]))));
        Assert.False(world.Holds(new CreatedTokenThisTurn(new You(), new ObjectFilter(Types: CardType.Creature))));

        // The record is kept under the player who created it, so the opponent's question is no.
        Assert.False(world.Holds(new CreatedTokenThisTurn(new EachOpponent())));
        Assert.False(world.Holds(new CreatedTokenThisTurn(new EachOpponent(), new ObjectFilter(Types: CardType.Artifact))));

        // A card put onto the battlefield is not a token, CR 111.1, so it is not in the record.
        var noTokens = World.New().Creature(2, Alice, 1, 1);
        Assert.False(noTokens.Holds(new CreatedTokenThisTurn(new You())));
    }

    [Fact]
    [RuleRef("111.1")]
    public void A_look_back_filter_refuses_a_predicate_it_cannot_ask_of_a_token_that_is_gone()
    {
        // The predicate would have to be asked of an object in play; guessing "no" would make
        // "a tapped token you created" a different card. It is refused by name instead.
        var world = World.New().Token(1, Alice, CardType.Artifact, ["Treasure"]).CeaseToExist(1);

        var refused = Assert.Throws<RefusedException>(
            () => world.Holds(new CreatedTokenThisTurn(new You(), new ObjectFilter(Also: new IsThisObject()))));
        Assert.Contains(nameof(CreatedTokenThisTurn), refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("700.5")]
    public void A_chosen_color_is_read_from_the_choice_the_card_stored()
    {
        // "the chosen color" is whichever colour the earlier ChooseValue step stored under the
        // key, never a printed one — the same reading Devotion.ChosenColorKey takes. With nothing
        // stored under the key no object is that colour.
        var world = World.New()
            .Creature(1, Alice, 1, 1, colors: Color.Green)
            .Creature(2, Alice, 1, 1, colors: Color.Red);
        var green = world.Choose("colour", "Green");
        var red = world.Choose("colour", "Red");
        var chosen = new ObjectFilter(Also: new IsChosenColor("colour"));

        Assert.True(green.Query.Matches(green.State, chosen, new ObjectId(1), green.Context));
        Assert.False(green.Query.Matches(green.State, chosen, new ObjectId(2), green.Context));
        Assert.False(red.Query.Matches(red.State, chosen, new ObjectId(1), red.Context));
        Assert.True(red.Query.Matches(red.State, chosen, new ObjectId(2), red.Context));
        Assert.Equal(0, world.Count(chosen));
    }

    [Fact]
    [RuleRef("205.3i")]
    public void A_basic_land_type_is_a_different_question_from_the_basic_supertype()
    {
        // CR 205.3i names the five; CR 205.4a makes Basic a supertype. A nonbasic land with two
        // of the five has a basic land type and is not basic, and a nonbasic land with none of
        // them has neither. The subtypes come from the layers, so a type granted in layer 4 is
        // the type this reads.
        var world = World.New()
            .LandOf(1, Alice, ["Island", "Swamp"])
            .LandOf(2, Alice, ["Gate"])
            .Creature(3, Alice, 1, 1, subtypes: []);
        world.Layers.Set(new ObjectId(3), world.Layers.Of(world.State, new ObjectId(3)) with { EveryCreatureType = true });

        Assert.True(world.Query.Matches(world.State, new ObjectFilter(HasBasicLandType: true), new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(HasBasicLandType: true), new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(HasBasicLandType: false), new ObjectId(2), world.Context));
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Land, HasBasicLandType: true)));

        // Neither land is basic: the supertype was never given to either.
        Assert.Equal(0, world.Count(new ObjectFilter(Supertypes: Supertype.Basic)));

        // CR 205.3d: every creature type is not every subtype — a land type is not one of them.
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(HasBasicLandType: true), new ObjectId(3), world.Context));
    }
    [Fact]
    [RuleRef("608.2h")]
    public void An_amount_read_off_a_departed_object_uses_last_known_information()
    {
        // CR 608.2h with 400.7: "exile target creature; its controller gains life equal to its
        // power" reads the creature after it has left the battlefield, and the game uses what it
        // last knew of it rather than failing to find it.
        var world = World.New().Creature(1, Alice, 3, 4, manaCost: "{2}{G}");
        var gone = world.State.Objects[new ObjectId(1)];
        var exiled = gone with { Id = new ObjectId(9), Zone = ZoneKey.Of(Cards.Zone.Exile, Alice) };
        var after = world with { State = new ObjectMoved(new ObjectId(1), exiled, -1).Apply(world.State) };
        var remembered = after.Remember("it", new ObjectId(1));

        Assert.False(after.State.Objects.ContainsKey(new ObjectId(1)));
        Assert.Equal(3, remembered.Value(new PowerOf(new Remembered("it"))));
        Assert.Equal(4, remembered.Value(new ToughnessOf(new Remembered("it"))));
        Assert.Equal(3, remembered.Value(new ManaValueOf(new Remembered("it"))));

        // An object the game never held counts for nothing rather than failing (CR 608.2h).
        Assert.Equal(0, after.Remember("it", new ObjectId(77)).Value(new PowerOf(new Remembered("it"))));
    }
}
