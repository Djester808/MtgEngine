using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
namespace MtgEngine.Api.Services;

public sealed record OwnedPrinting(string OracleId, string? ScryfallId, string Language, int Quantity, int QuantityFoil, int QuantityEtched = 0);
public sealed class CollectionOwnershipService(MtgEngineDbContext db)
{
    public async Task<OwnedPrinting[]> GetAsync(string owner, CancellationToken ct)
    {
        var rows = await db.CollectionCards.AsNoTracking().Where(c => c.Collection.UserId == owner && !c.Collection.IsDeck && c.Quantity + c.QuantityFoil + c.QuantityEtched > 0)
            .Select(c => new OwnedPrinting(c.OracleId, c.ScryfallId, c.Language, c.Quantity, c.QuantityFoil, c.QuantityEtched)).ToArrayAsync(ct).ConfigureAwait(false);
        return [.. rows.GroupBy(c => new { c.OracleId, c.ScryfallId, c.Language }).Select(g => new OwnedPrinting(g.Key.OracleId, g.Key.ScryfallId, g.Key.Language, g.Sum(c => c.Quantity), g.Sum(c => c.QuantityFoil), g.Sum(c => c.QuantityEtched)))];
    }
}
