using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class DeckSelectionTests
{
    [Fact]
    public void Departing_controller_exiles_another_players_card_without_losing_its_identity()
    {
        var zones = new ZoneSystem();
        var owner = new PlayerId(0);
        var controller = new PlayerId(1);
        var selection = new StartingCard("card", "selected-printing", CardFinish.Foil);
        var created = zones.Create(new GameSetup(
            [new PlayerStart("A", [selection], []), new PlayerStart("B", [], []), new PlayerStart("C", [], [])],
            1, OpeningHandSize: 0), new Store());
        var original = Assert.Single(created.State.Objects.Values);
        var moved = zones.Move(created.State, original.Id, ZoneKey.Shared(Zone.Battlefield), controller: controller);
        var permanent = Assert.Single(moved.State.Objects.Values);
        var log = created.Events.AddRange(moved.Events).Add(new PlayerLost(controller, "conceded"));
        var state = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        var exiled = Assert.Single(state.Objects.Values);
        Assert.Equal(Zone.Exile, exiled.Zone.Zone);
        Assert.NotEqual(permanent.Id, exiled.Id);
        Assert.Equal(original.CardInstanceId, exiled.CardInstanceId);
        Assert.Equal(selection, exiled.DeckSelection);
        Assert.Equal(owner, exiled.Owner);
    }

    [Fact]
    public void Commander_totals_follow_individual_cards_through_movement_and_replay()
    {
        var zones = new ZoneSystem();
        var owner = new PlayerId(0);
        var opponent = new PlayerId(1);
        var created = zones.Create(new GameSetup(
            [new PlayerStart("A", [], ["card", "card"]), new PlayerStart("B", [], [])],
            1, OpeningHandSize: 0), new Store());
        var commanders = created.State.Objects.Values.OrderBy(card => card.Id.Value).ToArray();
        var first = commanders[0];
        var second = commanders[1];
        var log = created.Events.Add(new MtgEngine.Rules.Events.SpellCast(first.Id, owner, Zone.Command))
            .Add(new DamageDealt(first.Id, null, opponent, 7, true, true))
            .Add(new DamageDealt(second.Id, null, opponent, 14, true, true));
        var state = GameReducer.Replay(log);
        Assert.Equal(1, state.CommandZoneCasts(first, owner));
        Assert.Equal(0, state.CommandZoneCasts(second, owner));
        Assert.Equal(2, state.Player(opponent).CommanderDamage.Count);
        Assert.Null(MtgEngine.Rules.Commander.CommanderDamage.LethalSource(state.Player(opponent)));

        var moved = zones.Move(state, first.Id, ZoneKey.Of(Zone.Hand, owner));
        var arrived = Assert.Single(moved.State.Objects.Values, card => card.Zone.Zone == Zone.Hand);
        log = log.AddRange(moved.Events).Add(new DamageDealt(arrived.Id, null, opponent, 14, true, true));
        state = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        Assert.Equal(1, state.CommandZoneCasts(arrived, owner));
        Assert.Equal(0, state.CommandZoneCasts(second, owner));
        Assert.Equal(2, state.Player(opponent).CommanderDamage.Count);
        Assert.NotNull(MtgEngine.Rules.Commander.CommanderDamage.LethalSource(state.Player(opponent)));

        // Copied characteristics must not change which physical commander was cast.
        var disguised = arrived with { Definition = arrived.Definition with { OracleId = "copied-card" } };
        Assert.Equal(1, state.CommandZoneCasts(disguised, owner));
    }

    [Fact]
    public void Selected_printings_survive_drawing_moving_and_replay_but_do_not_transfer_to_copies()
    {
        var zones = new ZoneSystem();
        var owner = new PlayerId(0);
        var selections = new[] { new StartingCard("card", "first-printing", CardFinish.Foil), new StartingCard("card", "second-printing", CardFinish.Etched) };
        var created = zones.Create(new GameSetup([new PlayerStart("A", [.. selections], []), new PlayerStart("B", [], [])], 1,
            OpeningHandSize: 0), new Store());
        var log = created.Events;
        var drawn = zones.Draw(created.State, owner, 2);
        log = log.AddRange(drawn.Events);
        Assert.Equal(selections.OrderBy(card => card.PrintingId), drawn.State.Objects.Values.Select(card => card.DeckSelection!).OrderBy(card => card.PrintingId));
        var original = drawn.State.Objects.Values.First();
        Assert.All(drawn.State.Objects.Values, card => Assert.NotNull(card.CardInstanceId));
        Assert.Equal(2, drawn.State.Objects.Values.Select(card => card.CardInstanceId).Distinct().Count());
        var moved = zones.Move(drawn.State, original.Id, ZoneKey.Of(Zone.Graveyard, owner));
        log = log.AddRange(moved.Events);
        var arrived = Assert.Single(moved.State.Objects.Values, card => card.Zone.Zone == Zone.Graveyard);
        Assert.NotEqual(original.Id, arrived.Id);
        Assert.Equal(original.CardInstanceId, arrived.CardInstanceId);
        Assert.Equal(original.DeckSelection, arrived.DeckSelection);
        var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
        Assert.Equal(arrived.DeckSelection, restored.Objects[arrived.Id].DeckSelection);
        Assert.Equal(arrived.CardInstanceId, restored.Objects[arrived.Id].CardInstanceId);
        var copied = zones.CopyInPlace(restored, arrived.Id, owner);
        Assert.Null(Assert.Single(copied.Events.OfType<ObjectCreated>()).Object.DeckSelection);
        Assert.Null(Assert.Single(copied.Events.OfType<ObjectCreated>()).Object.CardInstanceId);
        Assert.Equal(arrived.DeckSelection, copied.State.Objects[arrived.Id].DeckSelection);
    }

    [Fact]
    public void Hidden_and_face_down_views_do_not_disclose_printing_or_finish()
    {
        var zones = new ZoneSystem();
        var owner = new PlayerId(0);
        var projection = new ViewProjector(new LayerSystem(), _ => true);
        var state = zones.Create(new GameSetup(
            [new PlayerStart("A", [new StartingCard("card", "private-printing", CardFinish.Etched)], []), new PlayerStart("B", [], [])],
            1, OpeningHandSize: 0), new Store()).State;
        Assert.DoesNotContain("private-printing", JsonSerializer.Serialize(projection.Project(state, owner)));
        state = zones.Draw(state, owner, 1).State;
        Assert.Equal("private-printing", Assert.Single(projection.Project(state, owner).Hand).PrintingId);
        Assert.DoesNotContain("CardInstanceId", JsonSerializer.Serialize(projection.Project(state, owner)));
        Assert.DoesNotContain("private-printing", JsonSerializer.Serialize(projection.Project(state, new PlayerId(1))));
        var id = state.In(ZoneKey.Of(Zone.Hand, owner))[0];
        state = zones.Move(state, id, ZoneKey.Shared(Zone.Battlefield), Placement.Default, false, owner, null, true).State;
        var hidden = Assert.Single(projection.Project(state, owner).Battlefield);
        Assert.Null(hidden.PrintingId);
        Assert.Null(hidden.Finish);
        Assert.Empty(hidden.ArtworkKey);
    }

    [Fact]
    public void Old_startup_logs_are_upgraded_once_without_inventing_printings()
    {
        const string old = """
            [{"event":"GameStarted","players":[{"name":"A","deckOracleIds":["card"],"commandZoneOracleIds":[]}],
              "seed":1,"startingLife":20,"format":"casual","startingPlayer":0}]
            """;
        var restored = Assert.IsType<GameStarted>(Assert.Single(EventLogSerializer.Read(old)));
        Assert.Equal(new StartingCard("card"), Assert.Single(restored.Players[0].DeckCards));
        var written = EventLogSerializer.Write([restored]);
        Assert.DoesNotContain("deckOracleIds", written);
        Assert.Contains("deckCards", written);
        Assert.Null(Assert.Single(restored.Players[0].DeckCards).PrintingId);
    }

    private sealed class Store : IDefinitionStore
    {
        private static readonly CardDefinition Card = new("card", "Card", Layout.Normal,
            [new Face("Card", FaceRole.Main, new TypeLine(CardType.Creature))]);
        public CardDefinition? ByOracleId(string oracleId) => oracleId == "card" ? Card : null;
        public CardDefinition? ByName(string name) => name == "Card" ? Card : null;
    }
}
