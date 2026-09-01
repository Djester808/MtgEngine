namespace MtgEngine.Api.Services;

/// <summary>
/// Frees the memory held by games nobody has touched for hours, and eventually deletes them.
/// </summary>
/// <remarks>
/// Two clocks, because the two actions are not equally reversible. Eviction happens after hours
/// and costs a player nothing: the game is on disk, and opening it replays the log. Deletion
/// happens after a month and cannot be undone.
/// <para>
/// It does not concede on anyone's behalf. An evicted game is still there to come back to;
/// deciding that an absent player has lost is a rules question (CR 104.3a) and needs a real
/// timeout policy that players agree to, not a housekeeping job.
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
                var now = DateTimeOffset.UtcNow;

                // Reported every tick for as long as it lasts, rather than once: a store that
                // has gone away is a running game that will roll back on the next restart, and
                // it should be as loud on the tenth sweep as on the first.
                foreach (var session in _sessions.NotSaving())
                {
                    _logger.LogError(
                        session.SaveFailure, "Game {GameId} is not being saved.", session.GameId);
                }

                var stale = _sessions.Stale(now);
                foreach (var gameId in stale)
                    _sessions.Evict(gameId);

                var expired = await _sessions.ExpiredAsync(now, stoppingToken).ConfigureAwait(false);
                foreach (var gameId in expired)
                    await _sessions.RemoveAsync(gameId, stoppingToken).ConfigureAwait(false);

                if (stale.Count > 0 || expired.Count > 0)
                {
                    _logger.LogInformation(
                        "Evicted {Evicted} idle game(s); deleted {Deleted} expired.",
                        stale.Count, expired.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep must not take the host down; the next tick tries again.
                _logger.LogError(ex, "Idle game sweep failed.");
            }
        }
    }
}
