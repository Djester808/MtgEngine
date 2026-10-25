using Microsoft.Extensions.Caching.Memory;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

/// <summary>Bounded collection read snapshots shared by pages, invalidated by mutations
/// and catalog publication. Short expiry also refreshes live-provider prices.</summary>
public sealed class CollectionReadCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 50_000 });

    public CollectionDetailDto? Get((string Owner, Guid Id, DateTime Updated, long Catalog) key) =>
        _cache.TryGetValue(key, out CollectionDetailDto? value) ? value : null;

    public void Set((string Owner, Guid Id, DateTime Updated, long Catalog) key, CollectionDetailDto value) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, value.Cards.Length),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
        });

    public void Dispose() => _cache.Dispose();
}
