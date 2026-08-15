using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The live-API fallback's disk cache expires after 24 hours so it cannot pin
/// first-fetch prices forever, and an expired entry whose refresh fails transiently is
/// served stale rather than resolving a real card as "not found". Each test uses a
/// fresh service instance so the in-memory layer starts cold and the disk layer is
/// what's under test.
/// </summary>
public sealed class ScryfallCacheTtlTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtg-ttl-tests", Guid.NewGuid().ToString("N"));

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpResponseMessage> Respond { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Respond());
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.scryfall.test/") };
    }

    private ScryfallService Service(StubHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScryfallCache:Directory"] = _dir,
            })
            .Build();
        return new ScryfallService(new StubFactory(handler), NullLogger<ScryfallService>.Instance, config);
    }

    private static HttpResponseMessage Card(string usd) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$$"""{"oracle_id":"oracle-1","name":"Sol Ring","type_line":"Artifact","prices":{"usd":"{{{usd}}}"}}""",
            Encoding.UTF8, "application/json"),
    };

    private string CachePath => Path.Combine(_dir, "by-oracle", "oracle-1.json");

    private async Task SeedDiskAsync(string usd)
    {
        var handler = new StubHandler { Respond = () => Card(usd) };
        using var seeder = Service(handler);
        Assert.NotNull(await seeder.GetByOracleIdAsync("oracle-1"));
        Assert.True(File.Exists(CachePath));
    }

    [Fact]
    public async Task FreshDiskEntry_IsServedWithoutHittingTheApi()
    {
        await SeedDiskAsync("1.00");

        var handler = new StubHandler();
        using var sut = Service(handler);
        var def = await sut.GetByOracleIdAsync("oracle-1");

        Assert.Equal(0, handler.Calls);
        Assert.Equal(1.00m, def!.Prices.Usd);
    }

    [Fact]
    public async Task ExpiredDiskEntry_IsRefetchedAndRewritten()
    {
        await SeedDiskAsync("1.00");
        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddHours(-25));

        var handler = new StubHandler { Respond = () => Card("2.00") };
        using var sut = Service(handler);
        var def = await sut.GetByOracleIdAsync("oracle-1");

        Assert.Equal(1, handler.Calls);
        Assert.Equal(2.00m, def!.Prices.Usd);
        // The refreshed copy replaces the expired file, restarting its 24h window.
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ExpiredDiskEntry_RefreshFailsTransiently_ServesTheStaleCopyAndRetries()
    {
        await SeedDiskAsync("1.00");
        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddHours(-25));

        var handler = new StubHandler
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var sut = Service(handler);

        var def = await sut.GetByOracleIdAsync("oracle-1");
        Assert.Equal(1.00m, def!.Prices.Usd); // day-old prices beat "card not found"

        // A transient failure must not be cached as fresh — the next call retries.
        await sut.GetByOracleIdAsync("oracle-1");
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task NoDiskEntry_TransientFailure_StaysUncachedNull()
    {
        var handler = new StubHandler
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var sut = Service(handler);

        Assert.Null(await sut.GetByOracleIdAsync("oracle-1"));
        Assert.Null(await sut.GetByOracleIdAsync("oracle-1"));
        Assert.Equal(2, handler.Calls);
        Assert.False(File.Exists(CachePath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
