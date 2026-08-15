namespace MtgEngine.Domain.ValueObjects;

/// <summary>
/// Market data for one printing, from Scryfall's daily price feed (TCGplayer USD,
/// Cardmarket EUR, Cardhoarder tix). A null price means no listing for that finish,
/// not a price of zero. The marketplace ids locate the exact listing page; they are
/// stored instead of Scryfall's purchase_uris because those are ~400 chars of
/// affiliate-wrapped URL per printing — far too heavy for a 250k-entry index.
/// </summary>
public sealed record CardPrices
{
    /// <summary>Shared "no market data" instance — most lookups compare against this instead of allocating.</summary>
    public static readonly CardPrices None = new();

    public decimal? Usd { get; init; }
    public decimal? UsdFoil { get; init; }
    public decimal? UsdEtched { get; init; }
    public decimal? Eur { get; init; }
    public decimal? EurFoil { get; init; }
    public decimal? Tix { get; init; }

    /// <summary>TCGplayer product id → tcgplayer.com/product/{id}.</summary>
    public int? TcgplayerId { get; init; }
    /// <summary>Cardmarket product id (no stable public URL; kept for future use).</summary>
    public int? CardmarketId { get; init; }
    /// <summary>MTGO catalog id → cardhoarder.com/cards/{id}.</summary>
    public int? MtgoId { get; init; }
}
