namespace MtgEngine.Domain.Models;

/// <summary>
/// Cached LLM response, keyed by a hash of the request inputs.
/// </summary>
/// <remarks>
/// Deck suggestions and mana advice are deterministic-by-intent: the same deck and
/// commander should produce the same guidance. Without a cache, re-opening a panel
/// re-pays a multi-second Claude call. <see cref="ModelVersion"/> is part of the key
/// so changing the model or the prompt invalidates every stale entry automatically.
/// </remarks>
public sealed class AiResponseCache
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What produced this entry, e.g. "suggestions" or "mana-tune".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>SHA-256 of the normalised request inputs.</summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>Serialised response DTO.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Model id + prompt revision. Bump to invalidate.</summary>
    public string ModelVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
