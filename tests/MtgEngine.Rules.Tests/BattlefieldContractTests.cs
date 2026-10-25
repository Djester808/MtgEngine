using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Battlefield")]
public sealed class BattlefieldContractTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Other = new(1);
    private static readonly ZoneKey Key = ZoneKey.Shared(Zone.Battlefield);
    private static readonly ZoneSystem Zones = new();
    private static readonly LayerSystem Characteristics = new();
    private static readonly CardDefinition Definition = new("card", "Test permanent", Layout.Normal,
        [new Face("Test permanent", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Descriptive text.")]);

    [Fact]
    public void Shared_battlefield_starts_empty_and_lookup_is_membership_scoped()
    {
        var battlefield = new Battlefield(Start().State);
        Assert.Equal(Key, battlefield.Key);
        Assert.Equal(0, battlefield.Count);
        Assert.Empty(battlefield.ActiveMembers);
        Assert.Empty(battlefield.Find(_ => true));
        Assert.False(battlefield.Contains(new ObjectId(1)));
        Assert.Null(battlefield.Get(new ObjectId(1))); // It exists, but in the library.
        Assert.False(Start().State.Objects[new ObjectId(1)].IsPermanent);
        Assert.Throws<ArgumentNullException>(() => new Battlefield(null!));
        Assert.Throws<ArgumentNullException>(() => battlefield.Find(null!));
    }

    [Fact]
    public void Shared_membership_is_public_and_layout_uses_current_control_not_owner()
    {
        var moved = Zones.Move(Start().State, new ObjectId(1), ZoneKey.Of(Zone.Battlefield, Other), controller: Other);
        var battlefield = new Battlefield(moved.State);
        var id = Assert.Single(battlefield.Members);
        Assert.Equal(Key, battlefield.Get(id)!.Zone);
        Assert.Equal(Owner, battlefield.Get(id)!.Owner);
        Assert.Equal(id, Assert.Single(battlefield.OwnedBy(Owner)));
        Assert.Empty(battlefield.OwnedBy(Other));
        Assert.Equal(id, Assert.Single(battlefield.ControlledBy(Other, Characteristics)));
        foreach (var player in moved.State.Players)
        {
            var view = Project(moved.State, player.Id);
            var visible = Assert.Single(view.Battlefield);
            Assert.Equal(id, visible.Id);
            Assert.Equal(Owner, visible.Owner);
            Assert.Equal(Other, visible.Controller);
            Assert.Equal("printing", visible.PrintingId);
            Assert.Equal(CardFinish.Etched, visible.Finish);
            Assert.Equal("Descriptive text.", visible.OracleText);
            var seats = view.Table!.Opponents.Insert(0, view.Table.Self);
            Assert.Equal(id, Assert.Single(seats.Single(seat => seat.Player == Other).Battlefield.Creatures));
            Assert.Empty(seats.Single(seat => seat.Player == Owner).Battlefield.Creatures);
        }
    }

    [Fact]
    public void Controller_selection_uses_the_existing_characteristics_evaluator()
    {
        var moved = Zones.Move(Start().State, new ObjectId(1), Key);
        var id = Assert.Single(moved.State.In(Key));
        var state = moved.State with
        {
            ActiveEffects = [new ActiveEffect(1, new ControlBy(new You()), null, Other,
                [id], [], new UntilEndOfTurn(), 10, false)],
        };
        var battlefield = new Battlefield(state);
        Assert.Equal(Owner, battlefield.Get(id)!.InitialController);
        Assert.Equal(id, Assert.Single(battlefield.OwnedBy(Owner)));
        Assert.Empty(battlefield.ControlledBy(Owner, Characteristics));
        Assert.Equal(id, Assert.Single(battlefield.ControlledBy(Other, Characteristics)));
        Assert.Equal(Other, Assert.Single(Project(state, Owner).Battlefield).Controller);
    }

    [Theory]
    [InlineData(Placement.Default)]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Same_battlefield_movement_does_not_recreate_or_reset_a_permanent(Placement placement)
    {
        var moved = Zones.Move(Start().State, new ObjectId(1), Key, tapped: true);
        var id = Assert.Single(moved.State.In(Key));
        var result = Zones.Move(moved.State, id, ZoneKey.Of(Zone.Battlefield, Other), placement, controller: Other);
        Assert.Empty(result.Events);
        Assert.Same(moved.State, result.State);
        Assert.True(result.State.Objects[id].Tapped);
        Assert.Equal(Owner, Characteristics.ControllerOf(result.State, id));
    }

    [Fact]
    public void Phasing_retains_membership_identity_and_public_presentation_through_replay()
    {
        var start = Start();
        var moved = Zones.Move(start.State, new ObjectId(1), Key);
        var id = Assert.Single(moved.State.In(Key));
        var before = new Battlefield(moved.State);
        var phase = new ObjectChanged(id, PhasedOut: true);
        var log = start.Events.AddRange(moved.Events).Add(phase);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        var battlefield = new Battlefield(restored);
        Assert.Equal(1, battlefield.Count);
        Assert.Equal(id, Assert.Single(battlefield.Members));
        Assert.Empty(battlefield.ActiveMembers);
        Assert.True(battlefield.Get(id)!.PhasedOut);
        Assert.True(battlefield.Get(id)!.IsPermanent);
        Assert.DoesNotContain("IsPermanent", JsonSerializer.Serialize(battlefield.Get(id)));
        Assert.Equal(before.Get(id)!.CardInstanceId, battlefield.Get(id)!.CardInstanceId);
        Assert.Equal(id, Assert.Single(Project(restored, Other).Battlefield).Id);
        Assert.Equal(id, Assert.Single(before.ActiveMembers));
        var returned = new Battlefield(new ObjectChanged(id, PhasedOut: false).Apply(restored));
        Assert.Equal(id, Assert.Single(returned.ActiveMembers));
    }

    [Fact]
    public void Face_down_members_keep_public_identity_but_withhold_card_details_from_all_seats()
    {
        var moved = Zones.Move(Start().State, new ObjectId(1), Key, Placement.Default, false, Other, null, true);
        var battlefield = new Battlefield(moved.State);
        var id = Assert.Single(battlefield.Members);
        Assert.Equal("printing", battlefield.Get(id)!.DeckSelection!.PrintingId); // Server-only.
        foreach (var player in moved.State.Players)
        {
            var view = Project(moved.State, player.Id);
            var visible = Assert.Single(view.Battlefield);
            Assert.Equal(id, visible.Id);
            Assert.True(visible.FaceDown);
            Assert.Equal(Owner, visible.Owner);
            Assert.Equal(Other, visible.Controller);
            Assert.Empty(visible.Name);
            Assert.Empty(visible.OracleId);
            Assert.Null(visible.PrintingId);
            Assert.Null(visible.Finish);
            Assert.Null(visible.OracleText);
            Assert.DoesNotContain("CardInstanceId", JsonSerializer.Serialize(view));
        }
    }

    [Fact]
    public void Departure_removes_membership_and_preserves_physical_selection_with_private_hand_replay()
    {
        var start = Start();
        var entered = Zones.Move(start.State, new ObjectId(1), Key, controller: Other);
        var before = new Battlefield(entered.State);
        var id = Assert.Single(before.Members);
        var departed = Zones.Move(entered.State, id, ZoneKey.Of(Zone.Hand, Other));
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(
            start.Events.AddRange(entered.Events).AddRange(departed.Events))));
        var after = new Battlefield(restored);
        Assert.Empty(after.Members);
        Assert.False(after.Contains(id));
        Assert.Null(after.Get(id));
        Assert.Equal(1, before.Count);
        var hand = restored.Objects[Assert.Single(restored.In(ZoneKey.Of(Zone.Hand, Owner)))];
        Assert.NotEqual(id, hand.Id);
        Assert.True(before.Get(id)!.IsPermanent);
        Assert.False(hand.IsPermanent);
        Assert.Equal(before.Get(id)!.CardInstanceId, hand.CardInstanceId);
        Assert.Equal(before.Get(id)!.DeckSelection, hand.DeckSelection);
        Assert.Empty(Project(restored, Other).Battlefield);
        Assert.DoesNotContain("printing", JsonSerializer.Serialize(Project(restored, Other)));
        Assert.Equal("printing", Assert.Single(Project(restored, Owner).Hand).PrintingId);
    }

    private static GameView Project(GameState state, PlayerId viewer) => new ViewProjector(Characteristics, _ => true).Project(state, viewer);

    private static Outcome Start() => Zones.Create(new GameSetup(
        [new PlayerStart("A", [new StartingCard("card", "printing", CardFinish.Etched)], []),
         new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
        1, OpeningHandSize: 0), new Store());

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
