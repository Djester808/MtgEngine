using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A move that puts the object down face down, CR 701.40a and CR 702.37c. Manifest turns the card
/// face down <i>before</i> it is put onto the battlefield, so the entering replacement of CR 614.12
/// and every "whenever a creature enters" trigger must be offered the 2/2 with no name; without an
/// argument on the move the only road was to land the card and turn it over afterwards, which left
/// the right board and the wrong event. Nothing here names a card: the definitions are made up.
/// </summary>
public sealed partial class ZoneChangeTests
{
    private static readonly ZoneKey FaceDownBattlefield = ZoneKey.Shared(Zone.Battlefield);

    [Fact]
    public void A_card_put_onto_the_battlefield_face_down_arrives_face_down()
    {
        // CR 701.40a: "Put that card onto the battlefield face down."
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat));

        var outcome = Zones.Move(state, card, FaceDownBattlefield, Placement.Default, tapped: false, controller: null, faceIndex: null, faceDown: true);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.FaceDown);
        Assert.True(outcome.State.Objects[moved.To.Id].FaceDown);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void The_replacement_system_is_offered_an_event_whose_object_is_already_face_down()
    {
        // CR 614.1c with 701.40a: the card is face down before it enters, so what an entering
        // replacement reads is the face-down object and not the card's own face. This is the whole
        // reason the flag is an argument on the move rather than a second event after it.
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat));
        var fake = new ZoneReplacementFake();
        var zones = new ZoneSystem { Replacements = fake };

        zones.Move(state, card, FaceDownBattlefield, Placement.Default, tapped: false, controller: null, faceIndex: null, faceDown: true);

        var offered = Assert.IsType<ObjectMoved>(Assert.Single(fake.Offered));
        Assert.True(offered.To.FaceDown);

        // The departed object is the card as it last existed, CR 603.10, and it was face up.
        Assert.False(offered.Departed!.FaceDown);
    }

    [Fact]
    public void What_lands_face_down_reads_as_a_two_two_with_no_name()
    {
        // CR 701.40a and 708.2a: a 2/2 face-down creature with no text, no name, no subtypes and no
        // mana cost. The characteristics are the layer system's; this asserts the flag reaches it,
        // because a permanent that reads as the card's own face is the defect being closed.
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat));

        var outcome = Zones.Move(state, card, FaceDownBattlefield, Placement.Default, tapped: false, controller: null, faceIndex: null, faceDown: true);

        var landed = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events)).To.Id;
        var read = LegacySubsystems.Create().Characteristics.Of(outcome.State, landed);
        Assert.Equal(string.Empty, read.Name);
        Assert.Equal(2, read.Power);
        Assert.Equal(2, read.Toughness);
        Assert.Empty(read.Subtypes);
        Assert.Null(read.ManaCost);
        Assert.Equal(CardType.Creature, read.Types);
    }

    [Fact]
    public void A_move_that_does_not_ask_for_face_down_arrives_face_up()
    {
        // The default is the ordinary move: an object is face up unless a rule turns it over.
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat));

        var outcome = Zones.Move(state, card, FaceDownBattlefield);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.False(moved.To.FaceDown);
        Assert.False(outcome.State.Objects[moved.To.Id].FaceDown);
    }

    [Fact]
    public void A_face_down_permanent_that_leaves_the_battlefield_is_face_up_again()
    {
        // CR 400.7: the new object has no memory of the old one, so face-down status is dropped by
        // the zone change unless the move asks for it. A manifested creature put into a graveyard
        // is a face-up card there.
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat));
        var entered = Zones.Move(state, card, FaceDownBattlefield, Placement.Default, tapped: false, controller: null, faceIndex: null, faceDown: true).State;
        var permanent = entered.In(FaceDownBattlefield)[0];
        Assert.True(entered.Objects[permanent].FaceDown);

        var outcome = Zones.Move(entered, permanent, ZoneKey.Of(Zone.Graveyard, FaceDownSeat));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.False(moved.To.FaceDown);
        AssertLogFolds(entered, outcome);
    }

    [Fact]
    public void A_face_down_move_still_chooses_which_face_the_card_shows_underneath()
    {
        // CR 712.12 and 701.40b: the face index says which face the permanent is turned face up
        // onto, and the two arguments are independent — face down is what everyone sees, the index
        // is what is underneath it.
        var (state, card) = FaceDownSeeded(ZoneKey.Of(Zone.Hand, FaceDownSeat), FaceDownTwoFaced);

        var outcome = Zones.Move(state, card, FaceDownBattlefield, Placement.Default, tapped: false, controller: null, faceIndex: 1, faceDown: true);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.True(moved.To.FaceDown);
        Assert.Equal(1, moved.To.FaceIndex);
    }

    /// <summary>The seat every face-down test seeds its object for; player 0 in <see cref="Setup"/>.</summary>
    private static PlayerId FaceDownSeat => new(0);

    /// <summary>A made-up creature, so no printed card's characteristics decide what a test proves.</summary>
    private static readonly CardDefinition FaceDownCreature =
        new("oracle-face-down-subject", "Test Subject", Layout.Normal,
        [
            new Face("Test Subject", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Tester"]), "{3}{U}", new PrintedStat("5"), new PrintedStat("1")),
        ]);

    /// <summary>A made-up two-faced card, for the face index under a face-down move (CR 712.12).</summary>
    private static readonly CardDefinition FaceDownTwoFaced =
        new("oracle-face-down-two-ways", "Test Two Sides", Layout.ModalDfc,
        [
            new Face("Test Two Sides", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Tester"]), "{2}{U}", new PrintedStat("3"), new PrintedStat("3")),
            new Face("Test Two Sides Shore", FaceRole.Back, new TypeLine(CardType.Land)),
        ]);

    /// <summary>
    /// A started game with one made-up object put straight into a zone, so a card can be moved out
    /// of any zone without a deck or a cast deciding what the test proves.
    /// </summary>
    private static (GameState State, ObjectId Id) FaceDownSeeded(ZoneKey zone, CardDefinition? definition = null)
    {
        var state = Zones.Create(Setup(), Store).State;
        var seeded = new GameObject(new ObjectId(state.NextObjectId), definition ?? FaceDownCreature, 0, FaceDownSeat, FaceDownSeat, zone, state.NextTimestamp);
        return (new ObjectCreated(seeded, -1).Apply(state), seeded.Id);
    }
}
