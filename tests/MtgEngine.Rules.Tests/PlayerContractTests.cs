using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Player")]
public sealed class PlayerContractTests
{
    private static GameStarted Start(int startingPlayer = 0) =>
        new([new PlayerStart("Same name", [], []), new PlayerStart("Same name", [], [])], 1, 40, "commander", startingPlayer);

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Starting_seat_must_exist(int seat) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GameReducer.Replay([Start(seat)]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Initial_player_requires_a_display_name(string? name)
    {
        var start = Start() with { Players = [new PlayerStart(name!, [], [])] };
        Assert.ThrowsAny<ArgumentException>(() => GameReducer.Replay([start]));
    }

    [Fact]
    public void Lookup_rejects_a_seat_whose_stored_identity_disagrees()
    {
        var state = GameReducer.Replay([Start()]);
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { Id = new PlayerId(1) }) };
        Assert.Throws<InvalidOperationException>(() => state.Player(new PlayerId(0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Lookup_requires_an_existing_seat(int seat) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GameReducer.Replay([Start()]).Player(new PlayerId(seat)));

    [Fact]
    public void Creation_requires_a_nonnegative_seat() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerState.Create(new PlayerId(-1), "A", 20));

    [Fact]
    public void Empty_rosters_cannot_start() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GameReducer.Replay([Start() with { Players = [] }]));

    [Theory]
    [InlineData(Zone.Library)]
    [InlineData(Zone.Hand)]
    [InlineData(Zone.Graveyard)]
    public void Owned_zone_access_uses_the_existing_state_membership(Zone zone)
    {
        var state = GameReducer.Replay([Start()]);
        var player = state.Player(new PlayerId(1));
        var key = player.OwnedZone(zone);
        Assert.Equal(ZoneKey.Of(zone, player.Id), key);
        Assert.Empty(state.In(key));
        var populated = state with { Zones = state.Zones.SetItem(key, [new ObjectId(42)]) };
        Assert.Equal(new ObjectId(42), Assert.Single(populated.In(player.OwnedZone(zone))));
        Assert.Empty(state.In(key));
        Assert.Empty(populated.In(state.Player(new PlayerId(0)).OwnedZone(zone)));
    }

    [Theory]
    [InlineData(Zone.Battlefield)]
    [InlineData(Zone.Stack)]
    [InlineData(Zone.Exile)]
    [InlineData(Zone.Command)]
    [InlineData((Zone)999)]
    public void Shared_or_unknown_zones_are_not_player_owned(Zone zone) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerState.Create(new PlayerId(0), "A", 20).OwnedZone(zone));

    [Fact]
    public void Duplicate_names_are_distinct_seats_and_existing_data_replays()
    {
        var initial = GameReducer.Replay([Start(1)]);
        var events = new GameEvent[] { Start(1), new LifeChanged(new PlayerId(0), -43, "test"),
            new PlayerCountersChanged(new PlayerId(1), "poison", 2), new PlayerLost(new PlayerId(0), "test") };
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var first = restored.Player(new PlayerId(0));
        var second = restored.Player(new PlayerId(1));
        Assert.Equal(first.Name, second.Name);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(-3, first.Life);
        Assert.True(first.HasLost);
        Assert.False(second.HasLost);
        Assert.Equal(40, second.Life);
        Assert.Equal(2, second.Counters["poison"]);
        Assert.Empty(first.Counters);
        Assert.Equal(7, second.MaxHandSize);
        Assert.Equal(ManaPool.Empty, second.Mana);
        Assert.Equal(40, initial.Player(first.Id).Life);
        Assert.False(initial.Player(first.Id).HasLost);
        Assert.Equal(new PlayerId(1), restored.Turn.ActivePlayer);
        var roundTrip = JsonSerializer.Deserialize<PlayerState>(JsonSerializer.Serialize(first, EventLogSerializer.Options), EventLogSerializer.Options)!;
        Assert.Equal(first.Id, roundTrip.Id);
        Assert.Equal(first.Life, roundTrip.Life);
        Assert.Equal(first.HasLost, roundTrip.HasLost);
    }

    [Fact]
    public void Ownership_and_controller_do_not_grant_another_viewer_the_hand()
    {
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var definition = new CardDefinition("private-card", "Private card", Layout.Normal,
            [new Face("Private card", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var card = new GameObject(new ObjectId(1), definition, 0, owner, other, ZoneKey.Of(Zone.Hand, owner), 1);
        var state = GameReducer.Replay([Start(), new ObjectCreated(card, 0)]);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        var ownerView = projector.Project(state, owner);
        var otherView = projector.Project(state, other);
        Assert.Equal(card.Id, Assert.Single(ownerView.Hand).Id);
        Assert.Empty(otherView.Hand);
        Assert.Equal(1, otherView.Players[0].HandCount);
        Assert.DoesNotContain("private-card", JsonSerializer.Serialize(otherView));
        Assert.Equal(owner, state.Objects[card.Id].Owner);
        Assert.Equal(other, state.Objects[card.Id].InitialController);
    }
}
