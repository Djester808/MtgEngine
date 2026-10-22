using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class CollectionMetadataTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MtgEngineDbContext _db;
    private readonly CollectionMetadataService _metadata;
    private readonly Clock _clock = new();
    private readonly Collection _collection = new("owner", "Cards");
    private readonly CollectionCard _row;
    public CollectionMetadataTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _row = new(_collection.Id, "oracle", "printing", 2, 1, "Notes") { PriceUsdAtAdd = 3.50m, PriceUsdFoilAtAdd = 7m };
        _db.Collections.Add(_collection);
        _db.CollectionCards.Add(_row);
        _db.SaveChanges();
        _metadata = new(_db, _clock, new CardHistoryService(_db));
    }
    [Fact]
    public async Task FinishMetadataIsIndependentAndMovingOneFinishToAnotherLanguageKeepsOtherCopies()
    {
        _row.QuantityEtched = 1;
        await _db.SaveChangesAsync();
        await _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", new("en", "NM", 1m, "USD", ["regular"], "nonfoil", "regular note"), default);
        await _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", new("en", "LP", 2m, "EUR", ["foil"], "foil", "foil private note"), default);
        await _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", new("en", "DMG", 3m, "JPY", ["etched"], "etched", "etched note"), default);
        Assert.Equal("NM", (await _metadata.GetAsync(_collection.Id, _row.Id, "owner", default, "nonfoil")).Condition);
        var foil = await _metadata.GetAsync(_collection.Id, _row.Id, "owner", default, "foil");
        Assert.Equal(2m, foil.PurchasePrice);
        Assert.Equal("foil private note", foil.Notes);
        Assert.Equal("DMG", (await _metadata.GetAsync(_collection.Id, _row.Id, "owner", default, "etched")).Condition);
        await _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", foil with { Language = "ja" }, default);
        var rows = await _db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == _collection.Id).ToArrayAsync();
        var english = Assert.Single(rows, c => c.Language == "en");
        var japanese = Assert.Single(rows, c => c.Language == "ja");
        Assert.Equal(2, english.Quantity);
        Assert.Equal(0, english.QuantityFoil);
        Assert.Equal(1, english.QuantityEtched);
        Assert.Equal(1, japanese.QuantityFoil);
        var shared = JsonSerializer.Serialize(DomainMapper.ToDto(japanese, null));
        Assert.DoesNotContain("PurchasePrice", shared);
        Assert.DoesNotContain("foil private note", shared);
    }
    [Fact]
    public async Task Metadata_is_owner_only_validated_and_purchase_money_never_enters_shared_card_dtos()
    {
        var request = new CollectionMetadataRequest("FR", "lp", 12.34m, "eur", ["Trade", "Trade"]);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _metadata.UpdateAsync(_collection.Id, _row.Id, "stranger", request, default));
        await _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", request, default);
        var actual = await _metadata.GetAsync(_collection.Id, _row.Id, "owner", default);
        Assert.Equal("fr", actual.Language);
        Assert.Equal("LP", actual.Condition);
        Assert.Equal(12.34m, actual.PurchasePrice);
        Assert.Equal("EUR", actual.Currency);
        Assert.Single(actual.Tags);
        var shared = JsonSerializer.Serialize(DomainMapper.ToDto(_row, null));
        Assert.DoesNotContain("PurchasePrice", shared);
        Assert.DoesNotContain("PurchaseCurrency", shared);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _metadata.GetAsync(_collection.Id, _row.Id, "stranger", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", request with { Condition = "invalid" }, default));
    }
    [Fact]
    public async Task Language_is_part_of_the_printing_key_and_cannot_overwrite_another_language()
    {
        var french = new CollectionCard(_collection.Id, "oracle", "printing") { Language = "fr" };
        _db.CollectionCards.Add(french);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _metadata.UpdateAsync(_collection.Id, _row.Id, "owner", new("fr", null, null, null, []), default));
        Assert.Equal(2, await _db.CollectionCards.CountAsync());
    }
    [Theory]
    [InlineData("tag")]
    [InlineData("untag")]
    [InlineData("condition")]
    [InlineData("remove")]
    public async Task Every_bulk_action_can_be_undone_without_losing_quantities_or_metadata(string action)
    {
        _row.TagsJson = "[\"Trade\"]";
        _row.Condition = "LP";
        _row.PurchasePrice = 12m;
        _row.PurchaseCurrency = "USD";
        await _db.SaveChangesAsync();
        var result = await _metadata.BulkAsync(_collection.Id, "owner", new([_row.Id], action, "Trade", "NM"), default);
        Assert.Equal(1, result.Affected);
        await _metadata.UndoAsync(result.UndoToken, "owner", default);
        var restored = await _db.CollectionCards.AsNoTracking().SingleAsync();
        Assert.Equal(2, restored.Quantity);
        Assert.Equal(1, restored.QuantityFoil);
        Assert.Equal("LP", restored.Condition);
        Assert.Equal(12m, restored.PurchasePrice);
        Assert.Equal(3.50m, restored.PriceUsdAtAdd);
        Assert.Equal("[\"Trade\"]", restored.TagsJson);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _metadata.UndoAsync(result.UndoToken, "owner", default));
    }
    [Fact]
    public async Task Undo_refuses_newer_changes_instead_of_overwriting_them()
    {
        var result = await _metadata.BulkAsync(_collection.Id, "owner", new([_row.Id], "condition", Condition: "NM"), default);
        await _db.CollectionCards.Where(c => c.Id == _row.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.Notes, "Newer notes"));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _metadata.UndoAsync(result.UndoToken, "owner", default));
        Assert.Equal("Newer notes", (await _db.CollectionCards.AsNoTracking().SingleAsync()).Notes);
    }
    [Fact]
    public async Task Undo_is_owner_scoped_and_expires_after_five_seconds()
    {
        var result = await _metadata.BulkAsync(_collection.Id, "owner", new([_row.Id], "remove"), default);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _metadata.UndoAsync(result.UndoToken, "stranger", default));
        _clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => _metadata.UndoAsync(result.UndoToken, "owner", default));
        Assert.Empty(await _db.CollectionCards.ToArrayAsync());
    }
    [Fact]
    public async Task Bulk_mutation_rejects_a_missing_row_before_changing_anything()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _metadata.BulkAsync(_collection.Id, "owner", new([_row.Id, Guid.NewGuid()], "remove"), default));
        Assert.Single(await _db.CollectionCards.ToArrayAsync());
    }
    [Fact]
    public async Task Undo_does_not_touch_an_unselected_row()
    {
        var other = new CollectionCard(_collection.Id, "other", "other-printing");
        _db.CollectionCards.Add(other);
        await _db.SaveChangesAsync();
        var result = await _metadata.BulkAsync(_collection.Id, "owner", new([_row.Id], "tag", "Trade"), default);
        await _db.CollectionCards.Where(c => c.Id == other.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.Quantity, 9));
        await _metadata.UndoAsync(result.UndoToken, "owner", default);
        Assert.Equal(9, (await _db.CollectionCards.AsNoTracking().SingleAsync(c => c.Id == other.Id)).Quantity);
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
