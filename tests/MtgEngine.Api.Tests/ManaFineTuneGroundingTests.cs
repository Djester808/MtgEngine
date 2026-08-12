using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Covers the grounding pass that turns the model's proposed land names into real,
/// addable cards: hallucinated names are dropped, non-lands are dropped even when they
/// resolve, cards already in the deck are skipped, and survivors carry the newest
/// printing's Scryfall id plus full card data.
/// </summary>
public class ManaFineTuneGroundingTests
{
    private sealed class FakeCardLookup : ICardLookup
    {
        public Dictionary<string, CardDefinition> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PrintingDto[]> Printings { get; } = new();

        public Task<CardDefinition?> GetByNameAsync(string name) =>
            Task.FromResult(ByName.TryGetValue(name, out var def) ? def : null);

        public Task<PrintingDto[]> GetPrintingsAsync(string oracleId) =>
            Task.FromResult(Printings.TryGetValue(oracleId, out var p) ? p : []);

        public Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            throw new NotSupportedException();

        public Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            throw new NotSupportedException();

        public Task<RulingDto[]> GetRulingsAsync(string oracleId) =>
            throw new NotSupportedException();

        public Task<CardDefinition[]> SearchAsync(
            string query, int limit = 20, int offset = 0, string sortBy = "name",
            string sortDir = "asc", bool matchCase = false, bool matchWord = false,
            bool useRegex = false) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingAnthropicClient : IAnthropicClient
    {
        public Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Grounding tests must not reach the model.");
    }

    private sealed class ThrowingCacheService : IAiCacheService
    {
        public Task<T> GetOrCreateAsync<T>(
            string kind, string modelVersion, IEnumerable<string?> keyParts,
            Func<Task<T>> factory, TimeSpan? ttl = null) =>
            throw new NotSupportedException("Grounding tests must not reach the cache.");
    }

    private static ManaFineTuneService.RawLandSuggestion Proposed(string name, string reason = "why") =>
        new() { Name = name, Reason = reason };

    private static CardDefinition Land(string oracleId, string name) =>
        new() { OracleId = oracleId, Name = name, CardTypes = CardType.Land };

    private static CardDefinition Enchantment(string oracleId, string name) =>
        new() { OracleId = oracleId, Name = name, CardTypes = CardType.Enchantment };

    private static PrintingDto Printing(string scryfallId) =>
        new() { ScryfallId = scryfallId, SetCode = "abc", SetName = "Alpha Beta Charlie" };

    private static (ManaFineTuneService Service, FakeCardLookup Cards) MakeService()
    {
        var cards = new FakeCardLookup();
        var service = new ManaFineTuneService(
            new ThrowingAnthropicClient(),
            new ThrowingCacheService(),
            cards,
            NullLogger<ManaFineTuneService>.Instance);
        return (service, cards);
    }

    private static ManaFineTuneRequest Request(params string[] deckCardNames) =>
        new() { DeckCardNames = deckCardNames };

    [Fact]
    public async Task ResolvedLand_IsKept_WithNewestPrintingAndCardData()
    {
        var (service, cards) = MakeService();
        cards.ByName["Command Tower"] = Land("oracle-ct", "Command Tower");
        cards.Printings["oracle-ct"] = [Printing("newest-id"), Printing("older-id")];

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Command Tower", "fixes colors")], Request());

        var s = Assert.Single(result);
        Assert.Equal("Command Tower", s.Name);
        Assert.Equal("fixes colors", s.Reason);
        Assert.Equal("newest-id", s.ScryfallId);
        Assert.NotNull(s.Card);
        Assert.Equal("oracle-ct", s.Card!.OracleId);
    }

    [Fact]
    public async Task UnknownCardName_IsDropped()
    {
        var (service, _) = MakeService();

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Definitely Not A Real Card")], Request());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolvedNonLand_IsDropped()
    {
        var (service, cards) = MakeService();
        cards.ByName["Goblin Bombardment"] = Enchantment("oracle-gb", "Goblin Bombardment");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Goblin Bombardment")], Request());

        Assert.Empty(result);
    }

    [Fact]
    public async Task CardAlreadyInDeck_IsDropped_CaseInsensitively()
    {
        var (service, cards) = MakeService();
        cards.ByName["Command Tower"] = Land("oracle-ct", "Command Tower");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Command Tower")], Request("COMMAND TOWER"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task BlankName_IsDropped()
    {
        var (service, _) = MakeService();

        var result = await service.GroundSuggestionsAsync(
            [Proposed("   ")], Request());

        Assert.Empty(result);
    }

    [Fact]
    public async Task LandWithNoKnownPrintings_IsKept_WithoutScryfallId()
    {
        var (service, cards) = MakeService();
        cards.ByName["Weird Promo Land"] = Land("oracle-wpl", "Weird Promo Land");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Weird Promo Land")], Request());

        var s = Assert.Single(result);
        Assert.Null(s.ScryfallId);
        Assert.NotNull(s.Card);
    }

    [Fact]
    public async Task MixedProposals_KeepOnlyTheGroundedLands_InOrder()
    {
        var (service, cards) = MakeService();
        cards.ByName["Command Tower"] = Land("oracle-ct", "Command Tower");
        cards.ByName["Mountain"] = Land("oracle-mtn", "Mountain");
        cards.ByName["Sol Ring"] = new CardDefinition
        {
            OracleId = "oracle-sr",
            Name = "Sol Ring",
            CardTypes = CardType.Artifact,
        };

        var result = await service.GroundSuggestionsAsync(
            [
                Proposed("Command Tower"),
                Proposed("Sol Ring"),          // resolves, but not a land
                Proposed("Imaginary Land"),    // does not resolve
                Proposed("Mountain"),
            ],
            Request());

        Assert.Equal(["Command Tower", "Mountain"], result.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task SameCardProposedTwice_IsEmittedOnce()
    {
        var (service, cards) = MakeService();
        cards.ByName["Command Tower"] = Land("oracle-ct", "Command Tower");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Command Tower"), Proposed("  command tower  ")], Request());

        Assert.Single(result);
    }

    [Fact]
    public async Task FaceName_ResolvingToADeckCardsCanonicalName_IsDropped()
    {
        var (service, cards) = MakeService();
        // The model cites one face; the deck stores the full MDFC name.
        cards.ByName["Cragcrown Pathway"] =
            Land("oracle-pw", "Cragcrown Pathway // Timbercrown Pathway");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("Cragcrown Pathway")],
            Request("Cragcrown Pathway // Timbercrown Pathway"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task ProposedNameWithStrayWhitespace_StillMatchesDeckEntry()
    {
        var (service, cards) = MakeService();
        cards.ByName["Command Tower"] = Land("oracle-ct", "Command Tower");

        var result = await service.GroundSuggestionsAsync(
            [Proposed("  Command Tower ")], Request("Command Tower"));

        Assert.Empty(result);
    }
}
