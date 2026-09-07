using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>One seat being asked for: the account that will hold it and the deck it brings.</summary>
public sealed record NewGameSeat(
    string UserId,
    string Name,
    IReadOnlyList<string> DeckOracleIds,
    IReadOnlyList<string> CommandZoneOracleIds);

/// <summary>A table being created. The honour-system setting is chosen here and never again.</summary>
public sealed record NewGameRequest(
    IReadOnlyList<NewGameSeat> Seats,
    ulong Seed,
    bool AllowHonourSystem,
    string Format,
    int StartingLife);

/// <summary>
/// The live tables, and the log each one is stored as.
/// </summary>
/// <remarks>
/// <b>State is a fold of the log, and only the log is stored.</b> Creating a game runs the
/// engine's <c>Create</c> and stores the events it produced; every applied command appends its
/// events; a restart replays them through <c>GameReducer.Replay</c>. There is no serialized
/// state, so there is nothing that can disagree with the log about what happened.
/// <para>
/// The deck gate runs once, at creation. A card's status can move underneath a table that is
/// already playing — the pipeline writes <c>cards/</c> continuously — and a game that was legal
/// when it started stays legal, so a resumed game recomputes what is enforced but never refuses
/// itself back out of existence.
/// </para>
/// </remarks>
public sealed class GameSessionService
{
    private readonly ConcurrentDictionary<Guid, GameSession> _live = new();
    private readonly IGameLogStore _store;
    private readonly ICardFilePool _pool;
    private readonly DeckGate _gate;
    private readonly Func<Subsystems> _subsystems;

    public GameSessionService(IGameLogStore store, ICardFilePool pool, DeckGate gate, Func<Subsystems>? subsystems = null)
    {
        _store = store;
        _pool = pool;
        _gate = gate;
        _subsystems = subsystems ?? Subsystems.Wave1;
    }

    /// <summary>
    /// Creates a table: every deck through the gate, then the engine, then the log stored.
    /// </summary>
    /// <exception cref="InvalidRequestException">A deck holds a card this table cannot play.</exception>
    public async Task<GameSession> CreateAsync(NewGameRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Seats.Count < 2)
            throw new InvalidRequestException("A game needs at least two seats.");

        var enforced = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in request.Seats)
        {
            var admission = _gate.Admit([.. seat.DeckOracleIds, .. seat.CommandZoneOracleIds], request.AllowHonourSystem);
            enforced.UnionWith(admission.EnforcedOracleIds);
        }

        ct.ThrowIfCancellationRequested();

        var setup = new GameSetup(
            [.. request.Seats.Select(s => new PlayerStart(s.Name, [.. s.DeckOracleIds], [.. s.CommandZoneOracleIds]))],
            request.Seed,
            request.StartingLife,
            request.Format);

        var engine = new Game(_subsystems(), _pool);
        var created = engine.Create(setup);

        var id = Guid.NewGuid();
        var session = new GameSession(
            id, engine, created.State, created.Events,
            [.. request.Seats.Select(s => s.UserId)],
            enforced.ToImmutable(),
            request.AllowHonourSystem);

        await _store.CreateAsync(
            new PersistedGame
            {
                Id = id,
                Format = request.Format,
                AllowHonourSystem = request.AllowHonourSystem,
                SeatsJson = JsonSerializer.Serialize(session.Seats),
                LogJson = EventLogSerializer.Write(session.Log),
                EventCount = session.Log.Count,
                Finished = session.Finished,
            },
            ct).ConfigureAwait(false);

        _live[id] = session;
        return session;
    }

    /// <summary>The table, live if it is in memory and replayed from its log if it is not.</summary>
    public async Task<GameSession?> FindAsync(Guid id, CancellationToken ct = default)
    {
        if (_live.TryGetValue(id, out var live))
            return live;

        var stored = await _store.LoadAsync(id, ct).ConfigureAwait(false);
        if (stored is null)
            return null;

        var log = EventLogSerializer.Read(stored.LogJson).ToImmutableList();
        var state = GameReducer.Replay(log);
        var seats = (JsonSerializer.Deserialize<string[]>(stored.SeatsJson) ?? []).ToImmutableList();
        var session = new GameSession(
            id, new Game(_subsystems(), _pool), state, log, seats,
            EnforcedIn(log), stored.AllowHonourSystem);

        return _live.GetOrAdd(id, session);
    }

    /// <summary>Applies a command to a table and stores the log it produced.</summary>
    public Task<GameResult> ApplyAsync(GameSession session, Command command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ApplyAsync(command, PersistAsync, ct);
    }

    /// <summary>Whether the table plays this object by the rules, or leaves it to the players.</summary>
    public static bool IsEnforced(GameSession session, ObjectId what)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.State.Objects.TryGetValue(what, out var obj)
            && session.EnforcedOracleIds.Contains(obj.Definition.OracleId);
    }

    private Task PersistAsync(GameSession session, ImmutableList<GameEvent> appended, CancellationToken ct) =>
        _store.SaveLogAsync(session.Id, EventLogSerializer.Write(session.Log), session.Log.Count, session.Finished, ct);

    /// <summary>
    /// What a resumed game enforces, read back from the decks in its first event.
    /// </summary>
    /// <remarks>
    /// Not the gate: a card verified after the game began does not become enforced mid-table
    /// (the players have been handling it themselves), and a card demoted by a refutation does
    /// not throw a game away. Both are decided against the pool as it is now, which is the only
    /// answer that is the same for every player at the table.
    /// </remarks>
    private ImmutableHashSet<string> EnforcedIn(IEnumerable<GameEvent> log)
    {
        var started = log.OfType<GameStarted>().FirstOrDefault();
        if (started is null)
            return [];

        return ImmutableHashSet.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            started.Players
                .SelectMany(p => p.DeckOracleIds.Concat(p.CommandZoneOracleIds))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => _pool.StatusOf(id).Status == ImplementationStatus.Verified));
    }
}
