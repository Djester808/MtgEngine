using System.Collections.Immutable;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Services;

/// <summary>
/// One live table: the engine, the state it has folded to, and the log that made it.
/// </summary>
/// <remarks>
/// The session is the only mutable thing in the play path. Everything below it — state, events,
/// commands — is immutable, so a command is a swap of two fields under one lock rather than a
/// tree of objects being edited while somebody reads it.
/// <para>
/// <b>Seats are identity, not data.</b> <see cref="SeatOf"/> maps an account to a seat and is
/// the only way a caller becomes a player. A user id arriving in a message is never consulted:
/// a client that could name its own player id could act as its opponent, which is a cheat and
/// not a bug.
/// </para>
/// </remarks>
public sealed class GameSession : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Game _engine;

    internal GameSession(
        Guid id,
        Game engine,
        GameState state,
        ImmutableList<GameEvent> log,
        ImmutableList<string> seats,
        ImmutableHashSet<string> enforcedOracleIds,
        bool allowHonourSystem)
    {
        Id = id;
        _engine = engine;
        State = state;
        Log = log;
        Seats = seats;
        EnforcedOracleIds = enforcedOracleIds;
        AllowHonourSystem = allowHonourSystem;
    }

    public Guid Id { get; }

    /// <summary>The account signed in at each seat, index = seat. Empty means the seat is open.</summary>
    public ImmutableList<string> Seats { get; }

    /// <summary>Cards the engine plays. A manual action on one of these is refused, CR 100.6 by analogy: the rules are the rules here.</summary>
    public ImmutableHashSet<string> EnforcedOracleIds { get; }

    public bool AllowHonourSystem { get; }

    public GameState State { get; private set; }

    public ImmutableList<GameEvent> Log { get; private set; }

    public bool Finished => State.Outcome is not null;

    /// <summary>Which seat this account holds, or null if it is not at this table.</summary>
    public PlayerId? SeatOf(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
            return null;
        var index = Seats.IndexOf(userId, StringComparer.Ordinal);
        return index < 0 ? null : new PlayerId(index);
    }

    /// <summary>Every seated account, so the hub knows who to push a view to.</summary>
    public IEnumerable<(string UserId, PlayerId Player)> SeatedPlayers() =>
        Seats.Select((user, index) => (user, new PlayerId(index))).Where(s => s.user.Length > 0);

    /// <summary>
    /// What this player may see and do now.
    /// </summary>
    /// <remarks>
    /// The projector and the legal-action lister are wave-2 seams (E14) and are stubs until that
    /// lands, so both are null while the engine cannot answer. Pushing <c>ViewUpdated</c> with
    /// nothing in it is the honest report of that: the command was applied and the log grew, and
    /// there is no view yet. Swallowing the push instead would make a wired hub look unwired.
    /// </remarks>
    public (GameView? View, LegalActions? Legal) ProjectFor(PlayerId player)
    {
        GameView? view = null;
        LegalActions? legal = null;
        try
        {
            view = _engine.Engine.Views.Project(State, player);
        }
        catch (NotImplementedException)
        {
            // E14 has not landed. Reported as no view rather than as a failed command.
        }

        try
        {
            legal = _engine.Engine.Legal.For(State, player);
        }
        catch (NotImplementedException)
        {
            // Same seam.
        }

        return (view, legal);
    }

    /// <summary>
    /// Applies one command under this table's lock and returns what the engine decided.
    /// </summary>
    /// <remarks>
    /// A keyed lock per table rather than one lock for the server: two tables never contend, and
    /// two commands at one table are serialised, which is what "the server is authoritative"
    /// means when both players click at once.
    /// </remarks>
    internal async Task<GameResult> ApplyAsync(Command command, Func<GameSession, ImmutableList<GameEvent>, CancellationToken, Task> persist, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = _engine.Apply(State, command);
            if (result is not Applied applied)
                return result;

            State = applied.State;
            Log = Log.AddRange(applied.Events);
            await persist(this, applied.Events, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Releases the table's lock. A live table is not disposed while anyone is at it.</summary>
    public void Dispose() => _lock.Dispose();
}
