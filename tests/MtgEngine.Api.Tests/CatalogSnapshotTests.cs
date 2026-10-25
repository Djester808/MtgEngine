using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

public sealed class CatalogSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "catalog-audit-" + Guid.NewGuid());
    private readonly Handler _handler = new();
    private readonly ScryfallService _api;
    private readonly BulkDataService _catalog;
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://catalog.invalid/") };
    }
    public CatalogSnapshotTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BulkData:Directory"] = _dir,
            ["ScryfallCache:Directory"] = Path.Combine(_dir, "cache"),
        }).Build();
        var factory = new Factory(_handler);
        _api = new(factory, NullLogger<ScryfallService>.Instance, config);
        _catalog = new(_api, factory, NullLogger<BulkDataService>.Instance, config);
    }
    private async Task LoadAsync(string prefix)
    {
        var cards = $$"""
            {"oracle_id":"one","name":"{{prefix}} A","type_line":"Instant","cmc":5,"mana_cost":"{5}"}
            {"oracle_id":"two","name":"{{prefix}} Z","type_line":"Instant","cmc":1,"mana_cost":"{1}"}
            """;
        await File.WriteAllTextAsync(Path.Combine(_dir, "oracle_cards.json"), cards);
        await _catalog.RebuildIndexesAsync();
    }
    [Fact]
    public async Task NameSearchHonorsManaOrderingAndNegativeFiltersNeverInvokeFuzzyLookup()
    {
        await LoadAsync("Lightning");
        var sorted = await _catalog.SearchAsync("lightning", sortBy: "cmc");
        Assert.Equal(["Lightning Z", "Lightning A"], sorted.Select(c => c.Name));
        Assert.Empty(await _catalog.SearchAsync("name:\"Lightning A\" t:creature"));
        Assert.Equal(0, _handler.Calls);
    }
    [Fact]
    public async Task SearchesSeeOneCatalogGenerationDuringRefresh()
    {
        await LoadAsync("Old");
        var reads = Task.Run(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var result = await _catalog.SearchAsync("name:\" \"");
                Assert.Equal(2, result.Length);
                Assert.True(result.All(c => c.Name.StartsWith("Old", StringComparison.Ordinal))
                    || result.All(c => c.Name.StartsWith("New", StringComparison.Ordinal)));
            }
        });
        await LoadAsync("New");
        await reads;
        Assert.Equal(2, _catalog.CatalogVersion);
        Assert.All(await _catalog.SearchAsync("new"), c => Assert.StartsWith("New", c.Name));
    }
    public void Dispose()
    {
        _catalog.Dispose();
        _api.Dispose();
        _handler.Dispose();
        Directory.Delete(_dir, true);
    }
}
