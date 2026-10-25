using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services.Community;

public sealed class ExploreDeckCopyService(ExploreService explore, ICollectionService collections, MtgEngineDbContext db)
{
    public async Task<DeckDetailDto> Copy(string userId, string oracleId, string variant, CancellationToken ct)
    {
        var page = await explore.Page(oracleId, "average", variant, ct);
        if (!page.HasPage)
            throw new ResourceNotFoundException("No average deck is available for this selection.");
        var entries = page.Commanders.Concat(page.Sections.SelectMany(s => s.Cards)).ToArray();
        if (entries.Length == 0 || entries.Any(e => e.Card is null))
            throw new InvalidResourceStateException("Some average-deck cards are not available in the card catalog yet. Try again after the catalog updates.");
        if (entries.Any(e => e.Quantity is < 1 or > 999) || entries.Sum(e => (long)e.Quantity) > 500)
            throw new InvalidResourceStateException("The average deck has unexpected card quantities.");
        var name = page.Subject.Name + " — Average" + (variant.Length > 0 ? " (" + variant + ")" : "");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deck = await collections.CreateDeckAsync(userId, new(name[..Math.Min(200, name.Length)],
            page.Subject.ImageUriArtCrop, "commander", page.Subject.OracleId));
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await collections.AddCardToCollectionAsync(deck.Id, userId, new(entry.Card!.OracleId, Quantity: entry.Quantity));
        }
        var result = await collections.GetDeckAsync(deck.Id, userId) ?? throw new ResourceNotFoundException("Deck not found.");
        await transaction.CommitAsync(ct);
        return result;
    }
}
