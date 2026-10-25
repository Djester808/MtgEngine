using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Exile")]
public sealed class ExileContractTests
{
    private static readonly ZoneSystem Zones = new();
    private static readonly ZoneKey Shared = ZoneKey.Shared(Zone.Exile);
    private static readonly PlayerId Owner = new(0);
    private static readonly CardDefinition Definition = new("card", "Front // Back", Layout.Transform,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Front text."),
         new Face("Back", FaceRole.Back, new TypeLine(CardType.Creature), OracleText: "Back text.")]);

    [Fact]
    public void Empty_shared_snapshot_has_no_owner_or_members()
    {
        var exile = new ExileZone(Start().State);
        Assert.Equal(Shared, exile.Key);
        Assert.Null(exile.Key.Player);
        Assert.Equal(0, exile.Count);
        Assert.Empty(exile.Find(_ => true));
        Assert.False(exile.Contains(new ObjectId(1)));
        Assert.Null(exile.Get(new ObjectId(1)));
        Assert.Throws<ArgumentNullException>(() => new ExileZone(null!));
        Assert.Throws<ArgumentNullException>(() => exile.Find(null!));
    }

    [Fact]
    public void Cards_of_different_owners_share_membership_and_public_inspection()
    {
        var first = Zones.Move(Start().State, new ObjectId(1), ZoneKey.Of(Zone.Exile, Owner));
        var second = Zones.Move(first.State, new ObjectId(2), ZoneKey.Of(Zone.Exile, new PlayerId(3)));
        var exile = new ExileZone(second.State);
        Assert.Equal(2, exile.Count);
        Assert.Single(second.State.Zones.Keys, key => key.Zone == Zone.Exile);
        Assert.Single(exile.Find(card => card.Owner == Owner));
        Assert.Single(exile.Find(card => card.Owner == new PlayerId(1)));
        foreach (var player in second.State.Players)
        {
            var view = Project(second.State, player.Id);
            Assert.Equal(exile.Members, view.Exile.Select(card => card.Id));
            Assert.All(view.Exile, card => Assert.Equal("Front text.", card.OracleText));
            Assert.Equal(new[] { "first-printing", "second-printing" }, view.Exile.Select(card => card.PrintingId));
            Assert.DoesNotContain("CardInstanceId", JsonSerializer.Serialize(view));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exile_entry_resets_back_face_and_preserves_only_requested_face_down_status(bool faceDown)
    {
        var entered = Zones.Move(Start().State, new ObjectId(1), ZoneKey.Shared(Zone.Battlefield),
            Placement.Default, false, new PlayerId(1), 1, true);
        var permanent = Assert.Single(entered.State.Objects.Values, card => card.Zone.Zone == Zone.Battlefield);
        var moved = Zones.Move(entered.State, permanent.Id, Shared, Placement.Default, false, null, 1, faceDown);
        var card = moved.State.Objects[Assert.Single(new ExileZone(moved.State).Members)];
        Assert.Equal(0, card.FaceIndex);
        Assert.Equal(faceDown, card.FaceDown);
        Assert.Equal(Owner, card.Owner);
        Assert.Equal(permanent.CardInstanceId, card.CardInstanceId);
        Assert.Equal(permanent.DeckSelection, card.DeckSelection);
        Assert.NotEqual(permanent.Id, card.Id);
        if (!faceDown)
            Assert.Equal("Front text.", Assert.Single(Project(moved.State, Owner).Exile).OracleText);
    }

    [Fact]
    public void Face_down_exile_identity_is_withheld_from_every_seat_including_owner_after_reload()
    {
        var start = Start();
        var moved = Zones.Move(start.State, new ObjectId(1), Shared, Placement.Default, false, null, null, true);
        var restored = Replay(start.Events.AddRange(moved.Events));
        foreach (var player in restored.Players)
        {
            var card = Assert.Single(Project(restored, player.Id).Exile);
            Assert.True(card.FaceDown);
            Assert.Equal(string.Empty, card.Name);
            Assert.Equal(string.Empty, card.OracleId);
            Assert.Null(card.PrintingId);
            Assert.Null(card.Finish);
            Assert.Null(card.OracleText);
            Assert.Null(card.Power);
            Assert.Null(card.Toughness);
            Assert.Empty(card.Types);
            Assert.Empty(card.Abilities);
        }
    }

    [Fact]
    public void An_explicit_private_look_discloses_only_to_its_recipient_through_replay()
    {
        var start = Start();
        var moved = Zones.Move(start.State, new ObjectId(1), Shared, Placement.Default, false, null, null, true);
        var recipient = new PlayerId(2);
        var disclosed = new CardsLookedAt([recipient],
            [new RevealedCard("card", "Front", null, "Front text.", "first-printing")]);
        var state = Replay(start.Events.AddRange(moved.Events).Add(disclosed));
        foreach (var player in state.Players)
        {
            var view = Project(state, player.Id);
            Assert.Equal(string.Empty, Assert.Single(view.Exile).OracleId);
            if (player.Id == recipient)
                Assert.Equal("first-printing", Assert.Single(Assert.IsType<CardReveal>(view.LastLook).Cards).PrintingId);
            else
                Assert.Null(view.LastLook);
        }
        // A historical disclosure is not an ongoing permission or a link to a hidden object's id.
        Assert.DoesNotContain("ObjectId", JsonSerializer.Serialize(Project(state, recipient).LastLook));
    }

    [Fact]
    public void Exiling_an_exiled_card_creates_a_new_object_without_duplicating_membership()
    {
        var first = Zones.Move(Start().State, new ObjectId(1), Shared);
        var old = first.State.Objects[Assert.Single(new ExileZone(first.State).Members)];
        var second = Zones.Move(first.State, old.Id, Shared);
        var current = second.State.Objects[Assert.Single(new ExileZone(second.State).Members)];
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal(old.CardInstanceId, current.CardInstanceId);
        Assert.Equal(old.DeckSelection, current.DeckSelection);
        Assert.False(new ExileZone(second.State).Contains(old.Id));
        Assert.Single(second.Events.OfType<ObjectMoved>());
    }

    [Fact]
    public void Departure_preserves_snapshot_and_selection_and_private_destination_on_replay()
    {
        var start = Start();
        var entered = Zones.MoveAll(start.State,
            [new MoveRequest(new ObjectId(1), Shared), new MoveRequest(new ObjectId(2), Shared)]);
        var snapshot = new ExileZone(entered.State);
        var leaving = Assert.IsType<GameObject>(snapshot.Get(snapshot.Members[0]));
        var moved = Zones.Move(entered.State, leaving.Id, ZoneKey.Of(Zone.Hand, new PlayerId(2)));
        var state = Replay(start.Events.AddRange(entered.Events).AddRange(moved.Events));
        var exile = new ExileZone(state);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(snapshot.Members.Remove(leaving.Id), exile.Members);
        Assert.Null(exile.Get(leaving.Id));
        var arrived = state.Objects[Assert.Single(state.In(ZoneKey.Of(Zone.Hand, Owner)))];
        Assert.Equal(leaving.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(leaving.DeckSelection, arrived.DeckSelection);
        Assert.Equal(Owner, arrived.Owner);
        Assert.NotEqual(leaving.Id, arrived.Id);
        Assert.Equal("first-printing", Assert.Single(Project(state, Owner).Hand).PrintingId);
        Assert.DoesNotContain("first-printing", JsonSerializer.Serialize(Project(state, new PlayerId(1))));
    }

    private static GameView Project(GameState state, PlayerId viewer) => new ViewProjector(new LayerSystem(), _ => true).Project(state, viewer);
    private static GameState Replay(IEnumerable<GameEvent> events) => GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(events)));
    private static Outcome Start() => Zones.Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("card", "first-printing", CardFinish.Foil)], []),
         new PlayerStart("B", [new StartingCard("card", "second-printing", CardFinish.Etched)], []),
         new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
