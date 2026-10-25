using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Graveyard")]
public sealed class GraveyardContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly ZoneKey Pile = ZoneKey.Of(Zone.Graveyard, Owner);
    private static readonly ZoneSystem Zones = new();
    private static readonly CardDefinition Definition = new("card", "Front // Back", Layout.Transform,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Front text."),
         new Face("Back", FaceRole.Back, new TypeLine(CardType.Creature), OracleText: "Back text.")]);

    [Fact]
    public void Every_player_starts_with_an_empty_graveyard()
    {
        var state = Start().State;
        foreach (var player in state.Players)
        {
            var graveyard = new Graveyard(state, player.Id);
            Assert.Equal(player.Id, graveyard.Owner);
            Assert.Equal(0, graveyard.Count);
            Assert.Null(graveyard.Top);
            Assert.Empty(graveyard.Find(_ => true));
            Assert.False(graveyard.Contains(new ObjectId(999)));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_graveyard_requires_an_existing_owner(int owner) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graveyard(Start().State, new PlayerId(owner)));

    [Fact]
    public void Arrivals_are_owner_specific_top_first_and_public_to_every_seat()
    {
        var start = Start();
        var first = Zones.Move(start.State, new ObjectId(1), Pile);
        var second = Zones.Move(first.State, new ObjectId(2), ZoneKey.Of(Zone.Graveyard, new PlayerId(1)), controller: new PlayerId(1));
        var graveyard = new Graveyard(second.State, Owner);
        var top = Assert.IsType<GameObject>(graveyard.Top);
        Assert.Equal("second-printing", top.DeckSelection!.PrintingId);
        Assert.Equal(Owner, top.Owner);
        Assert.Equal(Owner, top.InitialController);
        Assert.Equal(2, graveyard.Count);
        Assert.Equal(top.Id, Assert.Single(graveyard.Find(card => card.DeckSelection!.Finish == CardFinish.Etched)));
        Assert.True(graveyard.Contains(top.Id));
        Assert.Equal(0, new Graveyard(second.State, new PlayerId(1)).Count);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var viewer in second.State.Players)
        {
            var view = projector.Project(second.State, viewer.Id);
            var visible = view.Players[0];
            Assert.Equal(2, visible.GraveyardCount);
            Assert.Equal(graveyard.Order, visible.Graveyard.Select(card => card.Id));
            Assert.Equal("second-printing", visible.Graveyard[0].PrintingId);
            Assert.Equal(CardFinish.Etched, visible.Graveyard[0].Finish);
            Assert.Equal("Front text.", visible.Graveyard[0].OracleText);
            Assert.DoesNotContain("CardInstanceId", JsonSerializer.Serialize(view));
        }
    }

    [Theory]
    [InlineData(Placement.Default)]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Moving_within_the_same_graveyard_does_not_reorder_or_create_an_object(Placement placement)
    {
        var first = Zones.Move(Start().State, new ObjectId(1), Pile);
        var second = Zones.Move(first.State, new ObjectId(2), Pile);
        var old = new Graveyard(second.State, Owner).Order[^1];
        var outcome = Zones.Move(second.State, old, Pile, placement);
        Assert.Empty(outcome.Events);
        Assert.Same(second.State, outcome.State);
    }

    [Fact]
    public void A_back_face_enters_the_graveyard_face_up_with_front_face_data()
    {
        var entered = Zones.Move(Start().State, new ObjectId(1), ZoneKey.Shared(Zone.Battlefield), Placement.Default, false, Owner, 1, true);
        var permanent = Assert.Single(entered.State.Objects.Values, card => card.Zone.Zone == Zone.Battlefield);
        var moved = Zones.Move(entered.State, permanent.Id, Pile, Placement.Default, false, null, 1, true);
        var card = Assert.IsType<GameObject>(new Graveyard(moved.State, Owner).Top);
        Assert.Equal(0, card.FaceIndex);
        Assert.False(card.FaceDown);
        Assert.Equal("Front", card.Face.Name);
        Assert.Equal(permanent.CardInstanceId, card.CardInstanceId);
        Assert.NotEqual(permanent.Id, card.Id);
    }

    [Fact]
    public void Leaving_preserves_remaining_order_identity_and_hidden_destination_privacy_after_reload()
    {
        var start = Start();
        var first = Zones.Move(start.State, new ObjectId(1), Pile);
        var second = Zones.Move(first.State, new ObjectId(2), Pile);
        var before = new Graveyard(second.State, Owner);
        var departing = Assert.IsType<GameObject>(before.Top);
        var moved = Zones.Move(second.State, departing.Id, ZoneKey.Of(Zone.Hand, Owner));
        var log = start.Events.AddRange(first.Events).AddRange(second.Events).AddRange(moved.Events);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        var remaining = new Graveyard(restored, Owner);
        Assert.Equal(before.Order.RemoveAt(0), remaining.Order);
        Assert.False(remaining.Contains(departing.Id));
        Assert.Equal(2, before.Count); // The read-only view still describes its original immutable snapshot.
        var arrived = restored.Objects[Assert.Single(restored.In(ZoneKey.Of(Zone.Hand, Owner)))];
        Assert.NotEqual(departing.Id, arrived.Id);
        Assert.Equal(departing.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(departing.DeckSelection, arrived.DeckSelection);
        Assert.Equal(Owner, arrived.Owner);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        Assert.DoesNotContain("second-printing", JsonSerializer.Serialize(projector.Project(restored, new PlayerId(1))));
        Assert.Equal("second-printing", Assert.Single(projector.Project(restored, Owner).Hand).PrintingId);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void A_batch_preserves_the_callers_arrival_sequence_through_replay(int first, int last)
    {
        var start = Start();
        // Each arrival goes on top: callers supply the chosen batch bottom-to-top.
        // Choosing that order is a caller responsibility, not a public reorder permission.
        var moved = Zones.MoveAll(start.State,
            [new MoveRequest(new ObjectId(first), Pile), new MoveRequest(new ObjectId(last), Pile)]);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(start.Events.AddRange(moved.Events))));
        var graveyard = new Graveyard(restored, Owner);
        Assert.Equal(2, graveyard.Count);
        Assert.Equal(new[] { new CardInstanceId(last), new CardInstanceId(first) },
            graveyard.Order.Select(id => restored.Objects[id].CardInstanceId!.Value));
        Assert.Equal(2, graveyard.Order.Distinct().Count());
    }

    private static Outcome Start() => Zones.Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("card", "first-printing", CardFinish.Foil),
            new StartingCard("card", "second-printing", CardFinish.Etched)], []),
         new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
