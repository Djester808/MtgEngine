using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Where a game's log lives between commands.</summary>
/// <remarks>
/// An interface rather than the <see cref="MtgEngineDbContext"/> directly, because the session
/// is a singleton holding live games and the context is scoped: the session would otherwise
/// capture a disposed context, which is the classic way this goes wrong. It also lets the hub
/// tests hold a table in memory without a database.
/// </remarks>
public interface IGameLogStore
{
    Task<PersistedGame?> LoadAsync(Guid id, CancellationToken ct = default);

    Task<bool> TryCreateAsync(PersistedGame game, CancellationToken ct = default);

    /// <summary>Appends one batch under an event-count concurrency check.</summary>
    Task AppendAsync(Guid id, string eventsJson, int expectedCount, int addedCount, bool finished, CancellationToken ct = default);
}

/// <summary>The database. One row per game, the log appended to per command.</summary>
public sealed class GameLogStore(IServiceScopeFactory scopes) : IGameLogStore
{
    public async Task<bool> TryCreateAsync(PersistedGame game, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var written = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO PersistedGames (Id, Format, AllowHonourSystem, SeatsJson, LogJson, EventCount, Finished, CreatedAt, UpdatedAt)
            VALUES ({game.Id}, {game.Format}, {game.AllowHonourSystem}, {game.SeatsJson}, {game.LogJson}, {game.EventCount}, {game.Finished}, {game.CreatedAt}, {game.UpdatedAt})
            ON CONFLICT(Id) DO NOTHING", ct).ConfigureAwait(false);
        return written == 1;
    }

    public async Task<PersistedGame?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var game = await db.PersistedGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct).ConfigureAwait(false);
        if (game is null)
            return null;
        var batches = await db.Set<PersistedGameEvent>().AsNoTracking().Where(batch => batch.GameId == id)
            .OrderBy(batch => batch.Sequence).Select(batch => batch.Json).ToListAsync(ct).ConfigureAwait(false);
        if (batches.Count > 0)
        {
            var initial = game.LogJson.Trim();
            var parts = new[] { initial[1..^1] }.Concat(batches.Select(batch => batch.Trim()[1..^1]))
                .Where(part => !string.IsNullOrWhiteSpace(part));
            game.LogJson = "[" + string.Join(",", parts) + "]";
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return game;
    }

    public async Task AppendAsync(Guid id, string eventsJson, int expectedCount, int addedCount, bool finished, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = await db.PersistedGames.Where(game => game.Id == id && game.EventCount == expectedCount)
            .ExecuteUpdateAsync(update => update.SetProperty(game => game.EventCount, expectedCount + addedCount)
                .SetProperty(game => game.Finished, finished).SetProperty(game => game.UpdatedAt, DateTime.UtcNow), ct)
            .ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidResourceStateException("The saved game changed. Reconnect before trying again.");
        if (addedCount > 0)
        {
            db.Set<PersistedGameEvent>().Add(new PersistedGameEvent { GameId = id, Sequence = expectedCount, Json = eventsJson });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
