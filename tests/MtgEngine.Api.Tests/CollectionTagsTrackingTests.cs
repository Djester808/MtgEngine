using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class CollectionTagsTrackingTests
{
    [Fact]
    public async Task In_place_tag_changes_are_persisted_after_loading_from_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options;
        await using (var seed = new MtgEngineDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Collections.Add(new Collection("owner", "Tagged") { Tags = ["original", "remove"] });
            await seed.SaveChangesAsync();
        }
        await using (var edit = new MtgEngineDbContext(options))
        {
            var collection = await edit.Collections.SingleAsync();
            collection.Tags[0] = "renamed";
            collection.Tags.Remove("remove");
            collection.Tags.Add("added");
            Assert.Equal(1, await edit.SaveChangesAsync());
            // The saved snapshot must be a copy too, so subsequent edits are detected.
            collection.Tags.Add("later");
            Assert.Equal(1, await edit.SaveChangesAsync());
        }
        await using var read = new MtgEngineDbContext(options);
        Assert.Equal(["renamed", "added", "later"], (await read.Collections.AsNoTracking().SingleAsync()).Tags);
    }
}
