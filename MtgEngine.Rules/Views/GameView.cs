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

    /// <summary>
    /// Where in the turn the game is (CR 500.1), as the step's name.
    /// </summary>
    /// <remarks>
    /// A client needs this to know when to offer an attack: declaring attackers is a turn-based
    /// action that happens before anyone has priority (CR 508.1), so "is it my turn and do I
    /// have priority" does not identify the moment.
    /// </remarks>
    public required string CurrentStep { get; init; }

    /// <summary>Attacking creature to the player it is attacking (CR 508.1b).</summary>
    public ImmutableDictionary<Guid, Guid> Attackers { get; init; } =
        ImmutableDictionary<Guid, Guid>.Empty;

    /// <summary>Each attacker and the creatures blocking it (CR 509.1g).</summary>
    public ImmutableDictionary<Guid, ImmutableList<Guid>> Blockers { get; init; } =
        ImmutableDictionary<Guid, ImmutableList<Guid>>.Empty;

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

    /// <summary>
    /// The decision the game is waiting on, or null.
    /// </summary>
    /// <remarks>
    /// Every player is told the game is waiting and on whom — a board that simply stops with no
    /// explanation is the worst thing a client can show. Only the player being asked is sent the
    /// options, because those can be hidden information: the list of cards to put on the bottom
    /// after a mulligan is that player's hand.
    /// </remarks>
    public ChoiceView? Choice { get; init; }
}

/// <summary>A decision the game is waiting on, as one player may see it.</summary>
public sealed record ChoiceView
{
    public required string Id { get; init; }

    /// <summary>Who has to answer. Everyone is told this much.</summary>
    public required Guid PlayerId { get; init; }

    public required string Kind { get; init; }

    public required string Prompt { get; init; }

    public int MinPicks { get; init; }

    public int MaxPicks { get; init; }

    /// <summary>True when the order of the picks is the answer (CR 603.3b, 616.1).</summary>
    public bool IsOrdering { get; init; }

    /// <summary>Populated only for the player being asked; null for everyone else.</summary>
    public ImmutableList<ChoiceOptionView>? Options { get; init; }
}

public sealed record ChoiceOptionView(string Id, string Label);

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

    /// <summary>
    /// Combat damage this player has taken from each commander, by the commander's name
    /// (CR 903.10a).
    /// </summary>
    /// <remarks>
    /// Public: everyone at the table needs to know how close a commander is to twenty-one,
    /// because it changes every block. Keyed by name rather than oracle id so a client can show
    /// it without another lookup.
    /// </remarks>
    public ImmutableDictionary<string, int> CommanderDamage { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>The name of this player's commander, if they have one (CR 903.3).</summary>
    public string? CommanderName { get; init; }

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
