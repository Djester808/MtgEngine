using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 608.3e: a permanent spell that resolves but whose controller can't put it onto the
/// battlefield is put into its owner's graveyard. The replacement system can only refuse the
/// entry — it does not know the move was a resolution and must not invent a second move — so the
/// follow-up belongs to the subsystem that proposed the move. Nothing here names a card.
/// </summary>
public sealed partial class ZoneChangeTests
{
    [Fact]
    public void A_resolving_permanent_spell_enters_the_battlefield_under_its_controllers_control()
    {
        // CR 608.3a: with nothing to stop it the resolution is the ordinary move, and the
        // permanent is the controller's even though the card is the other player's.
        var (state, spell) = ResolvingSeeded(ResolvingOwner);

        var outcome = Zones.ResolveOntoBattlefield(state, spell, ResolvingCaster);

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(Zone.Battlefield, moved.To.Zone.Zone);
        Assert.Equal(ResolvingCaster, moved.To.InitialController);
        Assert.Equal(ResolvingOwner, moved.To.Owner);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_permanent_spell_that_cannot_enter_the_battlefield_is_put_into_its_owners_graveyard()
    {
        // CR 608.3e. Without this the refusal leaves the card on the stack for the rest of the
        // game: the casting system reads the move, finds no permanent, and finishes anyway.
        var (state, spell) = ResolvingSeeded(ResolvingOwner);
        var zones = new ZoneSystem { Replacements = new ResolvingRefusesEntry() };

        var outcome = zones.ResolveOntoBattlefield(state, spell, ResolvingCaster);

        // CR 400.3 and 608.3e: its *owner's* graveyard, not the controller who cast it.
        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(ZoneKey.Of(Zone.Graveyard, ResolvingOwner), moved.To.Zone);
        Assert.Empty(outcome.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Empty(outcome.State.In(ZoneKey.Shared(Zone.Stack)));
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_resolution_whose_entry_stopped_to_ask_moves_nothing_yet()
    {
        // CR 616.1: the affected player has to say which effect applies before anything happens,
        // so the resolution has not decided that the permanent can't enter. Sending the card to a
        // graveyard here would answer the question by guessing.
        var (state, spell) = ResolvingSeeded(ResolvingOwner);
        var zones = new ZoneSystem { Replacements = new ZoneReplacementFake { Instead = ZoneReplacementFake.Asks } };

        var outcome = zones.ResolveOntoBattlefield(state, spell, ResolvingCaster);

        Assert.NotNull(outcome.State.Pending);
        Assert.DoesNotContain(outcome.Events, e => e is ObjectMoved);
        Assert.Equal(spell, Assert.Single(outcome.State.In(ZoneKey.Shared(Zone.Stack))));
    }

    [Fact]
    public void A_refused_graveyard_move_leaves_the_card_where_it_was()
    {
        // CR 608.3e says where the card goes, not that it must arrive. An effect that refuses both
        // moves leaves the object exactly where it was: the rule follows a refusal with one move,
        // and that move is offered like any other (CR 614.1), never forced.
        var (state, spell) = ResolvingSeeded(ResolvingOwner);
        var zones = new ZoneSystem { Replacements = new ZoneReplacementFake { Instead = ZoneReplacementFake.Nothing } };

        var outcome = zones.ResolveOntoBattlefield(state, spell, ResolvingCaster);

        Assert.DoesNotContain(outcome.Events, e => e is ObjectMoved);
        Assert.Equal(spell, Assert.Single(outcome.State.In(ZoneKey.Shared(Zone.Stack))));
    }

    [Fact]
    public void Resolving_an_object_that_is_no_longer_there_does_nothing()
    {
        // CR 608.2m: a spell already gone from the stack is not put anywhere by CR 608.3e. The
        // rule is about a controller who can't put a card onto the battlefield, not about a card
        // that has stopped existing, so there is no owner to read and nothing to move.
        var (state, spell) = ResolvingSeeded(ResolvingOwner);
        var gone = new ObjectCeasedToExist(spell).Apply(state);

        var outcome = Zones.ResolveOntoBattlefield(gone, spell, ResolvingCaster);

        Assert.Empty(outcome.Events);
        Assert.Equal(gone, outcome.State);
    }

    /// <summary>The seat that owns the card in every CR 608.3e test; player 1 in <see cref="Setup"/>.</summary>
    private static PlayerId ResolvingOwner => new(1);

    /// <summary>The seat that cast it, so owner and controller can never be confused for each other.</summary>
    private static PlayerId ResolvingCaster => new(0);

    /// <summary>A made-up permanent card, so no printed card's characteristics decide what a test proves.</summary>
    private static readonly CardDefinition ResolvingPermanent =
        new("oracle-resolving-subject", "Test Arrival", Layout.Normal,
        [
            new Face("Test Arrival", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Tester"]), "{1}{G}", new PrintedStat("2"), new PrintedStat("2")),
        ]);

    /// <summary>A started game with the made-up permanent spell on the stack, owned by one seat.</summary>
    private static (GameState State, ObjectId Id) ResolvingSeeded(PlayerId owner)
    {
        var state = Zones.Create(Setup(), Store).State;
        var seeded = new GameObject(new ObjectId(state.NextObjectId), ResolvingPermanent, 0, owner, owner, ZoneKey.Shared(Zone.Stack), state.NextTimestamp);
        return (new ObjectCreated(seeded, -1).Apply(state), seeded.Id);
    }

    /// <summary>
    /// CR 613.11 with 101.2: a "can't enter the battlefield" effect refuses the entry outright and
    /// changes nothing else, so the graveyard move CR 608.3e follows it with still happens.
    /// </summary>
    private sealed class ResolvingRefusesEntry : IReplacementSystem
    {
        public string Name => "fake that refuses the battlefield";

        public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected) =>
            proposed is ObjectMoved moved && moved.To.Zone.Zone == Zone.Battlefield
                ? Outcome.Unchanged(state)
                : new Outcome(proposed.Apply(state), [proposed]);
    }
}
