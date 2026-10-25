using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed partial class CollectionMetadataService
{
    internal sealed record Snapshot(Guid Id, Guid CollectionId, string OracleId, string? ScryfallId, int Quantity, int QuantityFoil,
        string? Notes, string Board, DateTime AddedAt, decimal? PriceUsdAtAdd, decimal? PriceUsdFoilAtAdd,
        string Language, string? Condition, decimal? PurchasePrice, string? PurchaseCurrency, string TagsJson, int QuantityEtched = 0, decimal? PriceUsdEtchedAtAdd = null, string FinishMetadataJson = "{}")
    {
        public static Snapshot From(CollectionCard c) => new(c.Id, c.CollectionId, c.OracleId, c.ScryfallId, c.Quantity, c.QuantityFoil,
            c.Notes, c.Board, c.AddedAt, c.PriceUsdAtAdd, c.PriceUsdFoilAtAdd, c.Language, c.Condition, c.PurchasePrice, c.PurchaseCurrency, c.TagsJson, c.QuantityEtched, c.PriceUsdEtchedAtAdd, c.FinishMetadataJson);
        public CollectionCard ToCard() => new()
        {
            Id = Id,
            CollectionId = CollectionId,
            OracleId = OracleId,
            ScryfallId = ScryfallId,
            Quantity = Quantity,
            QuantityFoil = QuantityFoil,
            QuantityEtched = QuantityEtched,
            PriceUsdEtchedAtAdd = PriceUsdEtchedAtAdd,
            FinishMetadataJson = FinishMetadataJson,
            Notes = Notes,
            Board = Board,
            AddedAt = AddedAt,
            PriceUsdAtAdd = PriceUsdAtAdd,
            PriceUsdFoilAtAdd = PriceUsdFoilAtAdd,
            Language = Language,
            Condition = Condition,
            PurchasePrice = PurchasePrice,
            PurchaseCurrency = PurchaseCurrency,
            TagsJson = TagsJson,
        };
    }
}
