using System.Collections.Immutable;

namespace MtgEngine.Rules.Views;

/// <summary>
/// What one player is allowed to know. The only shape that ever leaves the server.
/// </summary>
/// <remarks>
/// Hidden information is <b>absent</b> here, not flagged. There is no library list with a
/// "visible: false" beside it and no opponent hand with the names blanked, because both of those
/// designs put the secret in the payload and trust every future serializer, log line and debug
/// dump not to spill it. If a field is not on this record, no amount of client tampering
/// reveals it.
/// <para>
/// The engine this replaces sent one <c>GameState</c> to a SignalR group containing both
/// players, which handed each of them the other's hand and both libraries.
/// </para>
/// </remarks>
public sealed record GameView
{
    public required Guid GameId { get; init; }

    /// <summary>The player this view was built for. Every visibility decision was made for them.</summary>
    public required Guid Viewer { get; init; }

    public required int TurnNumber { get; init; }

    public required Guid ActivePlayerId { get; init; }

    /// <summary>In seating order, so the client can lay the table out consistently (CR 103.5).</summary>
    public ImmutableList<PlayerView> Players { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Battlefield { get; init; } = [];

    /// <summary>Public zone; index 0 is the top (CR 405.2).</summary>
    public ImmutableList<ObjectView> Stack { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Exile { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Command { get; init; } = [];
}

/// <summary>One player as the viewer sees them.</summary>
public sealed record PlayerView
{
    public required Guid PlayerId { get; init; }

    public required string Name { get; init; }

    public required int Life { get; init; }

    public int PoisonCounters { get; init; }

    /// <summary>
    /// A count and never a list. Any player may count any library at any time (CR 401.3) and no
    /// player may look at one (CR 401.2) — including their own.
    /// </summary>
    public required int LibraryCount { get; init; }

    /// <summary>Any player may count any hand (CR 402.3).</summary>
    public required int HandCount { get; init; }

    /// <summary>
    /// Populated only when this player is the viewer; null for everyone else (CR 402.3).
    /// </summary>
    public ImmutableList<ObjectView>? Hand { get; init; }

    /// <summary>Public zone, examinable by anyone at any time (CR 404.2).</summary>
    public ImmutableList<ObjectView> Graveyard { get; init; } = [];

    public bool HasLost { get; init; }

    /// <summary>Against the one-per-turn allowance (CR 305.2), so the client can grey the drop.</summary>
    public int LandsPlayedThisTurn { get; init; }
}

/// <summary>
/// One object, as much of it as the viewer may see.
/// </summary>
/// <remarks>
/// The characteristics here are the <em>printed</em> ones, and they are named that way on
/// purpose. Current power, toughness, types and abilities are the printed values with every
/// applicable continuous effect layered over them (CR 613), which is slice 4; until that exists
/// there is nothing to report and a field called <c>Power</c> would be a lie the moment the
/// first lord is implemented.
/// </remarks>
public sealed record ObjectView
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Lets the client fetch art and printings from the card database it already has.</summary>
    public required string OracleId { get; init; }

    public required Guid ControllerId { get; init; }

    public string? ManaCost { get; init; }

    public string? TypeLine { get; init; }

    public int? PrintedPower { get; init; }

    public int? PrintedToughness { get; init; }

    // ---- Battlefield status; null off the battlefield (CR 403.3) ------------------------

    public bool? IsTapped { get; init; }

    public bool? HasSummoningSickness { get; init; }

    public int? DamageMarked { get; init; }

    public IReadOnlyDictionary<string, int>? Counters { get; init; }
}
