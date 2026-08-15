using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The snapshot worker records one price row per owned printing per day (idempotently),
/// and the history service reads them back windowed. Real in-memory SQLite so the
/// (ScryfallId, CapturedAt) unique index behaves as in production.
/// </summary>
public sealed class PriceSnapshotTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly ServiceProvider _provider;

    public PriceSnapshotTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();

        // The worker resolves a fresh scoped DbContext per pass; hand it a container
        // whose scoped context shares this test's connection.
        var services = new ServiceCollection();
        services.AddDbContext<MtgEngineDbContext>(o => o.UseSqlite(_conn));
        _provider = services.BuildServiceProvider();
    }

    private sealed class Lookup : StubScryfallService
    {
        public Dictionary<string, CardPrices> PricesByScryfallId { get; } = new();
        public Dictionary<string, string> DefaultPrintingByOracleId { get; } = new();

        public override Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult<CardDefinition?>(new CardDefinition
            {
                OracleId = "oracle-x",
                Name = "Card",
                Prices = PricesByScryfallId.TryGetValue(scryfallId, out var p) ? p : CardPrices.None,
            });

        public override Task<PrintingDto[]> GetPrintingsAsync(string oracleId) =>
            Task.FromResult(DefaultPrintingByOracleId.TryGetValue(oracleId, out var id)
                ? new[] { new PrintingDto { ScryfallId = id } }
                : Array.Empty<PrintingDto>());
    }

    private PriceSnapshotWorker Worker(Lookup lookup) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        lookup,
        NullLogger<PriceSnapshotWorker>.Instance);

    private async Task SeedCardAsync(string? scryfallId, string oracleId = "oracle-1")
    {
        var collection = new Collection("user-1", "Binder");
        _db.Collections.Add(collection);
        _db.CollectionCards.Add(new CollectionCard(collection.Id, oracleId, scryfallId));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Snapshot_RecordsPinnedPrintingOncePerDay()
    {
        await SeedCardAsync("scry-1");
        var lookup = new Lookup();
        lookup.PricesByScryfallId["scry-1"] = new CardPrices { Usd = 1.55m };
        var worker = Worker(lookup);

        var first = await worker.SnapshotAsync(CancellationToken.None);
        var second = await worker.SnapshotAsync(CancellationToken.None); // same day → no-op

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var row = Assert.Single(_db.CardPriceSnapshots.AsNoTracking());
        Assert.Equal("scry-1", row.ScryfallId);
        Assert.Equal(1.55m, row.Usd);
        Assert.Equal(DateTime.UtcNow.Date, row.CapturedAt);
    }

    [Fact]
    public async Task Snapshot_ResolvesUnpinnedRowsToTheDefaultPrinting()
    {
        await SeedCardAsync(scryfallId: null, oracleId: "oracle-2");
        var lookup = new Lookup();
        lookup.DefaultPrintingByOracleId["oracle-2"] = "scry-default";
        lookup.PricesByScryfallId["scry-default"] = new CardPrices { Usd = 3m };

        var added = await Worker(lookup).SnapshotAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal("scry-default", _db.CardPriceSnapshots.AsNoTracking().Single().ScryfallId);
    }

    [Fact]
    public async Task Snapshot_SkipsPrintingsWithNoPriceData()
    {
        await SeedCardAsync("scry-unpriced");
        var lookup = new Lookup(); // no prices registered → CardPrices.None

        var added = await Worker(lookup).SnapshotAsync(CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Empty(_db.CardPriceSnapshots.AsNoTracking());
    }

    [Fact]
    public async Task History_ReturnsWindowedPointsOldestFirst()
    {
        var today = DateTime.UtcNow.Date;
        for (var daysAgo = 0; daysAgo < 10; daysAgo++)
        {
            _db.CardPriceSnapshots.Add(new CardPriceSnapshot
            {
                ScryfallId = "scry-1",
                CapturedAt = today.AddDays(-daysAgo),
                Usd = 1m + daysAgo,
            });
        }
        _db.CardPriceSnapshots.Add(new CardPriceSnapshot { ScryfallId = "scry-other", CapturedAt = today, Usd = 99m });
        await _db.SaveChangesAsync();

        var sut = new PriceHistoryService(_db);
        var points = await sut.GetHistoryAsync("scry-1", days: 5, CancellationToken.None);

        Assert.Equal(6, points.Length); // today plus five days back, other printings excluded
        Assert.True(points.First().Date < points.Last().Date);
        Assert.Equal(1m, points.Last().Usd);
    }

    [Fact]
    public async Task History_UnknownPrinting_IsEmptyNotAnError()
    {
        var sut = new PriceHistoryService(_db);
        Assert.Empty(await sut.GetHistoryAsync("scry-never-seen", days: 90, CancellationToken.None));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _conn.Dispose();
    }
}
