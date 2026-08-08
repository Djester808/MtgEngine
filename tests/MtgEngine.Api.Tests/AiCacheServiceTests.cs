using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Covers the caching contract the AI services rely on: identical inputs must not
/// re-hit the API, and a model/prompt revision must invalidate everything stale.
/// </summary>
public sealed class AiCacheServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly AiCacheService _sut;

    public AiCacheServiceTests()
    {
        // Real SQLite (in-memory) rather than the InMemory provider, so unique
        // indexes and column constraints behave as they will in production.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<MtgEngineDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new AiCacheService(_db, NullLogger<AiCacheService>.Instance);
    }

    private sealed record Payload(string Value, int Number);

    [Fact]
    public async Task Miss_InvokesFactory_AndReturnsItsResult()
    {
        int calls = 0;

        var result = await _sut.GetOrCreateAsync("kind", "v1", ["a", "b"], () =>
        {
            calls++;
            return Task.FromResult(new Payload("fresh", 1));
        });

        Assert.Equal(1, calls);
        Assert.Equal(new Payload("fresh", 1), result);
    }

    [Fact]
    public async Task Hit_DoesNotInvokeFactory_AndReturnsCachedValue()
    {
        int calls = 0;
        Task<Payload> Factory()
        {
            calls++;
            return Task.FromResult(new Payload($"call-{calls}", calls));
        }

        var first = await _sut.GetOrCreateAsync("kind", "v1", ["a", "b"], Factory);
        var second = await _sut.GetOrCreateAsync("kind", "v1", ["a", "b"], Factory);

        Assert.Equal(1, calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DifferentKeyParts_Miss()
    {
        int calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(new Payload("x", calls)); }

        await _sut.GetOrCreateAsync("kind", "v1", ["a"], Factory);
        await _sut.GetOrCreateAsync("kind", "v1", ["b"], Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DifferentModelVersion_Miss()
    {
        int calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(new Payload("x", calls)); }

        await _sut.GetOrCreateAsync("kind", "v1", ["a"], Factory);
        await _sut.GetOrCreateAsync("kind", "v2", ["a"], Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DifferentKind_Miss()
    {
        int calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(new Payload("x", calls)); }

        await _sut.GetOrCreateAsync("suggestions", "v1", ["a"], Factory);
        await _sut.GetOrCreateAsync("mana-tune", "v1", ["a"], Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeyParts_AreDelimited_SoAdjacentPartsCannotCollide()
    {
        // ["ab","c"] and ["a","bc"] concatenate identically; a naive key would collide.
        int calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(new Payload("x", calls)); }

        await _sut.GetOrCreateAsync("kind", "v1", ["ab", "c"], Factory);
        await _sut.GetOrCreateAsync("kind", "v1", ["a", "bc"], Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExpiredEntry_IsRefreshed()
    {
        int calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(new Payload($"call-{calls}", calls)); }

        await _sut.GetOrCreateAsync("kind", "v1", ["a"], Factory);

        // Age the stored entry past the requested TTL.
        var row = await _db.AiResponseCache.SingleAsync();
        row.CreatedAt = DateTime.UtcNow.AddHours(-2);
        await _db.SaveChangesAsync();

        var refreshed = await _sut.GetOrCreateAsync(
            "kind", "v1", ["a"], Factory, ttl: TimeSpan.FromHours(1));

        Assert.Equal(2, calls);
        Assert.Equal("call-2", refreshed.Value);
    }

    [Fact]
    public async Task RefreshingExpiredEntry_UpdatesInPlace_WithoutViolatingUniqueIndex()
    {
        Task<Payload> Factory() => Task.FromResult(new Payload("x", 1));

        await _sut.GetOrCreateAsync("kind", "v1", ["a"], Factory);

        var row = await _db.AiResponseCache.SingleAsync();
        row.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await _db.SaveChangesAsync();

        await _sut.GetOrCreateAsync("kind", "v1", ["a"], Factory);

        Assert.Equal(1, await _db.AiResponseCache.CountAsync());
    }

    [Fact]
    public async Task FactoryException_Propagates_AndNothingIsCached()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _sut.GetOrCreateAsync<Payload>("kind", "v1", ["a"],
                () => throw new HttpRequestException("upstream 500")));

        Assert.Equal(0, await _db.AiResponseCache.CountAsync());
    }

    [Fact]
    public async Task UnreadablePayload_IsTreatedAsMiss_AndOverwritten()
    {
        await _sut.GetOrCreateAsync("kind", "v1", ["a"],
            () => Task.FromResult(new Payload("good", 1)));

        var row = await _db.AiResponseCache.SingleAsync();
        row.PayloadJson = "{ this is not valid json";
        await _db.SaveChangesAsync();

        var result = await _sut.GetOrCreateAsync("kind", "v1", ["a"],
            () => Task.FromResult(new Payload("recovered", 2)));

        Assert.Equal("recovered", result.Value);
        Assert.Equal(1, await _db.AiResponseCache.CountAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
