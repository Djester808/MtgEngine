using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "FoundationIntegration")]
public sealed class FoundationIntegrationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_selected_card_keeps_one_identity_across_zone_contracts_and_viewer_reload(int seats)
    {
        var owner = new PlayerId(0);
        var other = new PlayerId(1);
        var selection = new StartingCard("fixture", "selected-printing", CardFinish.Etched);
        var players = Enumerable.Range(0, seats).Select(i => new PlayerStart($"Seat {i}",
            i == 0 ? [selection] : [], i == 0 ? [new StartingCard("fixture", "commander-printing")] : [])).ToImmutableList();
        var zones = new ZoneSystem();
        var created = zones.Create(new GameSetup(players, 7, OpeningHandSize: 0, StartingPlayer: 1), new Store());
        var state = created.State;
        var log = created.Events;
        var original = state.Objects[Assert.Single(state.In(state.Player(owner).OwnedZone(Zone.Library)))];
        var commander = Assert.Single(new CommandZone(state).Members);
        Assert.True(state.Objects[commander].IsCommander);
        Assert.Equal(other, new TurnSnapshot(state).ActivePlayer);
        Assert.False(new PrioritySnapshot(state).CanPass(owner));

        Fold(new LibraryInspectionChanged("integration", new LibraryInspection(owner, owner, [original.Id])));
        Fold(new CardsLookedAt([owner], [new RevealedCard("fixture", "Fixture", null, "Descriptive text.", selection.PrintingId)]));
        Assert.Contains(Subsystems.Wave2().Views.Project(state, owner).InspectedLibraryCards, card => card.Id == original.Id);
        Assert.Empty(Subsystems.Wave2().Views.Project(state, other).InspectedLibraryCards);
        Apply(LibraryDraw.Apply(state, owner));
        var current = state.Objects[Assert.Single(new Hand(state, owner).Members)];
        Assert.DoesNotContain(Subsystems.Wave2().Views.Project(state, owner).InspectedLibraryCards, card => card.Id == original.Id);
        CheckViews(publicCard: false);

        Apply(zones.Move(state, current.Id, ZoneKey.Shared(Zone.Battlefield), Placement.Default, false, other, 0, true));
        current = Assert.Single(state.Objects.Values, card => card.CardInstanceId == original.CardInstanceId);
        Assert.Same(current, PermanentAccess.Require(state, current.Id));
        Assert.Equal(current.Id, Assert.Single(new Battlefield(state).ControlledBy(other, new LayerSystem())));
        CheckViews(publicCard: false);

        Apply(zones.Move(state, current.Id, ZoneKey.Of(Zone.Graveyard, other)));
        current = Assert.IsType<GameObject>(new Graveyard(state, owner).Top);
        Assert.Equal(0, new Graveyard(state, other).Count);
        Assert.False(current.FaceDown);
        CheckViews(publicCard: true);

        Apply(zones.Move(state, current.Id, StackZone.Key));
        current = Assert.IsType<GameObject>(StackZone.Capture(state).Top);
        Assert.Equal(0, new Graveyard(state, owner).Count);
        CheckViews(publicCard: true);

        Apply(zones.Move(state, current.Id, ZoneKey.Shared(Zone.Exile), Placement.Default, false, null, null, true));
        current = Assert.IsType<GameObject>(new ExileZone(state).Get(Assert.Single(new ExileZone(state).Members)));
        Assert.True(StackZone.IsEmpty(state));
        CheckViews(publicCard: false);

        Apply(zones.Move(state, current.Id, ZoneKey.Shared(Zone.Command)));
        current = Assert.Single(state.Objects.Values, card => card.CardInstanceId == original.CardInstanceId);
        Assert.True(new CommandZone(state).Contains(current.Id));
        Assert.False(current.IsCommander); // Being in the command zone does not make a card a commander.
        Assert.True(state.Objects[commander].IsCommander);
        CheckViews(publicCard: true);

        void Apply(Outcome outcome)
        {
            log = log.AddRange(outcome.Events);
            state = outcome.State;
            var card = Assert.Single(state.Objects.Values, item => item.CardInstanceId == original.CardInstanceId);
            Assert.Equal(selection, card.DeckSelection);
            Assert.Equal(owner, card.Owner);
            var membership = state.Zones.SelectMany(zone => zone.Value.Select(id => (zone.Key, Id: id))).ToArray();
            Assert.Equal(state.Objects.Count, membership.Length);
            Assert.Equal(membership.Length, membership.Select(item => item.Id).Distinct().Count());
            Assert.All(membership, item => Assert.Equal(item.Key, state.Objects[item.Id].Zone));
        }

        void Fold(GameEvent change) => Apply(new Outcome(change.Apply(state), [change]));

        void CheckViews(bool publicCard)
        {
            var restored = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(log)));
            var projector = new ViewProjector(new LayerSystem(), _ => true);
            foreach (var player in state.Players)
            {
                var view = projector.Project(state, player.Id);
                var json = JsonSerializer.Serialize(view);
                Assert.Equal(json, JsonSerializer.Serialize(projector.Project(restored, player.Id)));
                Assert.Equal(player.Id, view.Table!.Self.Player);
                Assert.DoesNotContain("cardInstanceId", json, StringComparison.OrdinalIgnoreCase);
                if (publicCard || player.Id == owner)
                    Assert.Contains("selected-printing", json);
                else
                    Assert.DoesNotContain("selected-printing", json);
            }
        }
    }

    private sealed class Store : IDefinitionStore
    {
        private static readonly CardDefinition Definition = new("fixture", "Fixture", Layout.Normal,
            [new Face("Fixture", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Descriptive text.")]);
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
