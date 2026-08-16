using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Every DateTime must come back out of the database marked UTC.
/// </summary>
/// <remarks>
/// SQLite has no date type, so values round-trip through TEXT and materialize as
/// <see cref="DateTimeKind.Unspecified"/>. Serialized that way they reach the client with no
/// trailing <c>Z</c>, and JavaScript reads a bare date-time as *local* — which put every
/// timestamp hours into the future for anyone west of UTC. These pin the convention that
/// fixes it, across entities, so a new one cannot quietly opt out.
/// </remarks>
public sealed class UtcDateTimeConventionTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;

    public UtcDateTimeConventionTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();
    }

    /// <summary>Reads through a fresh context so the values come from SQLite, not the change tracker.</summary>
    private MtgEngineDbContext FreshRead()
    {
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        return new MtgEngineDbContext(options);
    }

    [Fact]
    public async Task Collection_timestamps_come_back_utc()
    {
        var c = new Collection("user-1", "Staples");
        _db.Collections.Add(c);
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.Collections.AsNoTracking().FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
    }

    [Fact]
    public async Task CollectionCard_AddedAt_comes_back_utc()
    {
        var c = new Collection("user-1", "Staples");
        _db.Collections.Add(c);
        _db.CollectionCards.Add(new CollectionCard(c.Id, "oracle-1", "scry-1", 1, 0, null, "main"));
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.CollectionCards.AsNoTracking().FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loaded.AddedAt.Kind);
    }

    [Fact]
    public async Task CollectionCardEvent_CreatedAt_comes_back_utc()
    {
        _db.CollectionCardEvents.Add(new CollectionCardEvent
        {
            UserId = "user-1",
            CollectionId = Guid.NewGuid(),
            CollectionName = "Staples",
            OracleId = "oracle-1",
        });
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.CollectionCardEvents.AsNoTracking().FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
    }

    [Fact]
    public async Task ForumPost_timestamps_come_back_utc()
    {
        _db.ForumPosts.Add(new ForumPost
        {
            DeckId = Guid.NewGuid(),
            AuthorId = "user-1",
            AuthorUsername = "someone",
        });
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.ForumPosts.AsNoTracking().FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loaded.PublishedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
    }

    [Fact]
    public async Task A_local_value_is_normalized_on_write_not_relabelled_on_read()
    {
        // Storing local wall-clock and then calling it UTC would bake in the very error the
        // convention exists to remove, so the instant must survive the round trip.
        var localNoon = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Local);
        _db.CardPriceSnapshots.Add(new CardPriceSnapshot
        {
            ScryfallId = "scry-1",
            CapturedAt = localNoon,
        });
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.CardPriceSnapshots.AsNoTracking().FirstAsync();
        Assert.Equal(DateTimeKind.Utc, loaded.CapturedAt.Kind);
        Assert.Equal(localNoon.ToUniversalTime(), loaded.CapturedAt);
    }

    [Fact]
    public async Task A_utc_value_survives_the_round_trip_unchanged()
    {
        var stamp = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        _db.CardPriceSnapshots.Add(new CardPriceSnapshot { ScryfallId = "scry-2", CapturedAt = stamp });
        await _db.SaveChangesAsync();

        using var read = FreshRead();
        var loaded = await read.CardPriceSnapshots.AsNoTracking().FirstAsync();
        Assert.Equal(stamp, loaded.CapturedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.CapturedAt.Kind);
    }

    [Fact]
    public void Serialized_timestamps_carry_a_Z()
    {
        // The whole point: what reaches the browser must be unambiguous.
        var utc = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var json = System.Text.Json.JsonSerializer.Serialize(utc).Trim('"');
        Assert.EndsWith("Z", json);

        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        Assert.DoesNotContain("Z", System.Text.Json.JsonSerializer.Serialize(unspecified));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
