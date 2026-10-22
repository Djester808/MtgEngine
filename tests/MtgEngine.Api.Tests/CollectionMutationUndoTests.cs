using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Tests;

public sealed class CollectionMutationUndoTests
{
    [Fact]
    public async Task UndoRestoresBothTransferEndsAndRejectsOverwritingLaterEdits()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var source = new Collection("owner", "Source");
        var target = new Collection("owner", "Target");
        var row = new CollectionCard(source.Id, "oracle", "printing", 3, 1) { Notes = "Keep notes", Language = "ja", Condition = "LP" };
        db.Collections.AddRange(source, target);
        db.CollectionCards.Add(row);
        await db.SaveChangesAsync();
        var service = new CollectionMutationUndoService(db, TimeProvider.System, new CardHistoryService(db));
        var moved = await service.RunAsync("owner", [source.Id, target.Id], ["oracle"], async () => { row.Quantity = 2; db.CollectionCards.Add(new CollectionCard(target.Id, "oracle", "printing", 1)); await db.SaveChangesAsync(); return true; }, false, default);
        Assert.True(await service.TryUndoAsync(moved.Token, "owner", default));
        db.ChangeTracker.Clear();
        var restored = Assert.Single(await db.CollectionCards.ToArrayAsync());
        Assert.Equal(3, restored.Quantity);
        Assert.Equal("Keep notes", restored.Notes);
        Assert.Equal("ja", restored.Language);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => service.TryUndoAsync(moved.Token, "owner", default));
        var changed = await service.RunAsync("owner", [source.Id], ["oracle"], async () => { db.CollectionCards.Remove(restored); await db.SaveChangesAsync(); return true; }, false, default);
        db.CollectionCards.Add(new CollectionCard(source.Id, "oracle", "printing", 9));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => service.TryUndoAsync(changed.Token, "owner", default));
        Assert.False(await service.TryUndoAsync(changed.Token, "stranger", default));
    }
}
