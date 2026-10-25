using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Services;

public sealed record CollectionExport(string Content, string ContentType, string FileName);
public sealed class CollectionExportService(ICollectionReader collections, MtgEngineDbContext db)
{
    public async Task<CollectionExport> ExportAsync(Guid id, string owner, string format, CancellationToken ct)
    {
        if (format is not ("csv" or "text"))
            throw new InvalidRequestException("Choose CSV or plain text.");
        await using var snapshot = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Collections.AnyAsync(c => c.Id == id && c.UserId == owner && !c.IsDeck, ct))
            throw new ResourceNotFoundException("Collection not found.");
        var collection = await collections.GetCollectionAsync(id, owner, ct)
            ?? throw new ResourceNotFoundException("Collection not found.");
        var metadata = await db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == id).ToDictionaryAsync(c => c.Id, ct);
        return Format(collection, metadata, format);
    }
    public static CollectionExport Format(CollectionDetailDto collection, IReadOnlyDictionary<Guid, CollectionCard> metadata, string format)
    {
        var csv = new StringBuilder("scryfallId,oracleId,unpinned,name,setCode,collectorNumber,quantity,finish,language,board,condition,purchasePriceCents,currency,priceUsdAtAddCents,priceUsdFoilAtAddCents,notes,tags,purchasePrice,priceUsdAtAdd,priceUsdFoilAtAdd,addedAt,priceUsdEtchedAtAdd,priceUsdEtchedAtAddCents\r\n");
        var text = new StringBuilder();
        foreach (var card in collection.Cards.OrderBy(c => c.CardDetails?.Name ?? c.OracleId, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id))
        {
            var row = metadata[card.Id];
            foreach (var (quantity, finish) in new[] { (card.Quantity, "nonfoil"), (card.QuantityFoil, "foil"), (card.QuantityEtched, "etched") })
            {
                if (quantity <= 0)
                    continue;
                var name = card.CardDetails?.Name ?? card.OracleId;
                var detail = CollectionFinishData.Read(row, finish);
                var tags = string.Join(";", detail.Tags ?? []);
                var values = new object?[] { card.ScryfallId, card.OracleId, card.ScryfallId is null, name, card.ScryfallId is null ? null : card.CardDetails?.SetCode, card.ScryfallId is null ? null : card.CardDetails?.CollectorNumber, quantity, finish,
                    row.Language, row.Board, detail.Condition, Cents(detail.PurchasePrice), detail.Currency, Cents(card.PriceUsdAtAdd), Cents(card.PriceUsdFoilAtAdd), detail.Notes, tags,
                    detail.PurchasePrice, card.PriceUsdAtAdd, card.PriceUsdFoilAtAdd, (detail.AddedAt ?? row.AddedAt).ToString("O", CultureInfo.InvariantCulture), card.PriceUsdEtchedAtAdd, Cents(card.PriceUsdEtchedAtAdd) };
                csv.AppendLine(string.Join(",", values.Select(Cell)));
                text.Append(quantity.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(name.Replace('\r', ' ').Replace('\n', ' '));
                if (finish == "etched")
                    text.Append(" *E*");
                if (finish == "foil")
                    text.Append(" *F*");
                text.Append(" | scryfallId=").Append(card.ScryfallId).Append(" | unpinned=").Append(card.ScryfallId is null)
                    .Append(" | finish=").Append(finish).Append(" | language=").Append(row.Language).Append(" | board=").Append(row.Board)
                    .Append(" | condition=").Append(detail.Condition).Append(" | purchasePriceCents=").Append(Cents(detail.PurchasePrice)?.ToString(CultureInfo.InvariantCulture))
                    .Append(" | currency=").Append(detail.Currency).Append(" | notes=").Append(OneLine(detail.Notes)).Append(" | tags=").Append(OneLine(tags)).AppendLine();
            }
        }
        return format == "text" ? new(text.ToString(), "text/plain; charset=utf-8", "collection.txt")
            : new(csv.ToString(), "text/csv; charset=utf-8", "collection.csv");
    }
    private static long? Cents(decimal? amount) => amount.HasValue ? decimal.ToInt64(decimal.Round(amount.Value * 100, 0, MidpointRounding.AwayFromZero)) : null;
    private static string OneLine(string? value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Quoting alone does not stop spreadsheet formula execution.
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
