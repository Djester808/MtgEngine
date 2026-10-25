using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class AuditMigrationTests
{
    [Fact]
    public async Task BoardMigrationBackfillsExistingCollectionRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260428175437_AddTagsToCollection");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Collections (Id, UserId, Name, CreatedAt, UpdatedAt, IsDeck, Tags)
            VALUES ('11111111-1111-1111-1111-111111111111', 'owner', 'Existing', '2020-01-01', '2020-01-01', 0, '[]');
            INSERT INTO CollectionCards (Id, CollectionId, OracleId, Quantity, QuantityFoil, AddedAt)
            VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'oracle', 3, 2, '2020-01-01');
            """);
        await migrator.MigrateAsync();
        var row = await db.CollectionCards.AsNoTracking().SingleAsync();
        Assert.Equal("main", row.Board);
        Assert.Equal(3, row.Quantity);
        Assert.Equal(2, row.QuantityFoil);
    }
    [Fact]
    public async Task UpgradeAndRollbackPreserveExistingUsersAndAppendedGameHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        var migrations = db.Database.GetMigrations().ToArray();
        var previous = migrations[^2];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        var user = new User { Username = "Alice", Email = "alice@example.test", PasswordHash = "test" };
        var game = new PersistedGame { LogJson = "[1]", EventCount = 1 };
        db.Users.Add(user);
        db.PersistedGames.Add(game);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        Assert.Equal(user.Id, (await db.Users.AsNoTracking().SingleAsync(u => u.Username == "ALICE")).Id);
        db.Set<PersistedGameEvent>().AddRange(
            new PersistedGameEvent { GameId = game.Id, Sequence = 1, Json = "[2,3]" },
            new PersistedGameEvent { GameId = game.Id, Sequence = 3, Json = "[4]" });
        game.EventCount = 4;
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(previous);
        Assert.Equal("[1,2,3,4]", (await db.PersistedGames.AsNoTracking().SingleAsync()).LogJson);
        Assert.Equal(4, (await db.PersistedGames.AsNoTracking().SingleAsync()).EventCount);
        Assert.Single(await db.Users.AsNoTracking().ToArrayAsync());
        await migrator.MigrateAsync();
        Assert.Empty(await db.Set<PersistedGameEvent>().AsNoTracking().ToArrayAsync());
        Assert.Equal("[1,2,3,4]", (await db.PersistedGames.AsNoTracking().SingleAsync()).LogJson);
    }
}
