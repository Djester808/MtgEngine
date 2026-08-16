using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The audit trail behind the card modal's History tab. These pin the two properties that
/// make it worth having: every way a card can move writes an event, and an event stays
/// readable after the collection it happened in is gone.
/// </summary>
public sealed class CardHistoryTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly CollectionService _sut;
    private readonly CardHistoryService _history;
    private const string UserId = "user-1";
    private const string OracleId = "oracle-1";

    public CardHistoryTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();
        _history = new CardHistoryService(_db);
        _sut = new CollectionService(_db, new Lookup(), _history);
    }

    private sealed class Lookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
            Task.FromResult<CardDefinition?>(
                new CardDefinition { OracleId = OracleId, Name = "Card", SetCode = "lea" });

        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<CardDefinition?>(
                new CardDefinition { OracleId = oracleId, Name = "Card", SetCode = "lea" });
    }

    private Collection NewCollection(string name, bool isDeck = false, string userId = UserId)
    {
        var c = new Collection(userId, name, isDeck: isDeck);
        _db.Collections.Add(c);
        return c;
    }

    private CollectionCard AddCard(
        Collection collection, string? scryfallId, int qty = 1, int foil = 0, string board = "main")
    {
        var card = new CollectionCard(collection.Id, OracleId, scryfallId, qty, foil, null, board);
        _db.CollectionCards.Add(card);
        return card;
    }

    private Task<CardHistoryEntryDto[]> HistoryAsync(string userId = UserId) =>
        _history.GetForCardAsync(userId, OracleId, 100, default);

    // ---- Recording ------------------------------------------------------

    [Fact]
    public async Task AddingANewCard_RecordsAnAddedEvent()
    {
        var c = NewCollection("Staples");
        await _db.SaveChangesAsync();

        await _sut.AddCardToCollectionAsync(
            c.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1", Quantity: 2, QuantityFoil: 1));

        var entry = Assert.Single(await HistoryAsync());
        Assert.Equal(CollectionCardEventType.Added, entry.EventType);
        Assert.Equal("Staples", entry.CollectionName);
        Assert.False(entry.IsDeck);
        Assert.Equal(2, entry.QuantityDelta);
        Assert.Equal(1, entry.QuantityFoilDelta);
        Assert.Equal(2, entry.QuantityAfter);
        Assert.Equal(1, entry.QuantityFoilAfter);
        // Set code and price come from the definition already resolved on the add path.
        Assert.Equal("lea", entry.SetCode);
    }

    [Fact]
    public async Task AddingMoreOfACardAlreadyHeld_RecordsAQuantityChange_NotASecondAdd()
    {
        var c = NewCollection("Staples");
        await _db.SaveChangesAsync();
        var request = new AddCardToCollectionRequest(OracleId, "scry-1", Quantity: 2);

        await _sut.AddCardToCollectionAsync(c.Id, UserId, request);
        await _sut.AddCardToCollectionAsync(c.Id, UserId, request);

        var entries = await HistoryAsync();
        Assert.Equal(2, entries.Length);
        // Newest first: the increment, then the original add.
        Assert.Equal(CollectionCardEventType.QuantityChanged, entries[0].EventType);
        Assert.Equal(2, entries[0].QuantityDelta);
        // The increment runs as ExecuteUpdate, so this is the assertion that the recorded
        // "after" is the real post-write row count rather than a guess.
        Assert.Equal(4, entries[0].QuantityAfter);
        Assert.Equal(CollectionCardEventType.Added, entries[1].EventType);
    }

    [Fact]
    public async Task UpdatingQuantityAndPrinting_RecordsBothAsSeparateEvents()
    {
        var c = NewCollection("Staples");
        var card = AddCard(c, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        await _sut.UpdateCollectionCardAsync(
            c.Id, card.Id, UserId, new UpdateCollectionCardRequest(5, 0, ScryfallId: "scry-2"));

        var entries = await HistoryAsync();
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, e => e.EventType == CollectionCardEventType.PrintingChanged);
        var qty = Assert.Single(entries, e => e.EventType == CollectionCardEventType.QuantityChanged);
        Assert.Equal(3, qty.QuantityDelta);
        Assert.Equal(5, qty.QuantityAfter);
    }

    [Fact]
    public async Task UpdatingOnlyNotes_RecordsNothing()
    {
        var c = NewCollection("Staples");
        var card = AddCard(c, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        await _sut.UpdateCollectionCardAsync(
            c.Id, card.Id, UserId, new UpdateCollectionCardRequest(2, 0, Notes: "mint"));

        Assert.Empty(await HistoryAsync());
    }

    [Fact]
    public async Task RemovingACard_RecordsTheCopiesLost()
    {
        var c = NewCollection("Staples");
        var card = AddCard(c, "scry-1", qty: 3, foil: 1);
        await _db.SaveChangesAsync();

        await _sut.RemoveCardFromCollectionAsync(c.Id, card.Id, UserId);

        var entry = Assert.Single(await HistoryAsync());
        Assert.Equal(CollectionCardEventType.Removed, entry.EventType);
        Assert.Equal(-3, entry.QuantityDelta);
        Assert.Equal(-1, entry.QuantityFoilDelta);
        Assert.Equal(0, entry.QuantityAfter);
        Assert.Equal(0, entry.QuantityFoilAfter);
    }

    [Fact]
    public async Task RemovingByOracle_RecordsEveryRowItDeleted()
    {
        var c = NewCollection("Staples");
        AddCard(c, "scry-1", qty: 2);
        AddCard(c, "scry-2", qty: 1, board: "side");
        await _db.SaveChangesAsync();

        await _sut.RemoveCardByOracleAsync(c.Id, OracleId, UserId);

        // ExecuteDelete never materializes rows; without the pre-read both would vanish
        // from history and the tab would show the card simply ceasing to exist.
        var entries = await HistoryAsync();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Equal(CollectionCardEventType.Removed, e.EventType));
        Assert.Contains(entries, e => e.Board == "side");
    }

    // ---- Transfers write both halves ------------------------------------

    [Fact]
    public async Task MovingACard_RecordsBothSidesNamingTheOtherEnd()
    {
        var source = NewCollection("Binder");
        var target = NewCollection("Commander", isDeck: true);
        var card = AddCard(source, "scry-1", qty: 3);
        await _db.SaveChangesAsync();

        await _sut.MoveCardAsync(
            source.Id, card.Id, UserId, new MoveCardRequest(target.Id, Quantity: 2));

        var entries = await HistoryAsync();
        Assert.Equal(2, entries.Length);

        var movedOut = Assert.Single(entries, e => e.EventType == CollectionCardEventType.MovedOut);
        Assert.Equal("Binder", movedOut.CollectionName);
        Assert.Equal("Commander", movedOut.CounterpartCollectionName);
        Assert.Equal(-2, movedOut.QuantityDelta);
        Assert.Equal(1, movedOut.QuantityAfter);

        var movedIn = Assert.Single(entries, e => e.EventType == CollectionCardEventType.MovedIn);
        Assert.Equal("Commander", movedIn.CollectionName);
        Assert.True(movedIn.IsDeck);
        Assert.Equal("Binder", movedIn.CounterpartCollectionName);
        Assert.Equal(2, movedIn.QuantityDelta);
        Assert.Equal(2, movedIn.QuantityAfter);
    }

    [Fact]
    public async Task MergingCollections_RecordsBothSidesForEachCard()
    {
        var source = NewCollection("Old");
        var target = NewCollection("New");
        AddCard(source, "scry-1", qty: 2);
        await _db.SaveChangesAsync();

        await _sut.MergeCollectionsAsync(
            target.Id, UserId, new MergeCollectionsRequest(source.Id, DeleteSource: true));

        var entries = await HistoryAsync();
        Assert.Contains(entries, e => e.EventType == CollectionCardEventType.MovedOut);
        Assert.Contains(entries, e => e.EventType == CollectionCardEventType.MovedIn);
    }

    // ---- History outlives the collection --------------------------------

    [Fact]
    public async Task DeletingACollection_RecordsItsCardsAsRemoved_AndTheHistorySurvives()
    {
        var c = NewCollection("Doomed");
        AddCard(c, "scry-1", qty: 4);
        await _db.SaveChangesAsync();

        await _sut.DeleteCollectionAsync(c.Id, UserId);

        // The cards went with the collection via cascade. The events must not: "which
        // collection did I delete this out of" is exactly what the tab is for, and the
        // events carry no foreign key precisely so the cascade cannot reach them.
        Assert.Empty(_db.Collections.Where(x => x.Id == c.Id));
        var entry = Assert.Single(await HistoryAsync());
        Assert.Equal(CollectionCardEventType.Removed, entry.EventType);
        Assert.Equal("Doomed", entry.CollectionName);
        Assert.Equal(-4, entry.QuantityDelta);
    }

    [Fact]
    public async Task RenamingACollectionLater_DoesNotRewriteWhatTheEventSaid()
    {
        var c = NewCollection("Original Name");
        await _db.SaveChangesAsync();
        await _sut.AddCardToCollectionAsync(
            c.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1"));

        await _sut.UpdateCollectionAsync(c.Id, UserId, new UpdateCollectionRequest("Renamed"));

        var entry = Assert.Single(await HistoryAsync());
        Assert.Equal("Original Name", entry.CollectionName);
    }

    // ---- The timestamp must be unambiguous on the wire -------------------

    [Fact]
    public async Task CreatedAt_ComesBackMarkedUtc_SoTheJsonCarriesAZ()
    {
        var c = NewCollection("Staples");
        await _db.SaveChangesAsync();
        await _sut.AddCardToCollectionAsync(
            c.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1"));

        var entry = Assert.Single(await HistoryAsync());

        // SQLite has no date type, so the value round-trips through TEXT as Unspecified.
        // Serialized that way it has no trailing Z, and JavaScript reads a bare date-time
        // as *local* — which put every event hours in the future and made the History tab
        // read "just now" for the length of the viewer's UTC offset.
        Assert.Equal(DateTimeKind.Utc, entry.CreatedAt.Kind);
        Assert.EndsWith("Z", System.Text.Json.JsonSerializer.Serialize(entry.CreatedAt).Trim('"'));
        Assert.True(
            (DateTime.UtcNow - entry.CreatedAt).Duration() < TimeSpan.FromMinutes(1),
            "a just-written event must read as now, not offset into the future or past");
    }

    // ---- Isolation and bounds -------------------------------------------

    [Fact]
    public async Task HistoryIsScopedToTheUser()
    {
        var mine = NewCollection("Mine");
        var theirs = NewCollection("Theirs", userId: "user-2");
        await _db.SaveChangesAsync();
        await _sut.AddCardToCollectionAsync(
            mine.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1"));
        await _sut.AddCardToCollectionAsync(
            theirs.Id, "user-2", new AddCardToCollectionRequest(OracleId, "scry-2"));

        Assert.Single(await HistoryAsync());
        Assert.Single(await HistoryAsync("user-2"));
    }

    [Fact]
    public async Task HistoryIsScopedToTheCard()
    {
        var c = NewCollection("Staples");
        await _db.SaveChangesAsync();
        await _sut.AddCardToCollectionAsync(
            c.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1"));
        await _sut.AddCardToCollectionAsync(
            c.Id, UserId, new AddCardToCollectionRequest("other-oracle", "scry-9"));

        Assert.Single(await HistoryAsync());
    }

    [Fact]
    public async Task ReadIsClampedToTheMaximum()
    {
        var c = NewCollection("Staples");
        await _db.SaveChangesAsync();
        for (var i = 0; i < 5; i++)
            await _sut.AddCardToCollectionAsync(
                c.Id, UserId, new AddCardToCollectionRequest(OracleId, "scry-1"));

        // An over-large limit clamps rather than scanning unbounded.
        Assert.Equal(5, (await _history.GetForCardAsync(UserId, OracleId, int.MaxValue, default)).Length);
        Assert.Equal(2, (await _history.GetForCardAsync(UserId, OracleId, 2, default)).Length);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
