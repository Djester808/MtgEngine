using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Moving and merging both fold copies into the destination's matching printing. These
/// pin the folding key, the quantity arithmetic, and the rule that acquisition data
/// (added-at and price-at-add) travels with the physical copies rather than being reset.
/// </summary>
public sealed class CollectionMoveMergeTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly CollectionService _sut;
    private const string UserId = "user-1";

    public CollectionMoveMergeTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();
        // Real history service, not a stub: it shares this context, so the move/merge tests
        // also prove the audit trail is written inside the same SaveChanges as the transfer.
        _sut = new CollectionService(_db, new Lookup(), new CardHistoryService(_db));
    }

    private sealed class Lookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult<CardDefinition?>(new CardDefinition { OracleId = "oracle-1", Name = "Card" });

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(new CardDefinition { OracleId = oracleId, Name = "Card" });
    }

    private Collection NewCollection(string name, string userId = UserId)
    {
        var c = new Collection(userId, name);
        _db.Collections.Add(c);
        return c;
    }

    private CollectionCard AddCard(
        Collection collection, string scryfallId, int qty = 1, int foil = 0,
        string board = "main", DateTime? addedAt = null, decimal? priceAtAdd = null,
        string oracleId = "oracle-1")
    {
        var card = new CollectionCard(collection.Id, oracleId, scryfallId, qty, foil, null, board)
        {
            AddedAt = addedAt ?? DateTime.UtcNow,
            PriceUsdAtAdd = priceAtAdd,
        };
        _db.CollectionCards.Add(card);
        return card;
    }

    // ---- Move ----------------------------------------------------------

    [Fact]
    public async Task Move_WholeRow_RemovesItFromTheSource()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 3, foil: 1);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id));

        Assert.Null(result.SourceRemainder);
        Assert.Equal(3, result.Target.Quantity);
        Assert.Equal(1, result.Target.QuantityFoil);
        Assert.Empty(_db.CollectionCards.Where(c => c.CollectionId == source.Id));
        Assert.Single(_db.CollectionCards.Where(c => c.CollectionId == target.Id));
    }

    [Fact]
    public async Task Move_PartialQuantity_LeavesTheRemainderBehind()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 4, foil: 2);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(
            source.Id, card.Id, UserId, new MoveCardRequest(target.Id, Quantity: 1, QuantityFoil: 2));

        Assert.Equal(1, result.Target.Quantity);
        Assert.Equal(2, result.Target.QuantityFoil);
        Assert.NotNull(result.SourceRemainder);
        Assert.Equal(3, result.SourceRemainder!.Quantity);
        Assert.Equal(0, result.SourceRemainder.QuantityFoil);
    }

    [Fact]
    public async Task Move_FoldsIntoTheMatchingPrintingInTheTarget()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 2);
        AddCard(target, "scry-1", qty: 5);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id));

        Assert.Equal(7, result.Target.Quantity);
        Assert.Single(_db.CollectionCards.Where(c => c.CollectionId == target.Id));
    }

    [Fact]
    public async Task Move_DoesNotFoldADifferentPrintingOrBoard()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 2, board: "main");
        AddCard(target, "scry-2", qty: 5, board: "main"); // other printing
        AddCard(target, "scry-1", qty: 5, board: "side"); // same printing, other board
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id));

        Assert.Equal(2, result.Target.Quantity); // its own new row, nothing folded
        Assert.Equal(3, await _db.CollectionCards.CountAsync(c => c.CollectionId == target.Id));
    }

    [Fact]
    public async Task Move_CarriesAcquisitionDataWithTheCopies()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var acquired = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        var card = AddCard(source, "scry-1", qty: 1, addedAt: acquired, priceAtAdd: 0.55m);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id));

        // The same physical copy — resetting these would restate when it was acquired and
        // wipe the baseline the price-change display compares against.
        Assert.Equal(acquired, result.Target.AddedAt);
        Assert.Equal(0.55m, result.Target.PriceUsdAtAdd);
    }

    [Fact]
    public async Task Move_FoldingKeepsTheEarlierAcquisition()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var older = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var card = AddCard(source, "scry-1", qty: 1, addedAt: older, priceAtAdd: 0.25m);
        AddCard(target, "scry-1", qty: 1, addedAt: newer, priceAtAdd: 9m);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id));

        Assert.Equal(older, result.Target.AddedAt);
        Assert.Equal(0.25m, result.Target.PriceUsdAtAdd);
    }

    [Fact]
    public async Task Move_RejectsMovingMoreCopiesThanAreHeld()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id, Quantity: 5)));
    }

    [Fact]
    public async Task Move_RejectsAnEmptyMoveAndASelfMove()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var card = AddCard(source, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(target.Id, 0, 0)));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(source.Id)));
    }

    [Fact]
    public async Task Move_RefusesCollectionsTheCallerDoesNotOwn()
    {
        var source = NewCollection("A");
        var stranger = NewCollection("B", userId: "someone-else");
        var card = AddCard(source, "scry-1");
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _sut.MoveCardAsync(source.Id, card.Id, UserId, new MoveCardRequest(stranger.Id)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _sut.MoveCardAsync(stranger.Id, card.Id, UserId, new MoveCardRequest(source.Id)));
    }

    // ---- Bulk move -----------------------------------------------------

    [Fact]
    public async Task MoveCards_MovesEverySelectedRowAndFoldsMatches()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var a = AddCard(source, "scry-1", qty: 2);
        var b = AddCard(source, "scry-2", qty: 1, foil: 1);
        AddCard(source, "scry-3", qty: 9); // not selected — stays put
        AddCard(target, "scry-1", qty: 5); // a folds into this
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardsAsync(
            source.Id, UserId, new MoveCardsRequest(target.Id, [a.Id, b.Id]));

        Assert.Equal(1, result.CardsMoved);
        Assert.Equal(1, result.CardsFolded);
        Assert.Equal(4, result.CopiesTransferred);
        Assert.Equal(2, result.RemovedCardIds.Length);
        Assert.Equal(7, _db.CollectionCards.Single(c => c.CollectionId == target.Id && c.ScryfallId == "scry-1").Quantity);
        Assert.Equal("scry-3", _db.CollectionCards.Single(c => c.CollectionId == source.Id).ScryfallId);
    }

    [Fact]
    public async Task MoveCards_RejectsTheWholeBatchWhenACardIsMissing()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var a = AddCard(source, "scry-1");
        await _db.SaveChangesAsync();

        // All-or-nothing: moving some and silently skipping others is worse than failing.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _sut.MoveCardsAsync(source.Id, UserId, new MoveCardsRequest(target.Id, [a.Id, Guid.NewGuid()])));
        Assert.Single(_db.CollectionCards.Where(c => c.CollectionId == source.Id));
    }

    [Fact]
    public async Task MoveCards_RejectsAnEmptySelectionAndASelfMove()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var a = AddCard(source, "scry-1");
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MoveCardsAsync(source.Id, UserId, new MoveCardsRequest(target.Id, [])));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MoveCardsAsync(source.Id, UserId, new MoveCardsRequest(source.Id, [a.Id])));
    }

    [Fact]
    public async Task MoveCards_IgnoresARepeatedIdRatherThanDoubleCounting()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        var a = AddCard(source, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        var result = await _sut.MoveCardsAsync(
            source.Id, UserId, new MoveCardsRequest(target.Id, [a.Id, a.Id]));

        Assert.Equal(2, result.CopiesTransferred);
        Assert.Equal(2, _db.CollectionCards.Single(c => c.CollectionId == target.Id).Quantity);
    }

    // ---- Merge ---------------------------------------------------------

    [Fact]
    public async Task Merge_FoldsMatchesAndMovesTheRest()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        AddCard(source, "scry-1", qty: 2);           // folds into the target's row
        AddCard(source, "scry-2", qty: 1, foil: 1);  // no counterpart, moves whole
        AddCard(target, "scry-1", qty: 3);
        await _db.SaveChangesAsync();

        var result = await _sut.MergeCollectionsAsync(
            target.Id, UserId, new MergeCollectionsRequest(source.Id));

        Assert.Equal(1, result.CardsMoved);
        Assert.Equal(1, result.CardsFolded);
        Assert.Equal(4, result.CopiesTransferred); // 2 + (1 + 1)
        Assert.Equal(2, result.Target.Cards.Length);
        Assert.Equal(5, result.Target.Cards.Single(c => c.ScryfallId == "scry-1").Quantity);
        Assert.Empty(_db.CollectionCards.Where(c => c.CollectionId == source.Id));
    }

    [Fact]
    public async Task Merge_KeepsTheSourceCollectionByDefault()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        AddCard(source, "scry-1");
        await _db.SaveChangesAsync();

        var result = await _sut.MergeCollectionsAsync(
            target.Id, UserId, new MergeCollectionsRequest(source.Id));

        Assert.False(result.SourceDeleted);
        Assert.NotNull(await _db.Collections.FindAsync(source.Id));
    }

    [Fact]
    public async Task Merge_DeletesTheEmptiedSourceWhenAsked()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        AddCard(source, "scry-1");
        await _db.SaveChangesAsync();

        var result = await _sut.MergeCollectionsAsync(
            target.Id, UserId, new MergeCollectionsRequest(source.Id, DeleteSource: true));

        Assert.True(result.SourceDeleted);
        Assert.Null(await _db.Collections.FindAsync(source.Id));
        Assert.Single(_db.CollectionCards.Where(c => c.CollectionId == target.Id));
    }

    [Fact]
    public async Task Merge_OfAnEmptyCollectionIsANoOp()
    {
        var source = NewCollection("A");
        var target = NewCollection("B");
        AddCard(target, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        var result = await _sut.MergeCollectionsAsync(
            target.Id, UserId, new MergeCollectionsRequest(source.Id));

        Assert.Equal(0, result.CardsMoved);
        Assert.Equal(0, result.CardsFolded);
        Assert.Equal(2, result.Target.Cards.Single().Quantity);
    }

    [Fact]
    public async Task Merge_RejectsMergingIntoItselfAndForeignCollections()
    {
        var target = NewCollection("B");
        var stranger = NewCollection("C", userId: "someone-else");
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidResourceStateException>(() =>
            _sut.MergeCollectionsAsync(target.Id, UserId, new MergeCollectionsRequest(target.Id)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _sut.MergeCollectionsAsync(target.Id, UserId, new MergeCollectionsRequest(stranger.Id)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
