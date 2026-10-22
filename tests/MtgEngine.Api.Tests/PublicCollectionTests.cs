using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class PublicCollectionTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MtgEngineDbContext _db;
    private readonly User _user = new() { Username = "Collector", Email = "private@example.test", PasswordHash = "secret" };
    private readonly PublicCollectionService _service;
    public PublicCollectionTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(_user);
        var collection = new Collection(_user.Id.ToString(), "Secret collection");
        var deck = new Collection(_user.Id.ToString(), "Deck", isDeck: true);
        _db.Collections.AddRange(collection, deck);
        _db.CollectionCards.AddRange(new CollectionCard(collection.Id, "sol", "printing", 2, 1, "Private note")
        { PurchasePrice = 100m, PurchaseCurrency = "USD", TagsJson = "[\"Private tag\"]" },
            new CollectionCard(collection.Id, "sol", "printing", 1, 0) { Language = "fr" },
            new CollectionCard(deck.Id, "deck-only", "other", 99, 0));
        _db.SaveChanges();
        _service = new(_db, new Lookup());
    }
    [Fact]
    public async Task Sharing_is_private_by_default_and_revocation_is_immediate()
    {
        Assert.False(await _service.Visibility(_user.Id, default));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.Summary("collector", default));
        await _service.SetVisibility(_user.Id, true, default);
        var result = await _service.Summary("COLLECTOR", default);
        Assert.Equal(4, result.Copies);
        Assert.Equal(1, result.UniqueCards);
        Assert.Equal(1, result.UniquePrintings);
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in new[] { "Price", "Currency", "private@example", "Secret collection", "Private note", "Private tag", "deck-only", "Password", "sol" })
            Assert.DoesNotContain(secret, json);
        await _service.SetVisibility(_user.Id, false, default);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.Summary("Collector", default));
    }
    [Fact]
    public async Task Deleted_and_unknown_accounts_never_publish_counts()
    {
        await _service.SetVisibility(_user.Id, true, default);
        _user.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.Summary("Collector", default));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.Summary("%", default));
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
    private sealed class Lookup : StubScryfallService
    {
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) => Task.FromResult<CardDefinition?>(null);
        public override Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) => Task.FromResult<CardDefinition?>(null);
    }
}
