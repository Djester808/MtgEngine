using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;

namespace MtgEngine.Api.Services;

public sealed class CollectionUndoCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CollectionUndoCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
                var now = clock.GetUtcNow().UtcDateTime;
                await db.CollectionUndos.Where(u => u.ExpiresAt < now).ExecuteDeleteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Expired collection undo records could not be removed.");
            }
        }
    }
}
