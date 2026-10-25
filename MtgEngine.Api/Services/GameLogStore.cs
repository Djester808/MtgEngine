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

    Task CreateAsync(PersistedGame game, CancellationToken ct = default);

    Task<bool> TryCreateAsync(PersistedGame game, CancellationToken ct = default);

    /// <summary>Replaces the stored log with the whole log as it stands, per applied command.</summary>
    Task SaveLogAsync(Guid id, string logJson, int eventCount, bool finished, CancellationToken ct = default);
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
        return await db.PersistedGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct).ConfigureAwait(false);
    }

    public async Task CreateAsync(PersistedGame game, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        db.PersistedGames.Add(game);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveLogAsync(Guid id, string logJson, int eventCount, bool finished, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        await db.PersistedGames
            .Where(g => g.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(g => g.LogJson, logJson)
                    .SetProperty(g => g.EventCount, eventCount)
                    .SetProperty(g => g.Finished, finished)
                    .SetProperty(g => g.UpdatedAt, DateTime.UtcNow),
                ct)
            .ConfigureAwait(false);
    }
}
