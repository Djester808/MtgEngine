using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

public sealed class GameDefinitionResolverTests
{
    [Fact]
    public async Task Allowed_manual_decks_reuse_catalog_data_without_requiring_generated_card_files()
    {
        var catalog = new Catalog();
        var definitions = await new GameDefinitionResolver(new Pool(), catalog).ResolveAsync(["known", "known"], true, CancellationToken.None);
        Assert.Same(catalog.Card, definitions.ByOracleId("known"));
        Assert.Same(catalog.Card, definitions.ByName("Known"));
        Assert.Equal(1, catalog.Reads);
        Assert.Empty(definitions.ByOracleId("known")!.Faces[0].AbilityList);
    }

    [Fact]
    public async Task Compiled_definitions_win_and_strict_tables_never_use_printed_only_data()
    {
        var catalog = new Catalog();
        var pool = new Pool { Card = catalog.Card with { Name = "Compiled" } };
        var resolved = await new GameDefinitionResolver(pool, catalog).ResolveAsync(["known"], true, CancellationToken.None);
        Assert.Same(pool.Card, resolved.ByOracleId("known"));
        Assert.Equal(0, catalog.Reads);
        await Assert.ThrowsAsync<InvalidRequestException>(() => new GameDefinitionResolver(new Pool(), catalog)
            .ResolveAsync(["known"], false, CancellationToken.None));
        Assert.Equal(0, catalog.Reads);
    }

    private sealed class Catalog : IPrintedCardLookup
    {
        public int Reads { get; private set; }
        public CardDefinition Card { get; } = new("known", "Known", Layout.Normal,
            [new Face("Known", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Untranslated rules text.")]);
        public Task<CardDefinition?> FindPrintedAsync(string oracleId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<CardDefinition?>(oracleId == Card.OracleId ? Card : null);
        }
    }

    private sealed class Pool : ICardFilePool
    {
        public CardDefinition? Card { get; init; }
        public int Count => Card is null ? 0 : 1;
        public CardDefinition? ByOracleId(string oracleId) => oracleId == Card?.OracleId ? Card : null;
        public CardDefinition? ByName(string name) => name == Card?.Name ? Card : null;
        public CardStatus StatusOf(string oracleId) => CardStatus.Unknown(oracleId);
    }
}
