using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Tests;

public sealed class CollectionExportTests
{
    [Fact]
    public void ExportIncludesLegacyMetadataAndCentColumns()
    {
        var row = new CollectionCard { ScryfallId = "printing", OracleId = "oracle", Quantity = 1, Board = "side", Language = "ja", PurchasePrice = 1.25m, Notes = "copy notes" };
        var collection = new CollectionDetailDto { Cards = [new CollectionCardDto { Id = row.Id, ScryfallId = row.ScryfallId, OracleId = row.OracleId, Quantity = 1, CardDetails = new CardDto { Name = "Card", CollectorNumber = "42a" } }] };
        var metadata = new Dictionary<Guid, CollectionCard> { { row.Id, row } };
        var csv = CollectionExportService.Format(collection, metadata, "csv").Content;
        Assert.Contains("unpinned", csv);
        Assert.Contains("collectorNumber", csv);
        Assert.Contains("purchasePriceCents", csv);
        Assert.Contains("priceUsdAtAddCents", csv);
        Assert.Contains("\"42a\"", csv);
        Assert.Contains("\"125\"", csv);
        var text = CollectionExportService.Format(collection, metadata, "text").Content;
        Assert.Contains("language=ja", text);
        Assert.Contains("board=side", text);
        Assert.Contains("purchasePriceCents=125", text);
        Assert.Contains("notes=copy notes", text);
    }
    [Fact]
    public void EtchedExportUsesItsOwnDetailsAndLeavesUnpinnedSetUnspecified()
    {
        var row = new CollectionCard { OracleId = "o", Quantity = 1, QuantityEtched = 2, Condition = "NM", PurchasePrice = 1m };
        CollectionFinishData.Write(row, "etched", new CollectionFinishMetadata("LP", 4m, "USD", ["Etched tag"], "Etched note"));
        var collection = new CollectionDetailDto { Cards = [new CollectionCardDto { Id = row.Id, OracleId = "o", Quantity = 1, QuantityEtched = 2, CardDetails = new CardDto { Name = "Card", SetCode = "DEFAULT", CollectorNumber = "42" } }] };
        var csv = CollectionExportService.Format(collection, new Dictionary<Guid, CollectionCard> { { row.Id, row } }, "csv").Content;
        Assert.Contains("\"2\",\"etched\",\"en\",\"main\",\"LP\",\"400\"", csv);
        Assert.Contains("Etched note", csv);
        Assert.DoesNotContain("DEFAULT", csv);
        Assert.Contains("*E*", CollectionExportService.Format(collection, new Dictionary<Guid, CollectionCard> { { row.Id, row } }, "text").Content);
    }
    private sealed class Lookup : StubScryfallService;
    [Fact]
    public void Export_preserves_finishes_private_metadata_csv_escaping_and_unspecified_printings()
    {
        var row = new CollectionCard { Quantity = 2, QuantityFoil = 1, OracleId = "oracle", Language = "ja", Condition = "LP", PurchasePrice = 1.25m, PurchaseCurrency = "EUR", TagsJson = "[\"trade\"]", Notes = "=HYPERLINK(\"unsafe\")\nsecond line" };
        var collection = new CollectionDetailDto { Cards = [new CollectionCardDto { Id = row.Id, Quantity = 2, QuantityFoil = 1, OracleId = "oracle", CardDetails = new CardDto { Name = "Card, \"quoted\"", SetCode = "SET" } }] };
        var metadata = new Dictionary<Guid, CollectionCard> { { row.Id, row } };
        var csv = CollectionExportService.Format(collection, metadata, "csv");
        Assert.Contains("\"Card, \"\"quoted\"\"\"", csv.Content);
        Assert.Contains("\"2\",\"nonfoil\",\"ja\",\"main\",\"LP\",\"125\",\"EUR\"", csv.Content);
        Assert.Contains("\"1\",\"foil\"", csv.Content);
        Assert.Contains("'=HYPERLINK", csv.Content);
        Assert.Contains("trade", csv.Content);
        var text = CollectionExportService.Format(collection, metadata, "text");
        Assert.Contains("2 Card, \"quoted\"", text.Content);
        Assert.Contains("1 Card, \"quoted\" *F*", text.Content);
        Assert.Contains("notes==HYPERLINK", text.Content);
        Assert.DoesNotContain("NaN", csv.Content);
    }
    [Fact]
    public async Task Export_refuses_other_owners_decks_and_invalid_formats_and_handles_empty_collection()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var collection = new Collection("owner", "Empty");
        var deck = new Collection("owner", "Deck", isDeck: true);
        db.Collections.AddRange(collection, deck);
        await db.SaveChangesAsync();
        var service = new CollectionExportService(new CollectionService(db, new Lookup(), new CardHistoryService(db)), db);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.ExportAsync(collection.Id, "stranger", "csv", default));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.ExportAsync(deck.Id, "owner", "csv", default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => service.ExportAsync(collection.Id, "owner", "html", default));
        var result = await service.ExportAsync(collection.Id, "owner", "csv", default);
        Assert.StartsWith("scryfallId,", result.Content);
        Assert.Equal("", (await service.ExportAsync(collection.Id, "owner", "text", default)).Content);
    }
}
