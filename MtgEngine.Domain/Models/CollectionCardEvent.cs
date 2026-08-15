namespace MtgEngine.Domain.Models;

/// <summary>What happened to a card in a collection.</summary>
public enum CollectionCardEventType
{
    /// <summary>Copies entered the collection for the first time.</summary>
    Added = 0,
    /// <summary>Copy count changed on a row that already existed.</summary>
    QuantityChanged = 1,
    /// <summary>The row was re-pinned to a different printing.</summary>
    PrintingChanged = 2,
    /// <summary>The row was deleted outright.</summary>
    Removed = 3,
    /// <summary>Copies left this collection for another one.</summary>
    MovedOut = 4,
    /// <summary>Copies arrived from another collection.</summary>
    MovedIn = 5,
}

/// <summary>
/// One thing that happened to one card in one collection: when, which printing, how many
/// copies, and — for a transfer — the collection at the other end.
/// </summary>
/// <remarks>
/// Append-only. Rows are never updated or deleted when the card they describe changes,
/// which is the whole point: the <see cref="CollectionCard"/> row only knows its current
/// state, so "when did this become foil" or "where did this come from" was unanswerable.
/// The printing, set code and price are denormalised onto the event because the row they
/// came from may since have been re-pinned, moved or deleted — resolving them at read
/// time would report today's answer for a past event.
/// </remarks>
public sealed class CollectionCardEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The collection this event happened in.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>The card, stable across printings and across the row being deleted.</summary>
    public string OracleId { get; set; } = string.Empty;

    /// <summary>The printing involved, when the row pinned one.</summary>
    public string? ScryfallId { get; set; }

    /// <summary>Set code at the time, so a re-pin later cannot rewrite history.</summary>
    public string? SetCode { get; set; }

    /// <summary>Which board (main/side/maybe) the row sat on.</summary>
    public string Board { get; set; } = "main";

    public CollectionCardEventType EventType { get; set; }

    /// <summary>Regular copies moved by this event. Negative when copies left.</summary>
    public int QuantityDelta { get; set; }

    /// <summary>Foil copies moved by this event. Negative when copies left.</summary>
    public int QuantityFoilDelta { get; set; }

    /// <summary>Regular copies on the row after the event.</summary>
    public int QuantityAfter { get; set; }

    /// <summary>Foil copies on the row after the event.</summary>
    public int QuantityFoilAfter { get; set; }

    /// <summary>The other collection in a transfer; null for everything else.</summary>
    public Guid? CounterpartCollectionId { get; set; }

    /// <summary>
    /// That collection's name as it read at the time. Denormalised on purpose: the
    /// collection can be renamed or deleted, and a dangling id tells the user nothing.
    /// </summary>
    public string? CounterpartCollectionName { get; set; }

    /// <summary>USD price of the printing when the event happened, if known.</summary>
    public decimal? PriceUsd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
