using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MtgEngine.Api.Services;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Hubs;

/// <summary>
/// Real-time play: players send actions, the server decides, everyone gets their own view.
/// </summary>
/// <remarks>
/// The rule this hub exists to keep: <b>each player is sent a view built for them, never the
/// game state.</b> The engine's previous hub broadcast one state object to a SignalR group
/// containing every player at the table, which handed each of them the other's hand and both
/// libraries. Here every push goes through <see cref="GameSession.ReadAsync"/>, which projects
/// per player, and the group is used only to know who to push to.
/// <para>
/// The server is authoritative in the strong sense: a client sends an intent, and if the rules
/// refuse it, nothing happens and only that client hears why. There is no client-side rules
/// check to disagree with.
/// </para>
/// </remarks>
[Authorize]
public sealed class GameHub : Hub
{
    private readonly GameSessionService _sessions;
    private readonly ILogger<GameHub> _logger;

    public GameHub(GameSessionService sessions, ILogger<GameHub> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>The signed-in player. Taken from the token, never from the message.</summary>
    /// <remarks>
    /// A client that could name its own player id could act as its opponent, which is a cheat
    /// rather than a bug, so the id comes from the authenticated principal and the payloads have
    /// nowhere to put one.
    /// </remarks>
    private Guid PlayerId =>
        Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new HubException("Not signed in.");

    private static string Group(Guid gameId) => $"game:{gameId:N}";

    // ---- Joining ---------------------------------------------------------------------------

    /// <summary>Joins a game the caller is seated at, and receives their view.</summary>
    public async Task Join(Guid gameId)
    {
        var session = Require(gameId);

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(gameId)).ConfigureAwait(false);
        await SendViewAsync(session, PlayerId).ConfigureAwait(false);
        await Clients.Caller.SendAsync("Log", await session.LogAsync().ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>Leaves the group. The seat is kept, so the player can come back.</summary>
    public Task Leave(Guid gameId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(gameId));

    // ---- Actions ---------------------------------------------------------------------------

    public Task PassPriority(Guid gameId) =>
        ActAsync(gameId, (game, me) => game.PassPriority(me));

    public Task PlayLand(Guid gameId, Guid cardId) =>
        ActAsync(gameId, (game, me) => game.PlayLand(me, new ObjectId(cardId)));

    public Task CastSpell(Guid gameId, Guid cardId, IReadOnlyList<TargetDto>? targets, int variableValue) =>
        ActAsync(gameId, (game, me) =>
            game.CastSpell(me, new ObjectId(cardId), ToTargets(targets), variableValue));

    public Task ActivateAbility(
        Guid gameId, Guid sourceId, string abilityId, IReadOnlyList<TargetDto>? targets) =>
        ActAsync(gameId, (game, me) =>
            game.ActivateAbility(me, new ObjectId(sourceId), abilityId, ToTargets(targets)));

    public Task DeclareAttackers(Guid gameId, IReadOnlyDictionary<Guid, Guid> attackers) =>
        ActAsync(gameId, (game, me) => game.DeclareAttackers(
            me, attackers.ToDictionary(kv => new ObjectId(kv.Key), kv => kv.Value)));

    public Task DeclareBlockers(Guid gameId, IReadOnlyDictionary<Guid, Guid[]> blocks) =>
        ActAsync(gameId, (game, me) => game.DeclareBlockers(
            me,
            blocks.ToDictionary(
                kv => new ObjectId(kv.Key),
                kv => (IReadOnlyList<ObjectId>)[.. kv.Value.Select(id => new ObjectId(id))])));

    public Task Discard(Guid gameId, Guid cardId) =>
        ActAsync(gameId, (game, me) => game.Discard(me, new ObjectId(cardId)));

    // ---- Plumbing --------------------------------------------------------------------------

    private GameSession Require(Guid gameId)
    {
        var session = _sessions.Find(gameId)
            ?? throw new HubException("That game is not running.");

        // Seat membership is the authorisation. Being signed in is not enough to act at a table
        // you are not sitting at, or to see a view built for someone else.
        if (!session.SeatNames.ContainsKey(PlayerId))
            throw new HubException("You are not seated at that game.");

        return session;
    }

    /// <summary>
    /// Runs an action, then pushes every seated player their own updated view.
    /// </summary>
    /// <remarks>
    /// An action the rules refuse is reported to the caller alone and changes nothing — the
    /// engine throws before emitting an event, so there is no half-applied state to unwind.
    /// </remarks>
    private async Task ActAsync(Guid gameId, Action<Rules.Engine.Game, Guid> action)
    {
        var session = Require(gameId);
        var me = PlayerId;

        try
        {
            await session.MutateAsync(game =>
            {
                action(game, me);
                return true;
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The rules said no. That is a normal answer, not a server fault, and only the
            // player who tried it hears about it.
            _logger.LogDebug(ex, "Refused action in game {GameId}", gameId);
            await Clients.Caller.SendAsync("Refused", ex.Message).ConfigureAwait(false);
            return;
        }

        await BroadcastAsync(session).ConfigureAwait(false);
    }

    /// <summary>Pushes each seated player the view built for them, and the shared log.</summary>
    private async Task BroadcastAsync(GameSession session)
    {
        var log = await session.LogAsync().ConfigureAwait(false);

        foreach (var playerId in session.SeatNames.Keys)
            await SendViewAsync(session, playerId).ConfigureAwait(false);

        await Clients.Group(Group(session.GameId)).SendAsync("Log", log).ConfigureAwait(false);
    }

    private async Task SendViewAsync(GameSession session, Guid playerId)
    {
        var view = await session.ReadAsync(playerId).ConfigureAwait(false);
        await Clients.User(playerId.ToString()).SendAsync("State", view).ConfigureAwait(false);
    }

    private static List<Target>? ToTargets(IReadOnlyList<TargetDto>? targets) =>
        targets is null ? null : [.. targets.Select(t => t.ToTarget())];
}

/// <summary>A target as a client names it (CR 115.1).</summary>
/// <remarks>
/// Ids only. The server looks up what they refer to and checks the target is legal, so a client
/// naming a card it cannot see gains nothing: the object either fails the target's filter or is
/// not in the zone the spell requires.
/// </remarks>
public sealed record TargetDto(string Kind, Guid? ObjectId, Guid? PlayerId)
{
    public Target ToTarget() => Kind switch
    {
        "player" => Target.ToPlayer(PlayerId ?? Guid.Empty),
        "spell" => Target.ToSpell(new ObjectId(ObjectId ?? Guid.Empty)),
        "card" => Target.ToCard(new ObjectId(ObjectId ?? Guid.Empty)),
        _ => Target.ToPermanent(new ObjectId(ObjectId ?? Guid.Empty)),
    };
}
