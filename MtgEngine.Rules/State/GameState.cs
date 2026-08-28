using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>
/// The whole game at one instant. Immutable: every event folds into a new one.
/// </summary>
/// <remarks>
/// This type is never sent to a client. It contains every library and every hand, and a player
/// is entitled to see neither (CR 400.2). <see cref="Views.PlayerViewProjector"/> builds the
/// per-player payload; the previous engine skipped that step and broadcast state to the whole
/// SignalR group.
/// </remarks>
public sealed record GameState
{
    public required Guid GameId { get; init; }

    /// <summary>Every object in the game, in every zone, by its current identity.</summary>
    public ImmutableDictionary<ObjectId, GameObject> Objects { get; init; } =
        ImmutableDictionary<ObjectId, GameObject>.Empty;

    /// <summary>
    /// Seating order (CR 103.5), which fixes turn order and therefore APNAP order (CR 101.4).
    /// </summary>
    /// <remarks>
    /// Every "who is next" question in the engine is answered from this list, so that none of
    /// them assume two players. The previous engine asked <c>OpponentOf(playerId)</c>, which is
    /// only meaningful in a duel and cannot be corrected without rewriting priority.
    /// </remarks>
    public ImmutableList<Guid> TurnOrder { get; init; } = [];

    public ImmutableDictionary<Guid, PlayerState> Players { get; init; } =
        ImmutableDictionary<Guid, PlayerState>.Empty;

    /// <summary>Shared, and unordered — permanents may be arranged however players like (CR 400.5).</summary>
    public ImmutableList<ObjectId> Battlefield { get; init; } = [];

    /// <summary>
    /// State-triggered abilities whose condition is currently true and that have already fired
    /// for it (CR 603.8).
    /// </summary>
    /// <remarks>
    /// In the state rather than in the engine because the state is a fold of the log and has to
    /// stay one: an engine-side set would be rebuilt empty on replay, and every state trigger in
    /// the game would fire a second time. Keyed by source and ability, as a string, so the set is
    /// something a log can carry and compare.
    /// </remarks>
    public ImmutableHashSet<string> ArmedStateTriggers { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>Shared. Top of the stack is index 0 (CR 405.2).</summary>
    public ImmutableList<ObjectId> Stack { get; init; } = [];

    /// <summary>Shared (CR 406).</summary>
    public ImmutableList<ObjectId> Exile { get; init; } = [];

    /// <summary>Shared (CR 408).</summary>
    public ImmutableList<ObjectId> Command { get; init; } = [];

    /// <summary>
    /// The next timestamp to hand out (CR 613.7). Monotonic, and never derived from a clock —
    /// see <see cref="GameObject.Timestamp"/>.
    /// </summary>
    public long NextTimestamp { get; init; } = 1;

    /// <summary>CR 102.1. The first turn is turn 1.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Whose turn it is (CR 102.1).</summary>
    public Guid ActivePlayerId { get; init; }

    /// <summary>
    /// Turns waiting to be taken out of turn order, oldest first (CR 500.7).
    /// </summary>
    /// <remarks>
    /// A queue rather than a count, because the extra turns are not always the current player's:
    /// "target player takes an extra turn after this one" hands one to somebody else, and the
    /// order matters when several are created before any is taken.
    /// <para>
    /// The rule says the last one created is taken first, so this is written to from the front.
    /// Two extra turns created by two spells are taken in the reverse of the order they were
    /// made, which is what a player casting the second one is paying for.
    /// </para>
    /// </remarks>
    public ImmutableList<Guid> ExtraTurns { get; init; } = [];

    /// <summary>Where in the turn the game is (CR 500.1).</summary>
    public TurnStep CurrentStep { get; init; } = TurnStep.Untap;

    /// <summary>Set once the game has ended (CR 104.2). Nothing more may happen.</summary>
    public bool IsOver { get; init; }

    /// <summary>Who won, if anyone. Null while the game runs, and null for a draw.</summary>
    public Guid? WinnerId { get; init; }

    /// <summary>Who may act, and who has passed since anything last happened (CR 117).</summary>
    public PriorityState Priority { get; init; } = new();

    /// <summary>
    /// Abilities that have triggered and are waiting to go on the stack (CR 603.3).
    /// </summary>
    public ImmutableList<PendingTrigger> PendingTriggers { get; init; } = [];

    /// <summary>
    /// Delayed triggered abilities waiting for their moment (CR 603.7).
    /// </summary>
    /// <remarks>
    /// In the state rather than in a field on the game for the usual reason: a game rebuilt from
    /// its log has to be waiting for the same things. A permanent that is going to be sacrificed
    /// at end of turn is part of what the game *is*.
    /// </remarks>
    public ImmutableList<DelayedTrigger> Delayed { get; init; } = [];

    /// <summary>
    /// Continuous effects created by resolved spells and abilities (CR 611.2, 613.7b).
    /// </summary>
    /// <remarks>
    /// Static abilities are deliberately absent: their effects are recomputed from the
    /// battlefield every time, so that one leaving takes its effect with it.
    /// </remarks>
    public ImmutableList<FloatingEffect> FloatingEffects { get; init; } = [];

    /// <summary>Whether a creature has gone to a graveyard from the battlefield this turn.</summary>
    /// <remarks>
    /// Morbid, and the several later words for the same question. Read off the printed card
    /// rather than the computed characteristics, because the reducer has no ability source and a
    /// permanent that stopped being a creature on its way out is a rarity the fold cannot see
    /// either way.
    /// </remarks>
    public bool CreatureDiedThisTurn { get; init; }

    /// <summary>
    /// Who is the monarch, if anyone (CR 725.1).
    /// </summary>
    /// <remarks>
    /// On the state rather than on a player because only one player can hold it (CR 725.3), and
    /// a flag per player would let the fold produce two. There is no monarch until an effect
    /// makes one, which is what the null means.
    /// </remarks>
    public Guid? MonarchId { get; init; }

    /// <summary>
    /// Whether it is day, night, or neither (CR 731.1).
    /// </summary>
    /// <remarks>
    /// Null is "neither", which is where every game starts and where it stays until something
    /// makes it one or the other - and once it has become one, it is always one of the two from
    /// then on. A designation the game itself has rather than a player, so it sits beside the
    /// monarch rather than on a <see cref="PlayerState"/>.
    /// </remarks>
    public bool? IsDay { get; init; }

    /// <summary>Who was the active player before this turn, if anyone was (CR 502.2).</summary>
    /// <remarks>
    /// The day-night check asks what the *previous* turn's active player did, and by the time it
    /// is asked the turn has already changed. Recorded as the turn changes rather than worked out
    /// from the turn order, which would be wrong the moment a player leaves or an extra turn is
    /// taken.
    /// </remarks>
    public Guid? PreviousActivePlayerId { get; init; }

    /// <summary>Who is attacking whom (CR 506–511). Reset when the combat phase ends.</summary>
    public CombatState Combat { get; init; } = new();

    /// <summary>
    /// A decision the game is waiting on, or null. Nothing may happen while one is outstanding.
    /// </summary>
    public PendingChoice? Choice { get; init; }

    /// <summary>
    /// Mulligans taken so far, per player (CR 103.5). Decides how many cards go on the bottom.
    /// </summary>
    public ImmutableDictionary<Guid, int> MulligansTaken { get; init; } =
        ImmutableDictionary<Guid, int>.Empty;

    /// <summary>True while the opening hands are still being settled (CR 103.5).</summary>
    public bool IsMulliganing { get; init; }

    /// <summary>
    /// Set once the game has been dealt and the first turn has begun. Until then there is no
    /// turn and no priority, only seats and libraries.
    /// </summary>
    public bool HasBegun => TurnNumber > 0;

    // ---- Lookups ------------------------------------------------------------------------

    public GameObject GetObject(ObjectId id) =>
        Objects.TryGetValue(id, out var obj)
            ? obj
            : throw new InvalidOperationException($"No object {id} in the game.");

    public bool TryGetObject(ObjectId id, out GameObject obj) => Objects.TryGetValue(id, out obj!);

    public PlayerState GetPlayer(Guid playerId) =>
        Players.TryGetValue(playerId, out var player)
            ? player
            : throw new InvalidOperationException($"No player {playerId} in the game.");

    /// <summary>
    /// The contents of a zone, in order. Per-player zones need an owner; shared zones ignore it.
    /// </summary>
    public ImmutableList<ObjectId> Contents(Zone zone, Guid? playerId = null)
    {
        if (zone.IsPerPlayer())
        {
            if (playerId is null)
                throw new ArgumentNullException(
                    nameof(playerId), $"{zone} belongs to a player; say which one.");

            var player = GetPlayer(playerId.Value);
            return zone switch
            {
                Zone.Library => player.Library,
                Zone.Hand => player.Hand,
                Zone.Graveyard => player.Graveyard,
                _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
            };
        }

        return zone switch
        {
            Zone.Battlefield => Battlefield,
            Zone.Stack => Stack,
            Zone.Exile => Exile,
            Zone.Command => Command,
            _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
        };
    }

    /// <summary>
    /// Every player, starting with the given one and following turn order — the order priority
    /// passes in (CR 117.3d) and the order simultaneous objects go on the stack in (CR 101.4).
    /// </summary>
    public IEnumerable<Guid> PlayersFrom(Guid first)
    {
        var start = TurnOrder.IndexOf(first);
        if (start < 0)
            throw new InvalidOperationException($"Player {first} is not seated in this game.");

        for (var i = 0; i < TurnOrder.Count; i++)
            yield return TurnOrder[(start + i) % TurnOrder.Count];
    }

    /// <summary>
    /// Turn order starting from the active player: APNAP, the order the rules resolve nearly
    /// every simultaneous choice in (CR 101.4).
    /// </summary>
    public IEnumerable<Guid> ApnapOrder() => PlayersFrom(ActivePlayerId);

    /// <summary>Players still in the game (CR 104.2 losers are out but stay seated for the log).</summary>
    public IEnumerable<Guid> ActivePlayers() => TurnOrder.Where(id => !GetPlayer(id).HasLost);

    /// <summary>
    /// The next player in turn order who is still in the game, skipping anyone who has lost.
    /// </summary>
    public Guid NextInTurnOrderAfter(Guid playerId) =>
        PlayersFrom(playerId).Skip(1).FirstOrDefault(id => !GetPlayer(id).HasLost, playerId);

    /// <summary>Whether the game is waiting on somebody to decide something.</summary>
    public bool IsWaitingForChoice => Choice is not null;

    /// <summary>
    /// Whether the given player could cast a sorcery right now: their main phase, an empty
    /// stack, and priority (CR 117.1a, 505.6a).
    /// </summary>
    public bool IsSorcerySpeedFor(Guid playerId) =>
        HasBegun &&
        Priority.Holder == playerId &&
        ActivePlayerId == playerId &&
        CurrentStep.IsMainPhase() &&
        Stack.IsEmpty;

    // ---- Small edits used by the reducer ------------------------------------------------

    public GameState WithObject(GameObject obj) =>
        this with { Objects = Objects.SetItem(obj.Id, obj) };

    public GameState WithPlayer(PlayerState player) =>
        this with { Players = Players.SetItem(player.PlayerId, player) };

    // ---- Equality ------------------------------------------------------------------------

    /// <summary>
    /// Value equality over every zone and object, not the reference comparison a record would
    /// generate for its collections (see <see cref="Structural"/>).
    /// </summary>
    /// <remarks>
    /// Two states are equal when they describe the same game position. This is what
    /// <c>Replay(log) == state</c> asserts, so it has to mean what it appears to mean.
    /// </remarks>
    public bool Equals(GameState? other) =>
        other is not null &&
        GameId == other.GameId &&
        TurnNumber == other.TurnNumber &&
        ActivePlayerId == other.ActivePlayerId &&
        CurrentStep == other.CurrentStep &&
        IsOver == other.IsOver &&
        WinnerId == other.WinnerId &&
        Priority == other.Priority &&
        NextTimestamp == other.NextTimestamp &&
        Structural.Same(TurnOrder, other.TurnOrder) &&
        Structural.Same(ExtraTurns, other.ExtraTurns) &&
        Structural.Same(Battlefield, other.Battlefield) &&
        Structural.Same(Stack, other.Stack) &&
        Structural.Same(Exile, other.Exile) &&
        Structural.Same(Command, other.Command) &&
        Structural.Same(Players, other.Players) &&
        Structural.Same(PendingTriggers, other.PendingTriggers) &&
        Structural.Same(FloatingEffects, other.FloatingEffects) &&
        CreatureDiedThisTurn == other.CreatureDiedThisTurn &&
        MonarchId == other.MonarchId &&
        IsDay == other.IsDay &&
        PreviousActivePlayerId == other.PreviousActivePlayerId &&
        Combat == other.Combat &&
        Choice == other.Choice &&
        IsMulliganing == other.IsMulliganing &&
        Structural.Same(MulligansTaken, other.MulligansTaken) &&
        Structural.Same(Objects, other.Objects);

    public override int GetHashCode() =>
        HashCode.Combine(GameId, TurnNumber, ActivePlayerId, CurrentStep, NextTimestamp, Objects.Count, Battlefield.Count, Stack.Count);

    /// <summary>Takes the next timestamp (CR 613.7) and advances the counter.</summary>
    public (GameState State, long Timestamp) TakeTimestamp() =>
        (this with { NextTimestamp = NextTimestamp + 1 }, NextTimestamp);
}
