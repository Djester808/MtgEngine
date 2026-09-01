using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>A stored game, as the session layer needs it back.</summary>
public sealed record StoredGameLog(Guid GameId, string Log, DateTimeOffset LastActivityUtc, bool IsOver);

/// <summary>
/// Where games in progress are kept between restarts.
/// </summary>
/// <remarks>
/// An interface because the session layer is a singleton and the database context is scoped, and
/// because tests need a store that does not need a database to prove that saving happens at all.
/// </remarks>
public interface IGameStore
{
    Task SaveAsync(StoredGameLog game, CancellationToken ct = default);

    Task<StoredGameLog?> LoadAsync(Guid gameId, CancellationToken ct = default);

    Task DeleteAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Finished games, and games nobody has touched since <paramref name="before"/>.</summary>
    Task<IReadOnlyList<Guid>> ExpiredAsync(DateTimeOffset before, CancellationToken ct = default);
}

/// <summary>
/// Games on disk, in the application's own database.
/// </summary>
/// <remarks>
/// A whole log is rewritten on every action rather than appended to. That is quadratic in the
/// length of a game, and it is the right trade here: a long game's log is on the order of a
/// hundred kilobytes, actions are human-paced, and the alternative — a row per event — would
/// either repeat every card's printed characteristics on every row or need a second table to
/// share them, which is a schema whose only job is to make a save marginally cheaper.
/// <para>
/// If this ever stops being true, the fix is to append events and keep the card table beside
/// them; nothing above this class would have to change, which is why the interface hands over a
/// log rather than a document format.
/// </para>
/// </remarks>
public sealed class SqliteGameStore : IGameStore
{
    private readonly IServiceScopeFactory _scopes;

    public SqliteGameStore(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task SaveAsync(StoredGameLog game, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

        var existing = await db.PersistedGames
            .FirstOrDefaultAsync(g => g.GameId == game.GameId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.PersistedGames.Add(new PersistedGame
            {
                GameId = game.GameId,
                Log = game.Log,
                LastActivityUtc = game.LastActivityUtc.UtcDateTime,
                IsOver = game.IsOver,
            });
        }
        else
        {
            existing.Log = game.Log;
            existing.LastActivityUtc = game.LastActivityUtc.UtcDateTime;
            existing.IsOver = game.IsOver;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<StoredGameLog?> LoadAsync(Guid gameId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

        var stored = await db.PersistedGames
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GameId == gameId, ct)
            .ConfigureAwait(false);

        return stored is null
            ? null
            : new StoredGameLog(
                stored.GameId,
                stored.Log,
                new DateTimeOffset(stored.LastActivityUtc, TimeSpan.Zero),
                stored.IsOver);
    }

    public async Task DeleteAsync(Guid gameId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

        await db.PersistedGames
            .Where(g => g.GameId == gameId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> ExpiredAsync(
        DateTimeOffset before, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

        var cutoff = before.UtcDateTime;

        return await db.PersistedGames
            .AsNoTracking()
            .Where(g => g.LastActivityUtc < cutoff)
            .Select(g => g.GameId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// A store that keeps games for as long as the process lives.
/// </summary>
/// <remarks>
/// For tests, and for a deployment that deliberately wants games to end with the process. It is
/// a real implementation rather than a no-op: a store that silently discarded what it was given
/// would let a test pass while proving nothing about whether saving happened.
/// </remarks>
public sealed class InMemoryGameStore : IGameStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, StoredGameLog> _games = new();

    public int Count => _games.Count;

    public Task SaveAsync(StoredGameLog game, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        _games[game.GameId] = game;
        return Task.CompletedTask;
    }

    public Task<StoredGameLog?> LoadAsync(Guid gameId, CancellationToken ct = default) =>
        Task.FromResult(_games.GetValueOrDefault(gameId));

    public Task DeleteAsync(Guid gameId, CancellationToken ct = default)
    {
        _games.TryRemove(gameId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ExpiredAsync(
        DateTimeOffset before, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            [.. _games.Values.Where(g => g.LastActivityUtc < before).Select(g => g.GameId)]);
}
