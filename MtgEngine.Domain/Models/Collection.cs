namespace MtgEngine.Domain.Models;

/// <summary>
/// Represents a user's card collection (e.g., "My Collection", "Modern Staples", etc.)
/// </summary>
public sealed class Collection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CoverUri { get; set; }
    public string? Format { get; set; }
    public string? CommanderOracleId { get; set; }
    public bool IsDeck { get; set; } = false;
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Cards owned in this collection
    /// </summary>
    public ICollection<CollectionCard> Cards { get; set; } = [];

    public Collection() { }

    public Collection(string userId, string name, string? description = null, bool isDeck = false)
    {
        UserId = userId;
        Name = name;
        Description = description;
        IsDeck = isDeck;
    }
}

/// <summary>
/// Represents a card instance owned in a collection (with quantity tracking)
/// </summary>
public sealed class CollectionCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    /// <summary>The oracle ID of the card (from CardDefinition)</summary>
    public string OracleId { get; set; } = string.Empty;
    
    /// <summary>The Scryfall card ID (for tracking specific printings)</summary>
    public string? ScryfallId { get; set; }

    /// <summary>How many non-foil copies we own</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>How many foil copies we own</summary>
    public int QuantityFoil { get; set; } = 0;

    /// <summary>Custom notes about this copy</summary>
    public string? Notes { get; set; }

    /// <summary>When this card was added to the collection</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public CollectionCard() { }

    public CollectionCard(
        Guid collectionId,
        string oracleId,
        string? scryfallId = null,
        int quantity = 1,
        int quantityFoil = 0,
        string? notes = null)
    {
        CollectionId = collectionId;
        OracleId = oracleId;
        ScryfallId = scryfallId;
        Quantity = quantity;
        QuantityFoil = quantityFoil;
        Notes = notes;
    }
}
