using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A planeswalker entering with its printed loyalty, CR 306.5b. Nothing in the engine wrote
/// <see cref="Counters.Loyalty"/> before this, and the state-based action in CR 704.5i reads it,
/// so every planeswalker was buried the moment it arrived. Nothing here names a card: the faces
/// below are made up, and only the printed number decides what the tests prove.
/// </summary>
public sealed partial class ZoneChangeTests
{
    private static readonly CardDefinition LoyaltyWalker = LoyaltyDefinition("Test Walker", "4");
    private static readonly CardDefinition LoyaltyWalkerZero = LoyaltyDefinition("Test Nought", "0");
    private static readonly CardDefinition LoyaltyWalkerVariable = LoyaltyDefinition("Test Variable", "X");

    [Fact]
    public void A_planeswalker_entering_from_the_stack_has_its_printed_loyalty_in_counters()
    {
        // CR 306.5b: as it enters, it gets loyalty counters equal to the number printed on it.
        var (state, walker) = LoyaltySeeded(ZoneKey.Shared(Zone.Stack), LoyaltyWalker);

        var outcome = Zones.Move(state, walker, ZoneKey.Shared(Zone.Battlefield));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(4, moved.To.Counters[Counters.Loyalty]);
        Assert.Equal(4, outcome.State.Objects[moved.To.Id].Counters[Counters.Loyalty]);
        AssertLogFolds(state, outcome);
    }

    [Theory]
    [InlineData(Zone.Graveyard)]
    [InlineData(Zone.Hand)]
    [InlineData(Zone.Exile)]
    [InlineData(Zone.Library)]
    public void A_planeswalker_put_onto_the_battlefield_from_anywhere_has_its_printed_loyalty(Zone from)
    {
        // CR 306.5b is about entering the battlefield, not about being cast: an effect that
        // returns a planeswalker from a graveyard or puts one from a hand onto the battlefield
        // gives it the same counters the stack would have.
        var (state, walker) = LoyaltySeeded(ZoneSystem.KeyFor(from, LoyaltySeat), LoyaltyWalker);

        var outcome = Zones.Move(state, walker, ZoneKey.Shared(Zone.Battlefield));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(4, moved.To.Counters[Counters.Loyalty]);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_creature_entering_the_battlefield_gets_no_loyalty_counters()
    {
        // CR 306.5b is a planeswalker's rule; nothing else arrives with a counter it was not given.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var bear = InHand(state, owner, Bear);

        var outcome = Zones.Move(state, bear, ZoneKey.Shared(Zone.Battlefield));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Empty(moved.To.Counters);
    }

    [Fact]
    public void A_planeswalker_leaving_the_battlefield_carries_no_loyalty_counters_with_it()
    {
        // CR 306.5a and 400.7: loyalty is a characteristic of the permanent, so the card in the
        // graveyard is a new object with nothing on it.
        var (state, walker) = LoyaltySeeded(ZoneKey.Shared(Zone.Stack), LoyaltyWalker);
        var entered = Zones.Move(state, walker, ZoneKey.Shared(Zone.Battlefield)).State;
        var permanent = entered.In(ZoneKey.Shared(Zone.Battlefield))[0];
        Assert.Equal(4, entered.Objects[permanent].Counters[Counters.Loyalty]);

        var outcome = Zones.Move(entered, permanent, ZoneKey.Of(Zone.Graveyard, LoyaltySeat));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Empty(moved.To.Counters);
        AssertLogFolds(entered, outcome);
    }

    [Fact]
    public void A_planeswalker_printed_with_zero_loyalty_enters_with_none_and_dies_to_the_state_based_action()
    {
        // CR 122.1a: no counter of zero is ever on a permanent, so 704.5i sees loyalty 0 and puts
        // it into its owner's graveyard the first time state-based actions are checked.
        var (state, walker) = LoyaltySeeded(ZoneKey.Shared(Zone.Stack), LoyaltyWalkerZero);
        var entered = Zones.Move(state, walker, ZoneKey.Shared(Zone.Battlefield)).State;
        var permanent = entered.In(ZoneKey.Shared(Zone.Battlefield))[0];
        Assert.Empty(entered.Objects[permanent].Counters);

        var settled = Subsystems.Wave1().Sba.Check(entered);

        Assert.Empty(settled.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Single(settled.State.In(ZoneKey.Of(Zone.Graveyard, LoyaltySeat)));
    }

    [Fact]
    public void A_planeswalker_whose_printed_loyalty_is_a_variable_is_refused_with_its_rule()
    {
        // CR 306.5c: the value is the one chosen as the spell was cast, and nothing carries that
        // choice here, so the shape is refused by name rather than entering with a guessed number.
        var (state, walker) = LoyaltySeeded(ZoneKey.Shared(Zone.Stack), LoyaltyWalkerVariable);

        var refused = Assert.Throws<RefusedException>(() => Zones.Move(state, walker, ZoneKey.Shared(Zone.Battlefield)));

        Assert.Equal("306.5c", refused.Rule);
    }

    [Fact]
    public void A_planeswalker_card_moved_between_other_zones_is_never_given_loyalty()
    {
        // The variable loyalty proves the guard: a move that is not onto the battlefield never
        // reads the printed number at all, so it cannot refuse a card on its way to a graveyard.
        var (state, walker) = LoyaltySeeded(ZoneKey.Of(Zone.Hand, LoyaltySeat), LoyaltyWalkerVariable);

        var outcome = Zones.Move(state, walker, ZoneKey.Of(Zone.Graveyard, LoyaltySeat));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Empty(moved.To.Counters);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_token_copy_of_a_planeswalker_enters_with_its_printed_loyalty()
    {
        // CR 111.1 and 707.2: a token is created on the battlefield, which is entering it, and the
        // copiable values it was made from carry the printed loyalty.
        var state = Zones.Create(Setup(), Store).State;

        var outcome = Zones.CreateToken(state, CopyRules.CopyOf(LoyaltyWalker, 0), LoyaltySeat, tapped: false, attacking: false);

        var created = Assert.IsType<ObjectCreated>(Assert.Single(outcome.Events));
        Assert.Equal(4, created.Object.Counters[Counters.Loyalty]);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_copy_of_a_planeswalker_made_where_it_stands_enters_with_its_printed_loyalty()
    {
        // CR 707.12: the copy is created in the zone the original is in; on the battlefield that
        // is a permanent entering, so CR 306.5b applies to it as well.
        var (state, walker) = LoyaltySeeded(ZoneKey.Shared(Zone.Battlefield), LoyaltyWalker);

        var outcome = Zones.CopyInPlace(state, walker, LoyaltySeat);

        var created = Assert.IsType<ObjectCreated>(Assert.Single(outcome.Events));
        Assert.Equal(4, created.Object.Counters[Counters.Loyalty]);
        AssertLogFolds(state, outcome);
    }

    /// <summary>The seat every loyalty test seeds its object for; player 0 in <see cref="Setup"/>.</summary>
    private static PlayerId LoyaltySeat => new(0);

    /// <summary>
    /// A started game with one made-up object put straight into a zone, so a planeswalker can be
    /// moved out of any zone without a deck, a draw or a cast deciding what the test proves.
    /// </summary>
    private static (GameState State, ObjectId Id) LoyaltySeeded(ZoneKey zone, CardDefinition definition)
    {
        var state = Zones.Create(Setup(), Store).State;
        var seeded = new GameObject(new ObjectId(state.NextObjectId), definition, 0, LoyaltySeat, LoyaltySeat, zone, state.NextTimestamp);
        return (new ObjectCreated(seeded, -1).Apply(state), seeded.Id);
    }

    /// <summary>A made-up planeswalker face, so no card's printed number decides what a test proves.</summary>
    private static CardDefinition LoyaltyDefinition(string name, string loyalty) =>
        new($"oracle-{loyalty}-walker", name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Planeswalker, Subtypes: ["Tester"]), "{2}{W}", Loyalty: loyalty)]);
}
