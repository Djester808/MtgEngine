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
    private readonly GameCardArt _art;
    private readonly ILogger<GameHub> _logger;

    public GameHub(GameSessionService sessions, GameCardArt art, ILogger<GameHub> logger)
    {
        _sessions = sessions;
        _art = art;
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
        var session = await RequireAsync(gameId).ConfigureAwait(false);

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

    /// <param name="options">
    /// The cost choices made as the spell was cast — kicker, buyback, dash, morph, delve and the
    /// rest (CR 601.2b, 601.2f–h). Optional, and omitted entirely by a client that only ever
    /// casts a spell for its printed cost.
    /// </param>
    /// <remarks>
    /// Added as a trailing optional argument rather than by widening the parameter list, because
    /// a client that sends four arguments must keep working: SignalR fills the missing one with
    /// its default. Every one of these had been implemented, tested and unreachable from here for
    /// want of somewhere to put it.
    /// </remarks>
    public Task CastSpell(
        Guid gameId,
        Guid cardId,
        IReadOnlyList<TargetDto>? targets,
        int variableValue,
        CastOptionsDto? options)
    {
        var chosen = options ?? CastOptionsDto.Printed;

        return ActAsync(gameId, (game, me) =>
        {
            // Inside the action rather than before it, so that an over-long list comes back as a
            // "Refused" like every other answer the rules give, instead of as a server fault.
            chosen.Validate();

            game.CastSpell(
                me,
                new ObjectId(cardId),
                ToTargets(targets),
                variableValue,
                Ids(chosen.TapToPay),
                chosen.Modes,
                chosen.Kicked,
                Ids(chosen.CostPayment),
                chosen.FaceDown,
                Ids(chosen.Delve),
                chosen.Buyback,
                chosen.Dashed,
                chosen.Evoked,
                chosen.Overloaded,
                chosen.Conspired,
                chosen.Bestowed,
                chosen.Replicated,
                chosen.Offspring,
                chosen.Entwined,
                Ids(chosen.Spliced),
                chosen.AssistPlayer is { } helper ? (helper, chosen.AssistAmount) : null,
                chosen.Squad,
                chosen.DamageDivision,
                chosen.AlternativeCost,
                chosen.Blitzed,
                chosen.Multikicked,
                chosen.Bargained,
                chosen.AsAdventure,
                chosen.Half,
                chosen.Fused,
                chosen.Prepared,
                chosen.WithFlash,
                chosen.Prototyped,
                chosen.Mutated,
                chosen.MutateOnTop);
        });
    }

    /// <param name="variableValue">
    /// The number named for a cost that asks for one — "remove any number of storage counters"
    /// (CR 601.2b). Zero for every ability that does not ask, and the client must send it: a hub
    /// method binds by position and no C# default is applied to an argument left off.
    /// </param>
    public Task ActivateAbility(
        Guid gameId,
        Guid sourceId,
        string abilityId,
        IReadOnlyList<TargetDto>? targets,
        int variableValue) =>
        ActAsync(gameId, (game, me) =>
            game.ActivateAbility(
                me, new ObjectId(sourceId), abilityId, ToTargets(targets), null, variableValue));

    /// <param name="attackers">
    /// Attacking creature id to what it attacks: the defending player, and optionally one of
    /// their planeswalkers (CR 508.1b).
    /// </param>
    public Task DeclareAttackers(Guid gameId, IReadOnlyDictionary<Guid, AttackDto> attackers) =>
        ActAsync(gameId, (game, me) => game.DeclareAttackers(
            me,
            attackers.ToDictionary(
                kv => new ObjectId(kv.Key),
                kv => kv.Value.Planeswalker is { } walker
                    ? AttackTarget.At(kv.Value.DefendingPlayer, new ObjectId(walker))
                    : AttackTarget.Player(kv.Value.DefendingPlayer))));

    public Task DeclareBlockers(Guid gameId, IReadOnlyDictionary<Guid, Guid[]> blocks) =>
        ActAsync(gameId, (game, me) => game.DeclareBlockers(
            me,
            blocks.ToDictionary(
                kv => new ObjectId(kv.Key),
                kv => (IReadOnlyList<ObjectId>)[.. kv.Value.Select(id => new ObjectId(id))])));

    public Task Discard(Guid gameId, Guid cardId) =>
        ActAsync(gameId, (game, me) => game.Discard(me, new ObjectId(cardId)));

    /// <summary>
    /// Concedes, leaving the game immediately (CR 104.3a).
    /// </summary>
    /// <remarks>
    /// The one action that needs no priority and no timing — a player may concede at any point,
    /// including while something is resolving and while the game is waiting on somebody else.
    /// Who is conceding comes from the token, not the message, so nobody can concede for
    /// anybody else.
    /// </remarks>
    public Task Concede(Guid gameId) => ActAsync(gameId, (game, me) => game.Concede(me));

    /// <summary>
    /// Answers the decision the game is waiting on (CR 103.5, 603.3b, 616.1, 704.5j).
    /// </summary>
    /// <remarks>
    /// The engine checks that the caller is the player being asked; the hub only has to say who
    /// is calling, which it takes from the token rather than the message.
    /// </remarks>
    public Task Choose(Guid gameId, IReadOnlyList<string> picks) =>
        ActAsync(gameId, (game, me) => game.Choose(me, picks ?? []));

    // ---- Plumbing --------------------------------------------------------------------------

    private async Task<GameSession> RequireAsync(Guid gameId)
    {
        var session = await _sessions.FindAsync(gameId, Context.ConnectionAborted).ConfigureAwait(false)
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
        var session = await RequireAsync(gameId).ConfigureAwait(false);
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

        // The log stores no art on purpose, so a resumed game has none until it is put back.
        view = await _art.FillAsync(view, Context.ConnectionAborted).ConfigureAwait(false);

        await Clients.User(playerId.ToString()).SendAsync("State", view).ConfigureAwait(false);
    }

    private static List<Target>? ToTargets(IReadOnlyList<TargetDto>? targets) =>
        targets is null ? null : [.. targets.Select(t => t.ToTarget())];

    private static List<ObjectId>? Ids(IReadOnlyList<Guid>? ids) =>
        ids is null ? null : [.. ids.Select(id => new ObjectId(id))];
}

/// <summary>The cost choices a client made while casting (CR 601.2b, 601.2f-h).</summary>
/// <remarks>
/// Ids only, like <see cref="TargetDto"/>: the server decides whether each choice is legal, so
/// naming a card that cannot be delved or a permanent that cannot be tapped gains nothing.
/// </remarks>
/// <summary>
/// Everything CR 601.2b asks a caster to choose, as a client sends it.
/// </summary>
/// <remarks>
/// <see cref="GameHub.CastSpell"/> takes this as a required parameter, and it has to stay
/// required: SignalR binds hub arguments by position and does <em>not</em> fill in a C# default
/// for one the caller left off. A trailing <c>= null</c> here reads as "old clients keep
/// working" and means "every cast fails with 'Failed to invoke CastSpell due to an error on the
/// server'" — which is exactly what happened, and what the in-process hub tests could not see,
/// because a C# caller does apply the default.
/// </remarks>
public sealed record CastOptionsDto(
    IReadOnlyList<Guid>? TapToPay = null,
    IReadOnlyList<int>? Modes = null,
    bool Kicked = false,
    IReadOnlyList<Guid>? CostPayment = null,
    bool FaceDown = false,
    IReadOnlyList<Guid>? Delve = null,
    bool Buyback = false,
    bool Dashed = false,
    bool Evoked = false,
    bool Overloaded = false,
    bool Conspired = false,
    bool Bestowed = false,
    int Replicated = 0,
    bool Offspring = false,
    bool Entwined = false,
    IReadOnlyList<Guid>? Spliced = null,
    Guid? AssistPlayer = null,
    int AssistAmount = 0,
    int Squad = 0,
    IReadOnlyList<int>? DamageDivision = null,
    bool AlternativeCost = false,
    bool Blitzed = false,
    int Multikicked = 0,

    /// <summary>Whether an additional cost was paid to bargain the spell (CR 702.166a).</summary>
    bool Bargained = false,

    /// <summary>Whether the Adventure half is being cast rather than the creature (CR 715.3).</summary>
    bool AsAdventure = false,

    /// <summary>Which half of a split card is being cast, or zero for the whole (CR 709.4).</summary>
    int Half = 0,

    /// <summary>Whether both halves are cast together as one spell (CR 702.102a).</summary>
    bool Fused = false,

    /// <summary>Whether a prepared permanent's spell is being copied and cast (CR 722.3c).</summary>
    bool Prepared = false,

    /// <summary>Whether the card's own offer of instant timing is being paid for.</summary>
    bool WithFlash = false,

    /// <summary>Whether a prototype card is being cast for its smaller cost (CR 718.3).</summary>
    bool Prototyped = false,

    /// <summary>
    /// Whether the mutate cost is being paid, making this a mutating creature spell
    /// (CR 702.140a).
    /// </summary>
    /// <remarks>
    /// The creature it merges with is an ordinary target and arrives in <c>targets</c> like any
    /// other, after whatever the card itself targets.
    /// </remarks>
    bool Mutated = false,

    /// <summary>
    /// Whether a mutating creature spell goes over the creature rather than under it
    /// (CR 702.140c).
    /// </summary>
    /// <remarks>
    /// The choice that decides what the permanent <em>is</em> afterwards: over, and it takes the
    /// new card's name, size and types; under, and it keeps its own and gains only the abilities.
    /// Ignored unless <see cref="Mutated"/> is set.
    /// </remarks>
    bool MutateOnTop = true)
{
    /// <summary>A spell cast for exactly what is printed on it, choosing nothing.</summary>
    public static readonly CastOptionsDto Printed = new();

    /// <summary>
    /// The most cards any one of these lists may name.
    /// </summary>
    /// <remarks>
    /// A hub method is not a controller and DataAnnotations do not run on it, so the cap is
    /// checked by hand. Well above any real cast — nothing taps sixty-four permanents — and low
    /// enough that a caller cannot hand the engine an unbounded list to walk.
    /// </remarks>
    public const int MaxChoices = 64;

    /// <summary>Throws if any list is longer than a cast could possibly need.</summary>
    public void Validate()
    {
        if (TapToPay?.Count > MaxChoices
            || Modes?.Count > MaxChoices
            || CostPayment?.Count > MaxChoices
            || Delve?.Count > MaxChoices
            || Spliced?.Count > MaxChoices
            || DamageDivision?.Count > MaxChoices)
        {
            throw new InvalidOperationException(
                $"A cast may not name more than {MaxChoices} cards for any one cost.");
        }

        // Assist is an amount of generic mana, not a list, so the cap that guards the lists says
        // nothing about it. The engine clamps it to what the spell actually costs; this only
        // stops a caller handing it a number that is not an amount of mana at all.
        if (Squad is < 0 or > MaxChoices)
        {
            throw new InvalidOperationException(
                $"Squad may not be paid more than {MaxChoices} times.");
        }

        if (AssistAmount is < 0 or > MaxChoices)
        {
            throw new InvalidOperationException(
                $"Assist may not offer more than {MaxChoices} mana.");
        }

        // The engine checks the division against what the spell actually deals (CR 601.2d); this
        // only stops a caller handing it numbers that are not amounts of damage. Negative ones
        // would otherwise let a division sum to the right total while healing a target.
        if (DamageDivision?.Any(amount => amount is < 0 or > MaxChoices) == true)
        {
            throw new InvalidOperationException(
                $"Damage divided among targets must be between 0 and {MaxChoices} each.");
        }
    }
}

/// <summary>What a creature is attacking, as a client names it (CR 508.1b).</summary>
public sealed record AttackDto(Guid DefendingPlayer, Guid? Planeswalker);

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
