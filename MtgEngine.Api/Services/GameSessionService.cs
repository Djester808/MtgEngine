using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Services;

/// <summary>One seat being asked for: the account that will hold it and the deck it brings.</summary>
public sealed record NewGameSeat(
    string UserId,
    string Name,
    IReadOnlyList<StartingCard> DeckCards,
    IReadOnlyList<StartingCard> CommandCards);

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
/// already playing — the pipeline writes <c>cards/</c> continuously — so new games persist
/// their admission decision and resume with the same coverage and definition snapshots.
/// </para>
/// </remarks>
public sealed class GameSessionService
{
    private readonly ConcurrentDictionary<Guid, GameSession> _live = new();
    private readonly IGameLogStore _store;
    private readonly ICardFilePool _pool;
    private readonly DeckGate _gate;
    private readonly GameDefinitionResolver _definitions;
    private readonly Func<ImmutableHashSet<string>, Subsystems> _subsystems;

    /// <param name="subsystems">
    /// The engine for a table, given the oracle ids the table enforces: the view says per card
    /// whether the engine plays it or the table does by hand, and only the admission knows.
    /// </param>
    public GameSessionService(IGameLogStore store, ICardFilePool pool, DeckGate gate, Func<ImmutableHashSet<string>, Subsystems>? subsystems = null,
        GameDefinitionResolver? definitions = null)
    {
        _store = store;
        _pool = pool;
        _gate = gate;
        _definitions = definitions ?? new GameDefinitionResolver(pool);
        _subsystems = subsystems ?? (enforced => Subsystems.Wave2(enforced.Contains));
    }

    /// <summary>
    /// Creates a table: every deck through the gate, then the engine, then the log stored.
    /// </summary>
    /// <exception cref="InvalidRequestException">A deck holds a card this table cannot play.</exception>
    public Task<GameSession> CreateAsync(NewGameRequest request, CancellationToken ct = default) =>
        CreateCoreAsync(request, Guid.NewGuid(), false, ct);

    /// <summary>A lobby's stable id is its game's id, so a retry cannot create another game.</summary>
    public Task<GameSession> CreateForLobbyAsync(Guid lobbyId, NewGameRequest request, CancellationToken ct = default) =>
        lobbyId == Guid.Empty ? throw new InvalidRequestException("Table id is required.") : CreateCoreAsync(request, lobbyId, true, ct);

    private async Task<GameSession> CreateCoreAsync(NewGameRequest request, Guid id, bool reuse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Seats.Count is < 2 or > 4)
            throw new InvalidRequestException("A game needs two to four seats.");
        if (request.Seats.Any(seat => string.IsNullOrWhiteSpace(seat.UserId) || string.IsNullOrWhiteSpace(seat.Name)))
            throw new InvalidRequestException("Every seat needs a signed-in account and player name.");
        if (request.Seats.Select(seat => seat.UserId).Distinct(StringComparer.Ordinal).Count() != request.Seats.Count)
            throw new InvalidRequestException("Each account may hold only one seat at a table.");
        if (reuse && await FindAsync(id, ct).ConfigureAwait(false) is { } existing)
            return SameTable(existing, request);

        var enforced = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in request.Seats)
        {
            var admission = _gate.Admit([.. seat.DeckCards.Concat(seat.CommandCards).Select(card => card.OracleId)], request.AllowHonourSystem);
            enforced.UnionWith(admission.EnforcedOracleIds);
        }

        ct.ThrowIfCancellationRequested();

        var definitions = await _definitions.ResolveAsync(
            request.Seats.SelectMany(seat => seat.DeckCards.Concat(seat.CommandCards)).Select(card => card.OracleId),
            request.AllowHonourSystem, ct).ConfigureAwait(false);

        var setup = new GameSetup(
            [.. request.Seats.Select(s => new PlayerStart(s.Name, [.. s.DeckCards], [.. s.CommandCards]))],
            request.Seed,
            request.StartingLife,
            request.Format,
            OfferMulligans: true);

        var enforcedIds = enforced.ToImmutable();
        var engine = new Game(_subsystems(enforcedIds), definitions);
        var created = engine.Create(setup);
        created = created with
        {
            Events = [.. created.Events.Select(e => e is GameStarted start ? start with { EnforcedOracleIds = enforcedIds } : e)],
        };

        var session = new GameSession(
            id, engine, created.State, created.Events,
            [.. request.Seats.Select(s => s.UserId)],
            enforcedIds,
            request.AllowHonourSystem);

        var stored = await _store.TryCreateAsync(
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

        if (!stored)
        {
            session.Dispose();
            var recovered = await FindAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidResourceStateException("The game could not be recovered. Try again.");
            return SameTable(recovered, request);
        }

        return _live.GetOrAdd(id, session);
    }

    private static GameSession SameTable(GameSession existing, NewGameRequest request)
    {
        if (!existing.Seats.SequenceEqual(request.Seats.Select(seat => seat.UserId))
            || !string.Equals(existing.State.Format, request.Format, StringComparison.OrdinalIgnoreCase)
            || existing.AllowHonourSystem != request.AllowHonourSystem)
            throw new InvalidResourceStateException("This table is already bound to a different game setup.");
        return existing;
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
        var enforcedIds = EnforcedIn(log);
        var session = new GameSession(
            id, new Game(_subsystems(enforcedIds), _pool), state, log, seats,
            enforcedIds, stored.AllowHonourSystem);

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
            && RulesCoverage.IsEnforced(obj, session.EnforcedOracleIds.Contains);
    }

    private Task PersistAsync(GameSession session, ImmutableList<GameEvent> appended, CancellationToken ct) =>
        _store.SaveLogAsync(session.Id, EventLogSerializer.Write(session.Log), session.Log.Count, session.Finished, ct);

    /// <summary>
    /// What a resumed game enforces, read from its admission snapshot.
    /// </summary>
    /// <remarks>
    /// Older logs did not record this decision and retain their historical reconstruction
    /// behavior. An explicitly empty snapshot means a manual table, not a missing snapshot.
    /// </remarks>
    private ImmutableHashSet<string> EnforcedIn(IEnumerable<GameEvent> log)
    {
        var started = log.OfType<GameStarted>().FirstOrDefault();
        if (started is null)
            return [];
        if (started.EnforcedOracleIds is { } admitted)
            return admitted.WithComparer(StringComparer.OrdinalIgnoreCase);

        return ImmutableHashSet.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            started.Players
                .SelectMany(p => p.DeckCards.Concat(p.CommandCards)).Select(card => card.OracleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => _pool.StatusOf(id).Status == ImplementationStatus.Verified));
    }
}
