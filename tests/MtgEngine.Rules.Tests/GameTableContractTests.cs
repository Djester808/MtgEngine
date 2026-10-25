using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "GameTable")]
public sealed class GameTableContractTests
{
    private static GameStarted Start() => new([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 42, 20, "casual");

    [Fact]
    public void Replay_requires_an_opening_event() =>
        Assert.Throws<InvalidOperationException>(() => GameReducer.Replay([]));

    [Fact]
    public void Replay_rejects_an_event_before_startup() =>
        Assert.Throws<InvalidOperationException>(() => GameReducer.Replay([new RngAdvanced(new RngState(5, 1)), Start()]));

    [Fact]
    public void Replay_cannot_reset_an_existing_game() =>
        Assert.Throws<InvalidOperationException>(() => GameReducer.Replay([Start(), new LifeChanged(new PlayerId(0), -2, "test"), Start()]));

    [Fact]
    public void Empty_incremental_fold_preserves_the_existing_snapshot()
    {
        var state = GameReducer.Replay([Start()]);
        Assert.Same(state, GameReducer.Apply(state, []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Startup_requires_a_format_label(string? format) =>
        Assert.ThrowsAny<ArgumentException>(() => GameReducer.Replay([Start() with { Format = format! }]));

    [Fact]
    public void Negative_opening_hand_size_is_invalid() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSystem().Create(new GameSetup(Start().Players, 42, OpeningHandSize: -1), new Store()));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Creation_and_reload_preserve_composition_identity_and_each_viewer(int count)
    {
        var players = Enumerable.Range(0, count).Select(i => new PlayerStart($"Seat {i}",
            [new StartingCard("card", $"printing-{i}", CardFinish.Foil), new StartingCard("card")], [new StartingCard("card")])).ToImmutableList();
        var created = new Game(Subsystems.Wave2(), new Store()).Create(new GameSetup(players, 42,
            StartingLife: 40, Format: "casual", OpeningHandSize: 1, StartingPlayer: count - 1));
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(created.Events)));
        Assert.Equal("casual", restored.Format);
        Assert.Equal(created.State.NextObjectId, restored.NextObjectId);
        Assert.Equal(created.State.NextTimestamp, restored.NextTimestamp);
        Assert.Equal(created.State.Rng, restored.Rng);
        Assert.Equal(3 * count, restored.Objects.Count);
        Assert.Equal(3 * count, restored.Objects.Values.Select(o => o.CardInstanceId).Distinct().Count());
        Assert.True(restored.NextObjectId > restored.Objects.Keys.Max(id => id.Value));
        var memberships = restored.Zones.SelectMany(zone => zone.Value.Select(id => (zone.Key, Id: id))).ToArray();
        Assert.Equal(restored.Objects.Count, memberships.Length);
        Assert.Equal(memberships.Length, memberships.Select(m => m.Id).Distinct().Count());
        Assert.All(memberships, m => Assert.Equal(m.Key, restored.Objects[m.Id].Zone));
        foreach (var obj in restored.Objects.Values)
        {
            var original = created.State.Objects[obj.Id];
            Assert.Equal(original.CardInstanceId, obj.CardInstanceId);
            Assert.Equal(original.DeckSelection, obj.DeckSelection);
            Assert.Equal(original.Owner, obj.Owner);
        }
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var player in restored.Players)
        {
            var view = projector.Project(restored, player.Id);
            Assert.Equal(JsonSerializer.Serialize(projector.Project(created.State, player.Id), EventLogSerializer.Options),
                JsonSerializer.Serialize(view, EventLogSerializer.Options));
            Assert.Equal(player.Id, view.Table!.Self.Player);
            Assert.Equal(player.Id.Value, view.Table.Self.Seat);
            Assert.Equal(Enumerable.Range(1, count - 1).Select(i => (player.Id.Value + i) % count), view.Table.Opponents.Select(s => s.Seat));
            Assert.All(view.Hand, card => Assert.Equal(player.Id, card.Owner));
            Assert.DoesNotContain("cardInstanceId", JsonSerializer.Serialize(view, EventLogSerializer.Options));
        }
    }

    private sealed class Store : IDefinitionStore
    {
        private static readonly CardDefinition Card = new("card", "Card", Layout.Normal,
            [new Face("Card", FaceRole.Main, new TypeLine(CardType.Land))]);
        public CardDefinition? ByOracleId(string oracleId) => oracleId == "card" ? Card : null;
        public CardDefinition? ByName(string name) => name == "Card" ? Card : null;
    }
}
