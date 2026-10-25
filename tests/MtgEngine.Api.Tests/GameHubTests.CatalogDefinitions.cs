using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task Selected_printings_and_finishes_survive_session_start_and_restore()
    {
        var logs = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var selections = DeckIds().Select((id, index) =>
            new StartingCard(id, $"printing-{index}", (CardFinish)(index % 3))).ToArray();
        var games = new GameSessionService(logs, pool, new DeckGate(pool));
        var started = await games.CreateAsync(new NewGameRequest(
            [new NewGameSeat(Alice, "Alice", selections, []), new NewGameSeat(Bob, "Bob", selections, [])],
            1, false, "casual", 20));

        var restored = await new GameSessionService(logs, pool, new DeckGate(pool)).FindAsync(started.Id);
        Assert.NotNull(restored);
        foreach (var session in new[] { started, restored })
        {
            Assert.Equal(24, session.State.Objects.Count);
            foreach (var owner in new[] { new PlayerId(0), new PlayerId(1) })
            {
                var cards = session.State.Objects.Values.Where(card => card.Owner == owner).ToArray();
                Assert.Equal(12, cards.Length);
                foreach (var selection in selections)
                {
                    var card = Assert.Single(cards, card => card.Definition.OracleId == selection.OracleId);
                    Assert.Equal(selection, card.DeckSelection);
                }
            }
        }
    }

    [Fact]
    public async Task Catalog_only_manual_cards_start_and_restore_with_their_printed_data_and_no_enforcement()
    {
        var logs = new MemoryLogStore();
        var pool = new FakePool();
        var catalog = new PrintedCatalog();
        var games = new GameSessionService(logs, pool, new DeckGate(pool), definitions: new GameDefinitionResolver(pool, catalog));
        var request = new NewGameRequest(
            [new NewGameSeat(Alice, "Alice", [.. DeckIds()], []), new NewGameSeat(Bob, "Bob", [.. DeckIds()], [])],
            1, true, "casual", 20);
        var started = await games.CreateAsync(request);
        Assert.Equal(12, catalog.Reads);
        Assert.Empty(started.EnforcedOracleIds);
        Assert.Equal(24, started.State.Objects.Count);
        Assert.All(started.State.Objects.Values, card =>
        {
            Assert.Equal(new PrintedStat("*"), card.Face.Power);
            Assert.Equal("Catalog rules text.", card.Face.OracleText);
            Assert.False(GameSessionService.IsEnforced(started, card.Id));
        });
        Assert.Equal(7, started.State.In(ZoneKey.Of(Zone.Hand, new PlayerId(0))).Count);
        await Assert.ThrowsAsync<InvalidRequestException>(() => games.CreateAsync(request with { AllowHonourSystem = false }));
        Assert.Equal(12, catalog.Reads);

        // Restore from persisted events without the catalog: the table's definition snapshot
        // must not depend on either a generated file or a fresh lookup after restarting.
        pool.With(DeckIds()[0], "Newly compiled", ImplementationStatus.Verified);
        var restored = await new GameSessionService(logs, pool, new DeckGate(pool)).FindAsync(started.Id);
        Assert.NotNull(restored);
        Assert.Empty(restored.EnforcedOracleIds);
        Assert.Equal(24, restored.State.Objects.Count);
        Assert.All(restored.State.Objects.Values, card => Assert.Equal("Catalog rules text.", card.Face.OracleText));
        Assert.Equal(12, catalog.Reads);

    }

    private sealed class PrintedCatalog : IPrintedCardLookup
    {
        public int Reads { get; private set; }
        public Task<CardDefinition?> FindPrintedAsync(string oracleId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<CardDefinition?>(new CardDefinition(oracleId, "Catalog " + oracleId, Layout.Normal,
                [new Face("Catalog face", FaceRole.Main, new TypeLine(CardType.Creature), Power: new PrintedStat("*"),
                    Toughness: new PrintedStat("2"), OracleText: "Catalog rules text.")]));
        }
    }
}
