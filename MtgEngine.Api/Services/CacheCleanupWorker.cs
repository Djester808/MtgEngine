using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;

namespace MtgEngine.Api.Services;

/// <summary>
/// Periodically prunes the persistent AI caches so they don't grow without bound.
/// </summary>
/// <remarks>
/// <see cref="AiCacheService"/> treats entries past its read TTL (14 days) as misses but
/// never deletes them, and every <c>ModelVersion</c> bump orphans the whole previous
/// generation. <see cref="ISynergyService"/> only ever inserts scored rows. Left alone,
/// both tables grow monotonically on SQLite with no reclamation. This sweep deletes rows
/// old enough that they can no longer be served.
/// </remarks>
public sealed class CacheCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CacheCleanupWorker> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Well past the 14-day read TTL — anything older is expired or version-orphaned.</summary>
    private static readonly TimeSpan AiCacheRetention = TimeSpan.FromDays(30);

    /// <summary>Synergy scores are pricier to regenerate, so they are kept longer.</summary>
    private static readonly TimeSpan SynergyRetention = TimeSpan.FromDays(60);

    public CacheCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<CacheCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

            var now = DateTime.UtcNow;
            var aiCutoff = now - AiCacheRetention;
            var synergyCutoff = now - SynergyRetention;

            var aiRemoved = await db.AiResponseCache
                .Where(c => c.CreatedAt < aiCutoff)
                .ExecuteDeleteAsync(ct);
            var synergyRemoved = await db.CardSynergyScores
                .Where(c => c.CreatedAt < synergyCutoff)
                .ExecuteDeleteAsync(ct);

            if (aiRemoved > 0 || synergyRemoved > 0)
                _logger.LogInformation(
                    "Cache cleanup: removed {Ai} AI cache rows and {Synergy} synergy rows",
                    aiRemoved, synergyRemoved);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutting down — expected.
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "CacheCleanupWorker: sweep failed"); }
            catch { /* logger may be disposed during shutdown */ }
        }
    }
}
