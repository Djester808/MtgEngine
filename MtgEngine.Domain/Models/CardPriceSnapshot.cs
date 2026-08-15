namespace MtgEngine.Domain.Models;

/// <summary>
/// One day's market prices for one printing, captured by the daily snapshot worker.
/// Scryfall only publishes current prices, so history exists only from the day a
/// printing first entered someone's collection — rows accumulate going forward.
/// One row per (ScryfallId, CapturedAt) — the unique index enforces it.
/// </summary>
public sealed class CardPriceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The printing this snapshot belongs to.</summary>
    public string ScryfallId { get; set; } = string.Empty;

    /// <summary>UTC date (midnight) of the capture.</summary>
    public DateTime CapturedAt { get; set; }

    public decimal? Usd { get; set; }
    public decimal? UsdFoil { get; set; }
    public decimal? UsdEtched { get; set; }
    public decimal? Eur { get; set; }
    public decimal? EurFoil { get; set; }
    public decimal? Tix { get; set; }
}
