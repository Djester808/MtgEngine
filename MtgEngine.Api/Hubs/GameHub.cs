using Microsoft.AspNetCore.SignalR;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
using MtgEngine.Rules;

namespace MtgEngine.Api.Hubs;

/// <summary>
/// SignalR hub for real-time game play.
/// All game-mutating actions flow through here.
///
/// Connection groups: one group per game ID ("game:{gameId}").
/// Each player connection is added to their game's group on JoinGame.
/// State diffs are broadcast to the whole group after each action.
/// </summary>
public sealed class GameHub : Hub
{
    private readonly GameSessionService _sessions;
    private readonly ILogger<GameHub> _logger;

    // Track connectionId -> (gameId, playerId) for cleanup on disconnect
    private static readonly Dictionary<string, (Guid GameId, Guid PlayerId)> _connectionMap = [];
    private static readonly SemaphoreSlim _mapLock = new(1, 1);

    public GameHub(GameSessionService sessions, ILogger<GameHub> logger)
    {
        _sessions = sessions;
        _logger   = logger;
    }

    // ---- Connection lifecycle ----------------------------

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _mapLock.WaitAsync();
        try { _connectionMap.Remove(Context.ConnectionId); }
        finally { _mapLock.Release(); }
        await base.OnDisconnectedAsync(exception);
    }

    // ---- Client -> Server methods -------------------------

    /// <summary>Join a game group. Must be called before any game actions.</summary>
    public async Task JoinGame(string gameId, string playerToken)
    {
        if (!Guid.TryParse(gameId, out var gid)) { await Error("Invalid game ID."); return; }

        var session = _sessions.Get(gid);
        if (session is null) { await Error("Game not found."); return; }

        if (!session.TryResolveToken(playerToken, out var playerId))
        { await Error("Invalid player token."); return; }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(gid));

        await _mapLock.WaitAsync();
        try { _connectionMap[Context.ConnectionId] = (gid, playerId); }
        finally { _mapLock.Release(); }

        // Send full state snapshot to joining player
        var snapshot = DomainMapper.ToDto(session.State, playerId);
        await Clients.Caller.SendAsync("GameStateSnapshot", snapshot);

        _logger.LogDebug("Player {PlayerId} joined game {GameId}", playerId, gid);
    }

    /// <summary>Re-sync full state (called after reconnect).</summary>
    public async Task RequestStateSync()
    {
        var (gid, playerId) = await GetContext();
        if (gid == Guid.Empty) return;

        var session = _sessions.GetOrThrow(gid);
        var snapshot = DomainMapper.ToDto(session.State, playerId);
        await Clients.Caller.SendAsync("GameStateSnapshot", snapshot);
    }

    // ---- Game actions (each follows the same pattern):
    //      1. Resolve caller identity
    //      2. Apply rules action under session lock
    //      3. Broadcast diff to game group

    public async Task PassPriority()
    {
        await ApplyAction(state => GameEngine.PassPriority(state, GetCallerId()));
    }

    public async Task PlayLand(string cardId)
    {
        if (!Guid.TryParse(cardId, out var cid)) { await Error("Invalid card ID."); return; }
        await ApplyAction(state => GameEngine.PlayLand(state, GetCallerId(), cid));
    }

    public async Task CastSpell(string cardId, string[] targetIds)
    {
        if (!Guid.TryParse(cardId, out var cid)) { await Error("Invalid card ID."); return; }
        var targets = targetIds
            .Where(t => Guid.TryParse(t, out _))
            .Select(t => new Domain.Models.Target
            {
                Type = Domain.Models.TargetType.Permanent,
                Id   = Guid.Parse(t),
            })
            .ToList();
        await ApplyAction(state => GameEngine.CastSpell(state, GetCallerId(), cid, targets));
    }

    public async Task ActivateMana(string permanentId)
    {
        if (!Guid.TryParse(permanentId, out var pid)) { await Error("Invalid permanent ID."); return; }
        await ApplyAction(state => GameEngine.ActivateMana(state, GetCallerId(), pid));
    }

    public async Task DeclareAttackers(string[] attackerIds)
    {
        var ids = ParseGuids(attackerIds);
        await ApplyAction(state => GameEngine.DeclareAttackers(state, GetCallerId(), ids));
    }

    public async Task DeclareBlockers(Dictionary<string, string> blockerToAttacker)
    {
        var parsed = blockerToAttacker
            .Where(kv => Guid.TryParse(kv.Key, out _) && Guid.TryParse(kv.Value, out _))
            .ToDictionary(kv => Guid.Parse(kv.Key), kv => Guid.Parse(kv.Value));
        await ApplyAction(state => GameEngine.DeclareBlockers(state, GetCallerId(), parsed));
    }

    public async Task SetBlockerOrder(string attackerId, string[] orderedBlockerIds)
    {
        if (!Guid.TryParse(attackerId, out var aid)) { await Error("Invalid attacker ID."); return; }
        var ids = ParseGuids(orderedBlockerIds);
        await ApplyAction(state => GameEngine.SetBlockerOrder(state, GetCallerId(), aid, ids));
    }

    public async Task Concede()
    {
        // TODO: implement concede in GameEngine
        await Error("Concede not yet implemented.");
    }

    // ---- Plumbing -----------------------------------------

    private async Task ApplyAction(Func<Domain.Models.GameState, Domain.Models.GameState> action)
    {
        var (gid, playerId) = await GetContext();
        if (gid == Guid.Empty) return;

        var session = _sessions.GetOrThrow(gid);

        try
        {
            var (before, after) = await session.ApplyAsync(action);

            // Send each player their own personalised diff
            var group = Clients.Group(GroupName(gid));
            var diff  = DomainMapper.ToDiff(before, after, playerId);
            await group.SendAsync("GameStateDiff", diff);
        }
        catch (InvalidOperationException ex)
        {
            await Error(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in game {GameId}", gid);
            await Error("An unexpected error occurred.");
        }
    }

    private Guid GetCallerId()
    {
        _connectionMap.TryGetValue(Context.ConnectionId, out var entry);
        return entry.PlayerId;
    }

    private async Task<(Guid GameId, Guid PlayerId)> GetContext()
    {
        await _mapLock.WaitAsync();
        try
        {
            if (_connectionMap.TryGetValue(Context.ConnectionId, out var entry))
                return entry;
        }
        finally { _mapLock.Release(); }

        await Error("Not joined to a game. Call JoinGame first.");
        return (Guid.Empty, Guid.Empty);
    }

    private async Task Error(string message) =>
        await Clients.Caller.SendAsync("Error", message);

    private static string GroupName(Guid gameId) => $"game:{gameId}";

    private static List<Guid> ParseGuids(string[] ids) =>
        ids.Where(id => Guid.TryParse(id, out _)).Select(Guid.Parse).ToList();
}
