using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "CommandZone")]
public sealed class CommandZoneContractTests
{
    private static readonly ZoneSystem Zones = new();
    private static readonly ZoneKey Command = ZoneKey.Shared(Zone.Command);
    private static readonly CardDefinition Definition = new("card", "Front // Back", Layout.Transform,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Front text."),
         new Face("Back", FaceRole.Back, new TypeLine(CardType.Creature), OracleText: "Back text.")]);

    [Fact]
    public void Empty_zone_and_missing_members_are_safe()
    {
        var zone = new CommandZone(GameStarted.Empty);
        Assert.Equal(Command, zone.Key);
        Assert.Equal(0, zone.Count);
        Assert.Empty(zone.Find(_ => true));
        Assert.False(zone.Contains(new ObjectId(99)));
        Assert.Null(zone.Get(new ObjectId(99)));
        Assert.Throws<ArgumentNullException>(() => new CommandZone(null!));
        Assert.Throws<ArgumentNullException>(() => zone.Find(null!));
    }

    [Fact]
    public void Initial_commanders_share_storage_and_are_public_with_their_selected_printings()
    {
        var state = Start().State;
        var zone = new CommandZone(state);
        Assert.Equal(4, zone.Count);
        Assert.Single(state.Zones.Keys, key => key.Zone == Zone.Command);
        Assert.All(zone.Members, id =>
        {
            var card = Assert.IsType<GameObject>(zone.Get(id));
            Assert.True(card.IsCommander);
            Assert.False(card.FaceDown);
            Assert.Equal(Command, card.Zone);
            Assert.NotNull(card.CardInstanceId);
        });
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var viewer in state.Players)
        {
            var view = projector.Project(state, viewer.Id);
            foreach (var seat in view.Players)
            {
                var visible = Assert.Single(seat.Command);
                Assert.Equal(seat.Id, visible.Owner);
                Assert.Equal($"printing-{seat.Id.Value}", visible.PrintingId);
                Assert.Equal(CardFinish.Etched, visible.Finish);
                Assert.Equal("Front text.", visible.OracleText);
            }
        }
    }

    [Fact]
    public void Noncard_noncommander_objects_are_members_and_project_by_owner_not_controller()
    {
        var state = Start().State;
        var owner = new PlayerId(0);
        var emblem = new GameObject(new ObjectId(state.NextObjectId), Definition, 0,
            owner, new PlayerId(1), Command, state.NextTimestamp);
        state = new ObjectCreated(emblem, -1).Apply(state);
        var zone = new CommandZone(state);
        Assert.Equal(5, zone.Count);
        Assert.Same(emblem, zone.Get(emblem.Id));
        Assert.Null(emblem.CardInstanceId);
        Assert.False(emblem.IsCommander);
        Assert.Equal(emblem.Id, Assert.Single(zone.Find(obj => !obj.IsCommander)));
        foreach (var viewer in state.Players)
        {
            var view = new ViewProjector(new LayerSystem(), _ => true).Project(state, viewer.Id);
            Assert.Contains(view.Players[0].Command, obj => obj.Id == emblem.Id && obj.Controller == new PlayerId(1));
            Assert.DoesNotContain(view.Players[1].Command, obj => obj.Id == emblem.Id);
        }
    }

    [Fact]
    public void Commander_returns_from_back_face_with_front_face_description()
    {
        var start = Start();
        var id = new CommandZone(start.State).Members[0];
        var entered = Zones.Move(start.State, id, ZoneKey.Shared(Zone.Battlefield), Placement.Default, false, null, 1);
        var permanent = Assert.Single(entered.State.Objects.Values, obj => obj.Zone.Zone == Zone.Battlefield);
        var returned = Zones.Move(entered.State, permanent.Id, Command);
        var replay = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(
            start.Events.AddRange(entered.Events).AddRange(returned.Events))));
        var card = replay.Objects.Values.Single(obj => obj.CardInstanceId == permanent.CardInstanceId);
        Assert.Equal(0, card.FaceIndex);
        Assert.True(card.IsCommander);
        Assert.Equal(permanent.DeckSelection, card.DeckSelection);
        Assert.Equal("Front text.", Assert.Single(new ViewProjector(new LayerSystem(), _ => true)
            .Project(replay, new PlayerId(1)).Players[0].Command).OracleText);
    }

    [Fact]
    public void Movement_and_replay_preserve_designation_selection_and_private_hand_boundary()
    {
        var start = Start();
        var before = new CommandZone(start.State);
        var card = before.Get(before.Members[0])!;
        var moved = Zones.Move(start.State, card.Id, ZoneKey.Of(Zone.Hand, new PlayerId(1)));
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(start.Events.AddRange(moved.Events))));
        var zone = new CommandZone(restored);
        Assert.Equal(3, zone.Count);
        Assert.Equal(4, before.Count);
        Assert.Null(zone.Get(card.Id));
        var arrived = restored.Objects[Assert.Single(restored.In(ZoneKey.Of(Zone.Hand, card.Owner)))];
        Assert.True(arrived.IsCommander);
        Assert.NotEqual(card.Id, arrived.Id);
        Assert.Equal(card.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(card.DeckSelection, arrived.DeckSelection);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        Assert.DoesNotContain("printing-0", JsonSerializer.Serialize(projector.Project(restored, new PlayerId(1))));
        Assert.Equal("printing-0", Assert.Single(projector.Project(restored, card.Owner).Hand).PrintingId);
        var returned = Zones.Move(restored, arrived.Id, ZoneKey.Of(Zone.Command, new PlayerId(2)));
        var replay = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(returned.Events)));
        var current = new CommandZone(replay).Get(new CommandZone(replay).Find(obj => obj.Owner == card.Owner)[0])!;
        Assert.Equal(Command, current.Zone);
        Assert.True(current.IsCommander);
        Assert.Equal(card.DeckSelection, current.DeckSelection);
        Assert.Equal(card.CardInstanceId, current.CardInstanceId);
    }

    private static Outcome Start() => Zones.Create(new GameSetup(
        [.. Enumerable.Range(0, 4).Select(i => new PlayerStart($"P{i}", [],
            [new StartingCard("card", $"printing-{i}", CardFinish.Etched)]))],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
