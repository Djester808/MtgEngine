using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Tests;

public sealed class CollectionOwnershipTests
{
    [Fact]
    public async Task CountsOwnedCopiesAcrossCollectionsWithoutDecksOrOtherOwners()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var first = new Collection("owner", "First");
        var second = new Collection("owner", "Second");
        var deck = new Collection("owner", "Deck", isDeck: true);
        var other = new Collection("other", "Private");
        db.Collections.AddRange(first, second, deck, other);
        db.CollectionCards.AddRange(new CollectionCard(first.Id, "oracle", "printing", 2, 1), new CollectionCard(second.Id, "oracle", "printing", 3), new CollectionCard(deck.Id, "oracle", "printing", 40), new CollectionCard(other.Id, "oracle", "printing", 90), new CollectionCard(second.Id, "other-oracle", null, 0));
        await db.SaveChangesAsync();
        var owned = await new CollectionOwnershipService(db).GetAsync("owner", default);
        var row = Assert.Single(owned);
        Assert.Equal(5, row.Quantity);
        Assert.Equal(1, row.QuantityFoil);
        Assert.Equal("printing", row.ScryfallId);
        Assert.Empty(await new CollectionOwnershipService(db).GetAsync("stranger", default));
    }
}
