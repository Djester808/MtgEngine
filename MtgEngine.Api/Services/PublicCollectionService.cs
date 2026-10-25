using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class PublicCollectionService(MtgEngineDbContext db, ICardLookup cards)
{
    public async Task<bool> Visibility(Guid userId, CancellationToken ct) =>
        (await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct)
            ?? throw new ResourceNotFoundException("Account not found.")).PublicCollection;

    public async Task SetVisibility(Guid userId, bool visible, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct)
            ?? throw new ResourceNotFoundException("Account not found.");
        user.PublicCollection = visible;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PublicCollectionSummary> Summary(string username, CancellationToken ct)
    {
        if (username.Length > 64)
            throw new ResourceNotFoundException("No public collection.");
        var normalized = username.ToLowerInvariant();
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username.ToLower() == normalized
            && u.DeletedAt == null && u.PublicCollection, ct) ?? throw new ResourceNotFoundException("No public collection.");
        var ownerId = owner.Id.ToString();
        var rows = await db.CollectionCards.AsNoTracking().Where(c => c.Collection.UserId == ownerId
            && !c.Collection.IsDeck && c.Quantity + c.QuantityFoil + c.QuantityEtched > 0).ToListAsync(ct);
        var definitions = new Dictionary<string, CardDefinition?>();
        var colors = new Dictionary<string, int>();
        var types = new Dictionary<string, int>();
        var rarities = new Dictionary<string, int>();
        var sets = new Dictionary<string, int>();
        var curve = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!definitions.TryGetValue(row.OracleId, out var card))
            {
                card = await cards.GetByOracleIdAsync(row.OracleId);
                definitions[row.OracleId] = card;
            }
            var copies = row.Quantity + row.QuantityFoil + row.QuantityEtched;
            foreach (var color in card?.ColorIdentity.Select(c => c.ToString()).DefaultIfEmpty("Colorless") ?? ["Unknown"])
                Add(colors, color, copies);
            foreach (var type in (card is null ? null : MtgEngine.Api.Mapping.DomainMapper.ToDto(card).CardTypes.Select(t => t.ToString()).DefaultIfEmpty("Other")) ?? ["Unknown"])
                Add(types, type, copies);
            // Resolve pinned printing for set and rarity; no monetary data enters this projection.
            var printing = row.ScryfallId is null ? card : await cards.GetByScryfallIdAsync(row.ScryfallId);
            Add(sets, printing?.SetCode ?? "Unpinned", copies);
            Add(rarities, printing?.Rarity ?? "Unknown", copies);
            Add(curve, card is null ? "Unknown" : card.Cmc >= 7 ? "7+" : card.Cmc.ToString(System.Globalization.CultureInfo.InvariantCulture), copies);
        }
        return new(rows.Sum(r => r.Quantity + r.QuantityFoil + r.QuantityEtched), rows.Where(r => r.ScryfallId is not null).Select(r => r.ScryfallId).Distinct().Count(),
            rows.Select(r => r.OracleId).Distinct().Count(), Buckets(colors), Buckets(types), Buckets(rarities), Buckets(sets),
            [new("Nonfoil", rows.Sum(r => r.Quantity)), new("Foil", rows.Sum(r => r.QuantityFoil)), new("Etched", rows.Sum(r => r.QuantityEtched))], Buckets(curve));
    }
    private static void Add(Dictionary<string, int> buckets, string key, int copies) => buckets[key] = buckets.GetValueOrDefault(key) + copies;
    private static CollectionCountBucket[] Buckets(Dictionary<string, int> buckets) =>
        [.. buckets.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new CollectionCountBucket(p.Key, p.Value))];
}
