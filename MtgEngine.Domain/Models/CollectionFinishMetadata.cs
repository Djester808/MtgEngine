namespace MtgEngine.Domain.Models;

public sealed record CollectionFinishMetadata(string? Condition = null, decimal? PurchasePrice = null, string? Currency = null,
    string[]? Tags = null, string? Notes = null, DateTime? AddedAt = null, decimal? PriceAtAdd = null);
