using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Hand")]
public sealed class HandContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly ZoneKey Key = ZoneKey.Of(Zone.Hand, Owner);
    private static readonly ZoneSystem Zones = new();
    private static readonly CardDefinition Definition = new("hand-card", "Front // Back", Layout.Transform,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Private front text."),
         new Face("Back", FaceRole.Back, new TypeLine(CardType.Creature), OracleText: "Back text.")]);

    [Fact]
    public void Empty_hands_are_owner_specific_and_lookup_is_read_only()
    {
        var state = Start().State;
        foreach (var player in state.Players)
        {
            var hand = new Hand(state, player.Id);
            Assert.Equal(player.Id, hand.Owner);
            Assert.Equal(ZoneKey.Of(Zone.Hand, player.Id), hand.Key);
            Assert.Equal(0, hand.Count);
            Assert.Empty(hand.Members);
            Assert.Empty(hand.Find(_ => true));
            Assert.False(hand.Contains(new ObjectId(999)));
            Assert.Throws<ArgumentNullException>(() => hand.Find(null!));
        }
        Assert.Throws<ArgumentNullException>(() => new Hand(null!, Owner));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_hand_requires_an_existing_owner(int owner) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Hand(Start().State, new PlayerId(owner)));

    [Fact]
    public void Shared_draw_moves_exactly_the_top_copy_and_replays_its_selection()
    {
        var start = Start();
        var empty = new Hand(start.State, Owner);
        var library = ZoneKey.Of(Zone.Library, Owner);
        var top = start.State.Objects[start.State.In(library)[0]];
        var draw = LibraryDraw.Apply(start.State, Owner);
        var restored = Replay(start.Events.AddRange(draw.Events));
        var hand = new Hand(restored, Owner);
        var arrived = restored.Objects[Assert.Single(hand.Members)];
        Assert.Equal(1, hand.Count);
        Assert.Equal(0, empty.Count);
        Assert.Single(restored.In(library));
        Assert.NotEqual(top.Id, arrived.Id);
        Assert.Equal(top.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(top.DeckSelection, arrived.DeckSelection);
        Assert.Equal(arrived.Id, Assert.Single(hand.Find(card => card.DeckSelection == top.DeckSelection)));
        Assert.False(hand.Contains(top.Id));
        Assert.IsType<ObjectMoved>(draw.Events[0]);
        Assert.IsType<ObjectDrawn>(draw.Events[1]);
    }

    [Fact]
    public void Entry_routes_to_owner_and_other_seats_receive_only_the_count_even_with_foreign_control()
    {
        var start = Start();
        var moved = Zones.Move(start.State, new ObjectId(1), ZoneKey.Of(Zone.Hand, new PlayerId(1)), controller: new PlayerId(1));
        var card = moved.State.Objects[Assert.Single(new Hand(moved.State, Owner).Members)];
        Assert.Equal(Owner, card.Owner);
        Assert.Equal(Owner, card.InitialController);
        // Even a server object carrying a foreign controller is not a disclosure permission.
        var state = moved.State with { Objects = moved.State.Objects.SetItem(card.Id, card with { InitialController = new PlayerId(1) }) };
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var player in state.Players)
        {
            var view = projector.Project(state, player.Id);
            Assert.Equal(1, view.Players[0].HandCount);
            Assert.All(view.Players.Skip(1), seat => Assert.Equal(0, seat.HandCount));
            var json = JsonSerializer.Serialize(view);
            Assert.DoesNotContain("CardInstanceId", json);
            if (player.Id == Owner)
            {
                var visible = Assert.Single(view.Hand);
                Assert.Equal(card.Id, visible.Id);
                Assert.Equal("first-printing", visible.PrintingId);
                Assert.Equal(CardFinish.Foil, visible.Finish);
                Assert.Equal("Private front text.", visible.OracleText);
            }
            else
            {
                Assert.Empty(view.Hand);
                Assert.DoesNotContain("first-printing", json);
                Assert.DoesNotContain("hand-card", json);
                Assert.DoesNotContain("Private front text.", json);
            }
        }
    }

    [Theory]
    [InlineData(Placement.Default)]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Same_hand_moves_do_not_change_identity_or_authoritative_membership(Placement placement)
    {
        var drawn = Zones.Draw(Start().State, Owner, 2);
        var hand = new Hand(drawn.State, Owner);
        var outcome = Zones.Move(drawn.State, hand.Members[0], Key, placement);
        Assert.Empty(outcome.Events);
        Assert.Same(drawn.State, outcome.State);
        Assert.Equal(hand.Members, new Hand(outcome.State, Owner).Members);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Return_from_battlefield_uses_front_face_visible_to_owner_only(bool faceDown)
    {
        var start = Start();
        var entered = Zones.Move(start.State, new ObjectId(1), ZoneKey.Shared(Zone.Battlefield), Placement.Default, false, new PlayerId(1), 1, faceDown);
        var permanent = Assert.Single(entered.State.Objects.Values, card => card.Zone.Zone == Zone.Battlefield);
        var returned = Zones.Move(entered.State, permanent.Id, Key, Placement.Default, false, null, 1, faceDown);
        var restored = Replay(start.Events.AddRange(entered.Events).AddRange(returned.Events));
        var card = restored.Objects[Assert.Single(new Hand(restored, Owner).Members)];
        Assert.Equal(0, card.FaceIndex);
        Assert.False(card.FaceDown);
        Assert.Equal(permanent.CardInstanceId, card.CardInstanceId);
        Assert.Equal(permanent.DeckSelection, card.DeckSelection);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        Assert.Equal("Front", Assert.Single(projector.Project(restored, Owner).Hand).Name);
        Assert.Empty(projector.Project(restored, new PlayerId(1)).Hand);
    }

    [Fact]
    public void Departure_removes_only_the_selected_copy_and_replay_preserves_survivors()
    {
        var start = Start();
        var drawn = Zones.Draw(start.State, Owner, 2);
        var before = new Hand(drawn.State, Owner);
        var departing = drawn.State.Objects[before.Members[0]];
        var moved = Zones.Move(drawn.State, departing.Id, ZoneKey.Of(Zone.Graveyard, Owner));
        var restored = Replay(start.Events.AddRange(drawn.Events).AddRange(moved.Events));
        var after = new Hand(restored, Owner);
        Assert.Equal(before.Members.Remove(departing.Id), after.Members);
        Assert.False(after.Contains(departing.Id));
        Assert.Equal(2, before.Count);
        var arrived = Assert.IsType<GameObject>(new Graveyard(restored, Owner).Top);
        Assert.NotEqual(departing.Id, arrived.Id);
        Assert.Equal(departing.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(departing.DeckSelection, arrived.DeckSelection);
        var view = new ViewProjector(new LayerSystem(), _ => true).Project(restored, new PlayerId(1));
        Assert.Equal(1, view.Players[0].HandCount);
        Assert.Empty(view.Hand);
        Assert.Equal(arrived.Id, Assert.Single(view.Players[0].Graveyard).Id);
    }

    [Fact]
    public void Empty_library_draw_does_not_fabricate_a_hand_member()
    {
        var start = Start();
        var drawn = Zones.Draw(start.State, Owner, 2);
        var before = new Hand(drawn.State, Owner);
        var empty = LibraryDraw.Apply(drawn.State, Owner);
        Assert.IsType<DrawFromEmptyLibraryAttempted>(Assert.Single(empty.Events));
        Assert.Equal(before.Members, new Hand(empty.State, Owner).Members);
    }

    private static GameState Replay(ImmutableList<GameEvent> events) =>
        GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(events)));

    private static Outcome Start() => Zones.Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("hand-card", "first-printing", CardFinish.Foil),
            new StartingCard("hand-card", "second-printing", CardFinish.Etched)], []),
         new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
