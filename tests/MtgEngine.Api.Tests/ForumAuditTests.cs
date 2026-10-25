using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MtgEngine.Api.Data;
using MtgEngine.Api.Migrations;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class ForumAuditTests
{
    private sealed class Lookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(new CardDefinition { OracleId = oracleId, Name = oracleId });
    }

    [Fact]
    public async Task Forum_pages_are_bounded_and_republish_preserves_comment_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = Guid.NewGuid().ToString();
        var decks = Enumerable.Range(0, 4).Select(i => new Collection(owner, $"Deck {i}", isDeck: true)).ToArray();
        decks[0].CommanderOracleId = "primary";
        decks[0].SecondaryCommanderOracleId = "partner";
        db.Collections.AddRange(decks);
        var service = new ForumService(db, new Lookup());
        await db.SaveChangesAsync();
        var posts = new List<Guid>();
        foreach (var deck in decks)
            posts.Add((await service.PublishDeckAsync(owner, "Author", new(deck.Id))).Id);
        for (var i = 0; i < 4; i++)
            await service.AddCommentAsync(posts[0], owner, "Author", new($"Comment {i}"));
        var republished = await service.PublishDeckAsync(owner, "Author", new(decks[0].Id, "Updated"));
        Assert.Equal(4, republished.CommentCount);
        var page1 = await service.GetAllPostsAsync(0, 2);
        var page2 = await service.GetAllPostsAsync(2, 2);
        Assert.Equal(2, page1.Length);
        Assert.Equal(2, page2.Length);
        Assert.Empty(page1.Select(p => p.Id).Intersect(page2.Select(p => p.Id)));
        var detail = await service.GetPostAsync(posts[0], 1, 2);
        Assert.NotNull(detail);
        Assert.Equal("primary", detail.CommanderOracleId);
        Assert.Equal("partner", detail.SecondaryCommanderOracleId);
        Assert.Equal(["Comment 1", "Comment 2"], detail.Comments.Select(c => c.Content));
    }

    [Fact]
    public async Task Historical_merge_changes_acquisition_metadata_only_on_quantity_recipient()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE CollectionCards(Id TEXT, CollectionId TEXT, OracleId TEXT, Board TEXT,
                ScryfallId TEXT, Quantity INTEGER, QuantityFoil INTEGER, AddedAt TEXT,
                PriceUsdAtAdd TEXT, PriceUsdFoilAtAdd TEXT);
            INSERT INTO CollectionCards VALUES('unbound','deck','oracle','main',NULL,2,0,'2020-01-01','1','2');
            INSERT INTO CollectionCards VALUES('first','deck','oracle','main','printing1',3,0,'2020-02-01','3','4');
            INSERT INTO CollectionCards VALUES('second','deck','oracle','main','printing2',5,0,'2020-03-01','5','6');
            """;
        await command.ExecuteNonQueryAsync();
        var migration = new CollapsePinnedUnpinnedPairs();
        foreach (var operation in migration.UpOperations.Cast<SqlOperation>())
        {
            command.CommandText = operation.Sql;
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = "SELECT Quantity || ':' || AddedAt || ':' || PriceUsdAtAdd FROM CollectionCards WHERE Id = 'second'";
        Assert.Equal("5:2020-03-01:5", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT Quantity || ':' || AddedAt || ':' || PriceUsdAtAdd FROM CollectionCards WHERE Id = 'first'";
        Assert.Equal("5:2020-01-01:1", await command.ExecuteScalarAsync());
    }
}
