using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IAiCacheService
{
    /// <summary>
    /// Returns the cached payload for these inputs, or invokes <paramref name="factory"/>
    /// and caches the result. A cache write failure never fails the request.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(
        string kind,
        string modelVersion,
        IEnumerable<string?> keyParts,
        Func<Task<T>> factory,
        TimeSpan? ttl = null);
}

public sealed class AiCacheService : IAiCacheService
{
    private readonly MtgEngineDbContext _db;
    private readonly ILogger<AiCacheService> _logger;

    /// <summary>Entries older than this are treated as misses and refreshed.</summary>
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(14);

    public AiCacheService(MtgEngineDbContext db, ILogger<AiCacheService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<T> GetOrCreateAsync<T>(
        string kind,
        string modelVersion,
        IEnumerable<string?> keyParts,
        Func<Task<T>> factory,
        TimeSpan? ttl = null)
    {
        var key = ComputeKey(keyParts);
        var cutoff = DateTime.UtcNow - (ttl ?? DefaultTtl);

        var hit = await _db.AiResponseCache.FirstOrDefaultAsync(c =>
            c.Kind == kind && c.CacheKey == key && c.ModelVersion == modelVersion);

        if (hit is not null && hit.CreatedAt >= cutoff)
        {
            try
            {
                var cached = JsonSerializer.Deserialize<T>(hit.PayloadJson, AnthropicResponse.JsonOptions);
                if (cached is not null)
                {
                    _logger.LogDebug("AI cache hit: {Kind} {Key}", kind, key[..8]);
                    return cached;
                }
            }
            catch (JsonException ex)
            {
                // Payload shape changed since it was written -- fall through and refresh.
                _logger.LogWarning(ex, "Discarding unreadable AI cache entry {Kind} {Key}", kind, key[..8]);
            }
        }

        var fresh = await factory();

        try
        {
            var payload = JsonSerializer.Serialize(fresh, AnthropicResponse.JsonOptions);
            if (hit is not null)
            {
                hit.PayloadJson = payload;
                hit.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.AiResponseCache.Add(new AiResponseCache
                {
                    Kind = kind,
                    CacheKey = key,
                    PayloadJson = payload,
                    ModelVersion = modelVersion,
                });
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Caching is an optimisation; never fail the caller because we could not store.
            _logger.LogWarning(ex, "Failed to cache AI response for {Kind}", kind);
        }

        return fresh;
    }

    /// <summary>
    /// SHA-256 over the key parts, newline-delimited so parts cannot run together
    /// (["ab","c"] must not collide with ["a","bc"]).
    /// </summary>
    private static string ComputeKey(IEnumerable<string?> parts)
    {
        var joined = string.Join('\n', parts.Select(p => p ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
