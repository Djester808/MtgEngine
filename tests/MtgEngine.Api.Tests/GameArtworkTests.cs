using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using Step = MtgEngine.Cards.Step;

namespace MtgEngine.Api.Tests;

public sealed class GameArtworkTests
{
    [Fact]
    public async Task Historical_public_reveals_resolve_the_selected_printing_without_a_live_object()
    {
        var lookup = new Lookup();
        var view = View() with
        {
            LastReveal = new CardReveal(new PlayerId(0), [new RevealedCard("cached", "Card", null, "Text", "first")]),
        };
        var result = await new GameArtworkService(lookup).ForAsync(view, CancellationToken.None);
        Assert.Equal("https://example.test/first.jpg", Assert.Single(result).Value.ImageUri);
        Assert.Equal(["first"], lookup.Printings);
        Assert.Empty(lookup.Requested);
    }

    private sealed class Lookup : ICardLookup
    {
        public List<string> Requested { get; } = [];
        public List<string> Printings { get; } = [];

        public Task<CardDefinition?> GetLocalByScryfallIdAsync(string scryfallId)
        {
            Printings.Add(scryfallId);
            return Task.FromResult<CardDefinition?>(scryfallId == "missing" ? null : new CardDefinition
            {
                OracleId = scryfallId == "wrong-card" ? "other" : "cached",
                ImageUriNormal = "https://example.test/" + scryfallId + ".jpg",
            });
        }

        public Task<CardDefinition?> GetLocalByOracleIdAsync(string oracleId)
        {
            Requested.Add(oracleId);
            return Task.FromResult<CardDefinition?>(oracleId == "cached"
                ? new CardDefinition { OracleId = oracleId, ImageUriNormal = "https://example.test/card.jpg" }
                : null);
        }

        public Task<CardDefinition?> GetByOracleIdAsync(string oracleId) => throw new NotSupportedException();
        public Task<CardDefinition?> GetByNameAsync(string name) => throw new NotSupportedException();
        public Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) => throw new NotSupportedException();
        public Task<PrintingDto[]> GetPrintingsAsync(string oracleId) => throw new NotSupportedException();
        public Task<RulingDto[]> GetRulingsAsync(string oracleId) => throw new NotSupportedException();
        public Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0,
            string sortBy = "name", string sortDir = "asc", bool matchCase = false,
            bool matchWord = false, bool useRegex = false) => throw new NotSupportedException();
    }

    private static ObjectView Card(int id, string oracleId, bool faceDown = false) =>
        new(new ObjectId(id), "Card", oracleId, new PlayerId(0), new PlayerId(0), false,
            faceDown, null, null, null, 0, [], null, null, [], [], [], true);

    private static GameView View(params ObjectView[] cards) =>
        new(new PlayerId(0), 1, Step.PrecombatMain, new PlayerId(0), new PlayerId(0),
            [], [.. cards], [], [], [], null, null);

    [Fact]
    public async Task Looks_up_only_unique_visible_definitions_and_never_fetches_a_cache_miss()
    {
        var lookup = new Lookup();
        var artwork = await new GameArtworkService(lookup).ForAsync(
            View(Card(1, "cached"), Card(2, "cached"), Card(3, "missing"),
                Card(4, "hidden", true), Card(5, "")), CancellationToken.None);

        Assert.Equal(new[] { "cached", "missing" }, lookup.Requested);
        var entry = Assert.Single(artwork);
        Assert.Equal("cached", entry.Key);
        Assert.Equal("https://example.test/card.jpg", entry.Value.ImageUri);
    }

    [Fact]
    public async Task An_absent_view_does_not_request_artwork()
    {
        var lookup = new Lookup();
        Assert.Empty(await new GameArtworkService(lookup).ForAsync(null, CancellationToken.None));
        Assert.Empty(lookup.Requested);
    }

    [Fact]
    public async Task Distinct_printings_of_the_same_card_keep_their_own_art_and_hidden_printings_are_not_looked_up()
    {
        var lookup = new Lookup();
        var first = Card(1, "cached") with { PrintingId = "first" };
        var second = Card(2, "cached") with { PrintingId = "second" };
        var artwork = await new GameArtworkService(lookup).ForAsync(View(first, second, first,
            Card(3, "cached", true) with { PrintingId = "hidden" }), CancellationToken.None);
        Assert.Equal(new[] { "first", "second" }, lookup.Printings);
        Assert.Empty(lookup.Requested);
        Assert.Equal("https://example.test/first.jpg", artwork[first.ArtworkKey].ImageUri);
        Assert.Equal("https://example.test/second.jpg", artwork[second.ArtworkKey].ImageUri);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-card")]
    public async Task Unavailable_or_mismatched_printings_use_the_existing_oracle_fallback(string printing)
    {
        var lookup = new Lookup();
        var card = Card(1, "cached") with { PrintingId = printing };
        var artwork = await new GameArtworkService(lookup).ForAsync(View(card), CancellationToken.None);
        Assert.Equal("https://example.test/card.jpg", artwork[card.ArtworkKey].ImageUri);
        Assert.Single(lookup.Printings);
        Assert.Single(lookup.Requested);
    }

    [Fact]
    public async Task Authorized_choice_cards_get_artwork_without_requesting_face_down_identities()
    {
        var lookup = new Lookup();
        var view = View() with { ChoiceCards = [Card(1, "cached"), Card(2, "hidden", true)] };
        var artwork = await new GameArtworkService(lookup).ForAsync(view, CancellationToken.None);
        Assert.Equal("cached", Assert.Single(lookup.Requested));
        Assert.Single(artwork);
    }

    [Fact]
    public async Task Active_inspection_art_is_scoped_to_visible_cards_and_expires_with_the_view()
    {
        var lookup = new Lookup();
        var selected = Card(1, "cached") with { PrintingId = "first" };
        var view = View() with
        {
            InspectedLibraryCards = [selected, selected, Card(2, "hidden", true)],
        };
        var service = new GameArtworkService(lookup);
        var artwork = await service.ForAsync(view, CancellationToken.None);
        Assert.Equal("https://example.test/first.jpg", Assert.Single(artwork).Value.ImageUri);
        Assert.Equal(["first"], lookup.Printings);
        Assert.Empty(lookup.Requested);

        lookup.Printings.Clear();
        Assert.Empty(await service.ForAsync(view with { InspectedLibraryCards = [] }, CancellationToken.None));
        Assert.Empty(lookup.Printings);
        Assert.Empty(lookup.Requested);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_local_lookup()
    {
        var lookup = new Lookup();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GameArtworkService(lookup).ForAsync(View(Card(1, "cached")), cancellation.Token));
        Assert.Empty(lookup.Requested);
    }
}
