using System.Collections.Concurrent;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Services;

/// <summary>Raised when a game changes, so the transport can push new views.</summary>
public sealed record GameChanged(Guid GameId, IReadOnlyList<string> Log);

/// <summary>
/// A game in progress, and the lock that makes it safe to share.
/// </summary>
/// <remarks>
/// One game is one critical section. <see cref="Game"/> is deliberately not thread-safe — a
/// rules engine that tried to be would be a rules engine full of locks — so every action goes
/// through <see cref="MutateAsync"/>, which serialises them per game rather than globally. Two
/// tables do not wait on each other.
/// </remarks>
public sealed class GameSession : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GameSession(Guid gameId, Game game, IReadOnlyDictionary<Guid, string> seatNames)
    {
        GameId = gameId;
        Game = game;
        SeatNames = seatNames;
    }

    public Guid GameId { get; }

    /// <summary>Never touched outside <see cref="MutateAsync"/> or <see cref="ReadAsync"/>.</summary>
    private Game Game { get; }

    public IReadOnlyDictionary<Guid, string> SeatNames { get; }

    /// <summary>When the last action happened, so an abandoned game can be swept up.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Runs an action against the game under its lock.</summary>
    public async Task<T> MutateAsync<T>(Func<Game, T> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = action(Game);
            LastActivityUtc = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Builds one player's view under the lock (CR 400.2).
    /// </summary>
    /// <remarks>
    /// The projection happens here rather than at the caller so that no code path outside this
    /// class ever holds a <c>GameState</c>. That is the rule the previous engine's hub broke by
    /// broadcasting state to everyone in the group.
    /// </remarks>
    public async Task<GameView> ReadAsync(Guid playerId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Game.ViewFor(playerId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The log as text, for a client's game journal.</summary>
    public async Task<IReadOnlyList<string>> LogAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. Game.Log.Select(e => e.Describe())];
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>
/// Every game the server is running, and who is seated at each.
/// </summary>
/// <remarks>
/// In memory. A game's log is a complete account of it (that is the whole design), so persisting
/// games later means storing the log and replaying it — no schema for state is needed, and none
/// is invented here in anticipation.
/// </remarks>
public sealed class GameSessionService : IDisposable
{
    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();
    private readonly IAbilitySource _abilities;

    public GameSessionService(IAbilitySource abilities) => _abilities = abilities;

    /// <summary>How long an untouched game is kept before it is swept up.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromHours(3);

    /// <summary>Starts a game and returns its id.</summary>
    public Guid Create(IReadOnlyList<PlayerSetup> setups, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(setups);

        var gameId = Guid.NewGuid();
        var random = new GameRandom(seed ?? Random.Shared.Next());
        var game = Game.Start(gameId, setups, random, abilities: _abilities);
        game.BeginPlay();

        var session = new GameSession(
            gameId, game, setups.ToDictionary(s => s.PlayerId, s => s.Name));

        _sessions[gameId] = session;
        return gameId;
    }

    public GameSession? Find(Guid gameId) => _sessions.GetValueOrDefault(gameId);

    /// <summary>Whether the player is seated at this game, which is what authorises an action.</summary>
    public bool IsSeated(Guid gameId, Guid playerId) =>
        Find(gameId)?.SeatNames.ContainsKey(playerId) == true;

    public bool Remove(Guid gameId)
    {
        if (!_sessions.TryRemove(gameId, out var session))
            return false;

        session.Dispose();
        return true;
    }

    /// <summary>Games untouched for longer than <see cref="Idle"/>.</summary>
    public IReadOnlyList<Guid> Stale(DateTimeOffset nowUtc) =>
        [.. _sessions.Values.Where(s => nowUtc - s.LastActivityUtc > Idle).Select(s => s.GameId)];

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();
    }
}
