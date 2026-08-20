namespace MtgEngine.Api.Services;

/// <summary>
/// Removes games nobody has touched for hours.
/// </summary>
/// <remarks>
/// Games live in memory, so a table someone closed the tab on is a leak that never ends. This is
/// the same shape as <c>CacheCleanupWorker</c>: a hosted service on a slow timer, doing one
/// cheap sweep.
/// <para>
/// It does not concede on anyone's behalf. A swept game simply stops existing; deciding that an
/// absent player has lost is a rules question (CR 104.3a) and needs a real timeout policy that
/// players agree to, not a housekeeping job.
/// </para>
/// </remarks>
public sealed class IdleGameSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly GameSessionService _sessions;
    private readonly ILogger<IdleGameSweeper> _logger;

    public IdleGameSweeper(GameSessionService sessions, ILogger<IdleGameSweeper> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var stale = _sessions.Stale(DateTimeOffset.UtcNow);
                foreach (var gameId in stale)
                    _sessions.Remove(gameId);

                if (stale.Count > 0)
                    _logger.LogInformation("Swept {Count} idle game(s).", stale.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep must not take the host down; the next tick tries again.
                _logger.LogError(ex, "Idle game sweep failed.");
            }
        }
    }
}
