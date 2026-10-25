using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class CollectionMoveMergeTests
{
    [Fact]
    public async Task PinningUnspecifiedPrintingKeepsAddedEtchedCopiesAndHistory()
    {
        var collection = NewCollection("Inventory");
        _db.CollectionCards.Add(new CollectionCard(collection.Id, "oracle-1", quantity: 2));
        await _db.SaveChangesAsync();
        var result = await _sut.AddCardToCollectionAsync(collection.Id, UserId,
            new("oracle-1", "printing", Quantity: 0, QuantityFoil: 0, QuantityEtched: 3));
        Assert.False(result.Created);
        var row = await _db.CollectionCards.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.Quantity);
        Assert.Equal(3, row.QuantityEtched);
        var history = await new CardHistoryService(_db).GetForCardAsync(UserId, "oracle-1", 10, default);
        Assert.Contains(history, h => h.QuantityEtchedDelta == 3 && h.QuantityEtchedAfter == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryCollectionDeletionPathRemovesPublishedDiscussion(bool merge)
    {
        var source = NewCollection("Published");
        source.IsDeck = true;
        var target = NewCollection("Target");
        var post = new ForumPost { DeckId = source.Id, AuthorId = UserId };
        _db.ForumPosts.Add(post);
        _db.ForumComments.Add(new ForumComment { ForumPostId = post.Id, AuthorId = UserId, Content = "Comment" });
        await _db.SaveChangesAsync();
        if (merge)
            await _sut.MergeCollectionsAsync(target.Id, UserId, new(source.Id, DeleteSource: true));
        else
            await _sut.DeleteCollectionAsync(source.Id, UserId);
        Assert.Empty(await _db.ForumPosts.ToArrayAsync());
        Assert.Empty(await _db.ForumComments.ToArrayAsync());
        Assert.False(await _db.Collections.AnyAsync(c => c.Id == source.Id));
    }

    [Fact]
    public async Task SwapReplacesOneCopyWithoutRemovingOtherPrintingsOrFinishes()
    {
        var deck = NewCollection("Deck");
        deck.IsDeck = true;
        AddCard(deck, "first", qty: 3, foil: 2, oracleId: "out");
        AddCard(deck, "second", qty: 2, oracleId: "out");
        await _db.SaveChangesAsync();
        Assert.True(await _sut.ReplaceCardCopiesAsync(deck.Id, UserId, "out", new("incoming", Quantity: 1)));
        var rows = await _db.CollectionCards.AsNoTracking().ToArrayAsync();
        Assert.Equal(7, rows.Sum(c => c.Quantity + c.QuantityFoil + c.QuantityEtched));
        Assert.Equal(6, rows.Where(c => c.OracleId == "out").Sum(c => c.Quantity + c.QuantityFoil));
        Assert.Equal(1, Assert.Single(rows, c => c.OracleId == "incoming").Quantity);
    }

    private sealed class FailAfterInsert : StubScryfallService
    {
        private int _calls;
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
        {
            if (++_calls == 2)
                throw new InvalidOperationException("Simulated lookup failure after insertion.");
            return Task.FromResult<CardDefinition?>(new() { OracleId = oracleId, Name = "Incoming" });
        }
    }

    [Fact]
    public async Task SwapFailureRollsBackInsertedCopyAndClearsRolledBackTracking()
    {
        var deck = NewCollection("Deck");
        deck.IsDeck = true;
        AddCard(deck, "out-print", qty: 3, oracleId: "out");
        await _db.SaveChangesAsync();
        var service = new CollectionService(_db, new FailAfterInsert(), new CardHistoryService(_db));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReplaceCardCopiesAsync(deck.Id, UserId, "out", new("incoming", Quantity: 1)));
        await _db.SaveChangesAsync();
        var row = await _db.CollectionCards.AsNoTracking().SingleAsync();
        Assert.Equal("out", row.OracleId);
        Assert.Equal(3, row.Quantity);
    }
}
