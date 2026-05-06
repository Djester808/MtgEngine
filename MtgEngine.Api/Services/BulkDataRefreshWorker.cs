namespace MtgEngine.Api.Services;

/// <summary>
/// Hosted service that keeps Scryfall bulk-data files fresh.
/// On startup: downloads files if missing or stale, then builds in-memory indexes.
/// Daily: re-checks Scryfall for updated files (they publish daily around 09:00 UTC).
/// </summary>
public sealed class BulkDataRefreshWorker : BackgroundService
{
    private readonly BulkDataService _bulkData;
    private readonly ILogger<BulkDataRefreshWorker> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public BulkDataRefreshWorker(BulkDataService bulkData, ILogger<BulkDataRefreshWorker> logger)
    {
        _bulkData = bulkData;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial load/download on startup — runs in background so app starts immediately
        await RunRefresh(stoppingToken, isStartup: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunRefresh(stoppingToken, isStartup: false);
        }
    }

    private async Task RunRefresh(CancellationToken ct, bool isStartup)
    {
        try
        {
            if (isStartup)
                _logger.LogInformation("BulkDataRefreshWorker: startup refresh");
            else
                _logger.LogInformation("BulkDataRefreshWorker: scheduled daily refresh");

            await _bulkData.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutting down — expected
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "BulkDataRefreshWorker: refresh failed"); }
            catch { /* EventLog may be disposed during host shutdown; swallow to keep worker alive */ }
        }
    }
}
