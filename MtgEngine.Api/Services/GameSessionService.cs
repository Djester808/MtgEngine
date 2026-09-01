using System.Collections.Concurrent;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
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
    private readonly IGameStore _store;

    public GameSession(
        Guid gameId, Game game, IReadOnlyDictionary<Guid, string> seatNames, IGameStore store)
    {
        GameId = gameId;
        Game = game;
        SeatNames = seatNames;
        _store = store;
    }

    public Guid GameId { get; }

    /// <summary>Never touched outside <see cref="MutateAsync"/> or <see cref="ReadAsync"/>.</summary>
    private Game Game { get; }

    public IReadOnlyDictionary<Guid, string> SeatNames { get; }

    /// <summary>When the last action happened, so an abandoned game can be swept up.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Runs an action against the game under its lock, and stores what happened.
    /// </summary>
    /// <remarks>
    /// The save is inside the lock, and deliberately: it is what makes the stored log the same
    /// log the players just played. Saving outside would let two actions interleave and write
    /// their logs in the other order, so a restart would restore a game that had gone backwards.
    /// <para>
    /// A failed save does not fail the action. The move has already happened in a game the
    /// players are watching, and throwing here would tell them a legal play was rejected when it
    /// was not — the cost of a lost save is a game that rolls back to the previous action on a
    /// restart, which is smaller than the cost of desynchronising the table.
    /// </para>
    /// </remarks>
    public async Task<T> MutateAsync<T>(Func<Game, T> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = action(Game);
            LastActivityUtc = DateTimeOffset.UtcNow;
            await SaveAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stores the game as it stands. The caller holds the lock.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await _store.SaveAsync(
                new StoredGameLog(
                    GameId, EventLogSerializer.Write(Game.Log), LastActivityUtc, Game.State.IsOver),
                ct).ConfigureAwait(false);

            SaveFailure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveFailure = ex;
        }
    }

    /// <summary>
    /// Why the last save failed, or null.
    /// </summary>
    /// <remarks>
    /// Recorded rather than thrown, and read by <see cref="IdleGameSweeper"/>, which logs it on
    /// every tick for as long as it lasts. A game quietly not being saved for an hour is exactly
    /// the failure this class exists to prevent, so the record needs somewhere to be seen —
    /// a field nobody reads would be the same silence with more code.
    /// </remarks>
    public Exception? SaveFailure { get; private set; }

    /// <summary>Stores the game as it stands, for a game that has just been created.</summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(ct).ConfigureAwait(false);
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
/// Memory is a cache over the store, not the record. A game's log is a complete account of it —
/// that is the whole design — so a game that is not in memory is rebuilt by replaying its log
/// rather than being lost, and a restart costs the players nothing but the reconnect.
/// </remarks>
public sealed class GameSessionService : IDisposable
{
    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();
    private readonly IAbilitySource _abilities;
    private readonly IGameStore _store;

    public GameSessionService(IAbilitySource abilities, IGameStore store)
    {
        _abilities = abilities;
        _store = store;
    }

    /// <summary>How long an untouched game is kept in memory before it is evicted.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromHours(3);

    /// <summary>
    /// How long a stored game is kept before it is deleted.
    /// </summary>
    /// <remarks>
    /// Far longer than <see cref="Idle"/>, because the two answer different questions. Eviction
    /// is about memory and is reversible — the game comes back off disk when someone opens it.
    /// Deletion is not, so it waits until the game is one nobody is coming back to.
    /// </remarks>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Starts a game and returns its id.</summary>
    public async Task<Guid> CreateAsync(
        IReadOnlyList<PlayerSetup> setups, int? seed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setups);

        var gameId = Guid.NewGuid();
        var random = new GameRandom(seed ?? Random.Shared.Next());
        var game = Game.Start(gameId, setups, random, abilities: _abilities);
        game.BeginPlay();

        var session = new GameSession(
            gameId, game, setups.ToDictionary(s => s.PlayerId, s => s.Name), _store);

        _sessions[gameId] = session;

        // Stored before it is played, so a game created and then interrupted by a restart is
        // still there when the players come back — opening hands are already dealt by this point.
        await session.PersistAsync(ct).ConfigureAwait(false);
        return gameId;
    }

    /// <summary>A game already in memory, or null. Does not go to the store.</summary>
    public GameSession? Find(Guid gameId) => _sessions.GetValueOrDefault(gameId);

    /// <summary>
    /// A game, from memory or from the store.
    /// </summary>
    /// <remarks>
    /// A finished game is not rehydrated. Its log is kept as the record of the match, but there
    /// is nothing left to play, and handing back a session would let a client take actions in a
    /// game that is over (CR 104.1).
    /// </remarks>
    public async Task<GameSession?> FindAsync(Guid gameId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(gameId, out var live))
            return live;

        var stored = await _store.LoadAsync(gameId, ct).ConfigureAwait(false);
        if (stored is null || stored.IsOver)
            return null;

        var rebuilt = Rehydrate(stored);

        // Two requests can miss at once. Whichever lands second discards its own copy rather
        // than replacing the first, so the game keeps one lock and both callers act on the same
        // session — two sessions over one game would each apply actions the other never saw.
        var session = _sessions.GetOrAdd(gameId, rebuilt);
        if (!ReferenceEquals(session, rebuilt))
            rebuilt.Dispose();

        return session;
    }

    private GameSession Rehydrate(StoredGameLog stored)
    {
        var log = EventLogSerializer.Read(stored.Log);

        // The randomness is new; see Game.Resume. Every shuffle's outcome is in the log, so the
        // board comes back exactly — what must not come back is the sequence of future draws.
        var game = Game.Resume(log, new GameRandom(Random.Shared.Next()), _abilities);

        var seats = ((GameStarted)log[0]).Seats
            .ToDictionary(seat => seat.PlayerId, seat => seat.Name);

        return new GameSession(stored.GameId, game, seats, _store);
    }

    /// <summary>Whether the player is seated at this game, which is what authorises an action.</summary>
    public async Task<bool> IsSeatedAsync(Guid gameId, Guid playerId, CancellationToken ct = default)
    {
        var session = await FindAsync(gameId, ct).ConfigureAwait(false);
        return session?.SeatNames.ContainsKey(playerId) == true;
    }

    /// <summary>Drops a game from memory. It stays in the store and can be reopened.</summary>
    public bool Evict(Guid gameId)
    {
        if (!_sessions.TryRemove(gameId, out var session))
            return false;

        session.Dispose();
        return true;
    }

    /// <summary>Ends a game for good: out of memory, and off disk.</summary>
    public async Task<bool> RemoveAsync(Guid gameId, CancellationToken ct = default)
    {
        var evicted = Evict(gameId);
        await _store.DeleteAsync(gameId, ct).ConfigureAwait(false);
        return evicted;
    }

    /// <summary>Live games whose last save did not land.</summary>
    public IReadOnlyList<GameSession> NotSaving() =>
        [.. _sessions.Values.Where(s => s.SaveFailure is not null)];

    /// <summary>Games untouched for longer than <see cref="Idle"/>.</summary>
    public IReadOnlyList<Guid> Stale(DateTimeOffset nowUtc) =>
        [.. _sessions.Values.Where(s => nowUtc - s.LastActivityUtc > Idle).Select(s => s.GameId)];

    /// <summary>Stored games past <see cref="Retention"/>, whether or not they are in memory.</summary>
    public Task<IReadOnlyList<Guid>> ExpiredAsync(
        DateTimeOffset nowUtc, CancellationToken ct = default) =>
        _store.ExpiredAsync(nowUtc - Retention, ct);

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();
    }
}
