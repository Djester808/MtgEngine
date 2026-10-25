using System.Net;
using MtgEngine.Api.Services;
using MtgEngine.Api.Services.Community;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class ExploreServiceTests
{
    private const string Id = "24fc0e72-7851-4f37-a508-1ae2d4b8da71";
    private sealed class Lookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) => Task.FromResult<CardDefinition?>(
            new() { OracleId = oracleId, Name = "Atraxa, Praetors' Voice", CardTypes = CardType.Creature, Supertypes = ["Legendary"], Legalities = new Dictionary<string, string> { ["commander"] = "legal" } });
    }
    private sealed class MemoryCache : IAiCacheService
    {
        private readonly Dictionary<string, object> _items = [];
        public async Task<T> GetOrCreateAsync<T>(string kind, string modelVersion, IEnumerable<string?> keyParts, Func<Task<T>> factory, TimeSpan? ttl = null)
        {
            var key = string.Join("|", keyParts);
            if (_items.TryGetValue(key, out var hit))
                return (T)hit;
            var value = await factory();
            _items[key] = value!;
            return value;
        }
    }
    private sealed class Upstream : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = EdhrecFixtures.Text(EdhrecFixtures.Commander);
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(request.RequestUri.AbsolutePath.Contains("average-decks", StringComparison.Ordinal) && Body == EdhrecFixtures.Text(EdhrecFixtures.Commander) ? EdhrecFixtures.Text(EdhrecFixtures.AverageDeck) : Body) });
        }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://fixture.test/") };
    }
    [Fact]
    public async Task Page_preserves_ratios_unknown_cards_and_original_breakdowns()
    {
        using var upstream = new Upstream();
        var service = new ExploreService(new Lookup(), upstream, new MemoryCache());
        var page = await service.Page(Id, "commander", "", default);
        var entry = page.Sections[0].Cards[0];
        Assert.Equal(43d / 945, entry.Inclusion);
        Assert.Null(entry.Card);
        Assert.Equal("Sanctum Lurker", entry.Name);
        Assert.Contains(page.Charts, c => c.Title == "Mana curve");
        Assert.Contains(page.Variants, v => v.Key == "theme-infect");
        await service.Page(Id, "commander", "", default);
        Assert.Equal(2, upstream.Calls);
    }
    [Fact]
    public async Task Invalid_paths_and_unlisted_themes_never_reach_upstream()
    {
        using var upstream = new Upstream();
        var service = new ExploreService(new Lookup(), upstream, new MemoryCache());
        await Assert.ThrowsAsync<InvalidRequestException>(() => service.Page(Id, "commander", "../other", default));
        Assert.Equal(0, upstream.Calls);
        await Assert.ThrowsAsync<InvalidRequestException>(() => service.Page(Id, "commander", "theme-not-a-real-theme", default));
        Assert.Single(upstream.Paths);
        Assert.DoesNotContain(upstream.Paths, p => p.Contains("not-a-real"));
    }
    [Theory]
    [InlineData(429, "busy")]
    [InlineData(500, "offline")]
    [InlineData(403, "<Code>AccessDenied</Code>")]
    [InlineData(200, "{}")]
    public async Task Transient_and_malformed_responses_are_not_cached_as_missing(int status, string body)
    {
        using var upstream = new Upstream { Status = (HttpStatusCode)status, Body = body };
        var service = new ExploreService(new Lookup(), upstream, new MemoryCache());
        await Assert.ThrowsAsync<CommunityDataException>(() => service.Page(Id, "card", "", default));
        upstream.Status = HttpStatusCode.OK;
        upstream.Body = EdhrecFixtures.Text(EdhrecFixtures.Card);
        Assert.True((await service.Page(Id, "card", "", default)).HasPage);
        Assert.Equal(2, upstream.Calls);
    }
    [Fact]
    public async Task Genuine_missing_pages_have_an_explicit_empty_state()
    {
        using var upstream = new Upstream { Status = HttpStatusCode.NotFound, Body = "missing" };
        var page = await new ExploreService(new Lookup(), upstream, new MemoryCache()).Page(Id, "card", "", default);
        Assert.False(page.HasPage);
        Assert.Empty(page.Sections);
        Assert.Null(page.DeckCount);
    }
}
