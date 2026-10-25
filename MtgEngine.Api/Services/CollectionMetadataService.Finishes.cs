using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Services;

public sealed partial class CollectionMetadataService
{
    private async Task UpdateFinishAsync(Collection collection, CollectionCard card, CollectionMetadataRequest request, string language, string? condition, string? currency, CancellationToken ct)
    {
        var finish = CollectionFinishData.Normalize(request.Finish!);
        var quantity = CollectionFinishData.Quantity(card, finish);
        if (quantity < 1)
            throw new InvalidRequestException("Add a copy in this finish before editing its details.");
        var previous = CollectionFinishData.Read(card, finish);
        var updated = previous with { Condition = condition, PurchasePrice = request.PurchasePrice, Currency = currency, Tags = NormalizeTags(request.Tags), Notes = request.Notes };
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (language == card.Language)
            CollectionFinishData.Write(card, finish, updated);
        else
        {
            var target = await db.CollectionCards.FirstOrDefaultAsync(c => c.CollectionId == card.CollectionId && c.Id != card.Id && c.OracleId == card.OracleId && c.ScryfallId == card.ScryfallId && c.Board == card.Board && c.Language == language, ct).ConfigureAwait(false);
            if (target is not null && CollectionFinishData.Quantity(target, finish) > 0)
                throw new InvalidResourceStateException("This finish already has copies in that language. Edit those copies instead.");
            if (target is null)
            {
                target = new CollectionCard(card.CollectionId, card.OracleId, card.ScryfallId, 0, 0, null, card.Board) { Language = language, AddedAt = previous.AddedAt ?? card.AddedAt, PriceUsdAtAdd = card.PriceUsdAtAdd, PriceUsdFoilAtAdd = card.PriceUsdFoilAtAdd, PriceUsdEtchedAtAdd = card.PriceUsdEtchedAtAdd };
                db.CollectionCards.Add(target);
            }
            CollectionFinishData.PreserveOwned(target);
            CollectionFinishData.SetQuantity(target, finish, quantity);
            CollectionFinishData.Write(target, finish, updated);
            CollectionFinishData.SetQuantity(card, finish, 0);
            CollectionFinishData.ClearEmpty(card);
            if (card.Quantity + card.QuantityFoil + card.QuantityEtched == 0)
                db.CollectionCards.Remove(card);
        }
        collection.UpdatedAt = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
