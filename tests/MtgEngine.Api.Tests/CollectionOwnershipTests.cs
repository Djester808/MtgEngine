using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// What "owned" means when the deck grid asks. Decks and collections share a table, so
/// the distinction is a flag rather than a schema boundary — which is exactly why it is
/// easy to get wrong and worth pinning.
/// </summary>
public sealed class CollectionOwnershipTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly MtgEngineDbContext _db;
    private readonly CollectionService _sut;
    private const string UserId = "user-1";

    public CollectionOwnershipTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_conn).Options;
        _db = new MtgEngineDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new CollectionService(_db, new Lookup(), new CardHistoryService(_db));
    }

    /// <summary>Ownership never consults Scryfall; the stub is only here to construct.</summary>
    private sealed class Lookup : StubScryfallService;

    private Collection NewCollection(string name, bool isDeck = false, string userId = UserId)
    {
        var c = new Collection(userId, name, null, isDeck);
        _db.Collections.Add(c);
        return c;
    }

    private void AddCard(Collection collection, string oracleId, int qty = 1, int foil = 0)
    {
        _db.CollectionCards.Add(
            new CollectionCard(collection.Id, oracleId, $"s-{oracleId}-{qty}-{foil}", qty, foil, null, "main"));
    }

    [Fact]
    public async Task Reports_every_oracle_id_held_in_a_collection()
    {
        var col = NewCollection("Binder");
        AddCard(col, "oracle-1");
        AddCard(col, "oracle-2");
        await _db.SaveChangesAsync();

        var owned = await _sut.GetOwnedOracleIdsAsync(UserId);

        Assert.Equal(["oracle-1", "oracle-2"], owned.OrderBy(x => x));
    }

    [Fact]
    public async Task A_card_that_is_only_in_a_deck_is_not_owned()
    {
        var deck = NewCollection("Sticky Test Deck", isDeck: true);
        AddCard(deck, "oracle-deck-only");
        await _db.SaveChangesAsync();

        Assert.Empty(await _sut.GetOwnedOracleIdsAsync(UserId));
    }

    [Fact]
    public async Task Owning_a_card_counts_even_when_it_is_also_in_a_deck()
    {
        var col = NewCollection("Binder");
        var deck = NewCollection("Deck", isDeck: true);
        AddCard(col, "oracle-1");
        AddCard(deck, "oracle-1");
        await _db.SaveChangesAsync();

        Assert.Equal(["oracle-1"], await _sut.GetOwnedOracleIdsAsync(UserId));
    }

    [Fact]
    public async Task A_row_with_no_copies_left_is_not_ownership()
    {
        var col = NewCollection("Binder");
        AddCard(col, "oracle-empty", qty: 0, foil: 0);
        AddCard(col, "oracle-foil-only", qty: 0, foil: 2);
        await _db.SaveChangesAsync();

        Assert.Equal(["oracle-foil-only"], await _sut.GetOwnedOracleIdsAsync(UserId));
    }

    [Fact]
    public async Task One_id_per_card_however_many_rows_hold_it()
    {
        var one = NewCollection("Binder");
        var two = NewCollection("Cube");
        AddCard(one, "oracle-1", qty: 2);
        AddCard(one, "oracle-1", qty: 1, foil: 1);
        AddCard(two, "oracle-1");
        await _db.SaveChangesAsync();

        Assert.Equal(["oracle-1"], await _sut.GetOwnedOracleIdsAsync(UserId));
    }

    [Fact]
    public async Task Another_users_collection_is_not_yours()
    {
        var theirs = NewCollection("Theirs", userId: "user-2");
        AddCard(theirs, "oracle-theirs");
        await _db.SaveChangesAsync();

        Assert.Empty(await _sut.GetOwnedOracleIdsAsync(UserId));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
