using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Rules.State;

/// <summary>
/// One permanent crossing the edge of the battlefield, as much of it as outlives the object.
/// </summary>
/// <remarks>
/// A zone change makes a new object with no relation to the old one (CR 400.7), so by the time
/// anything asks "did a permanent enter/leave this turn" the object it is asking about is gone or
/// is a stranger. What a question can read is only what was written down as it crossed.
/// <para>
/// Three facts, and every corpus shape is a filter over them. The <b>card</b> rather than a few
/// extracted flags, for the reason <see cref="PlayerState.SpellCardsCastThisTurn"/> keeps cards:
/// the family asks by type ("an artifact entered"), by the absence of one ("two or more nonland
/// permanents"), and on five singles by subtype ("the number of Goblins", "another Knight", "a
/// Food") — and no fixed set of extracted flags answers the next one. The <b>controller</b>
/// because almost every one of them is scoped to it. The <b>id</b> because "<em>another</em>
/// creature entered under your control this turn" is the same question with the asking permanent
/// taken out, and nothing else distinguishes it.
/// </para>
/// </remarks>
/// <param name="Id">The identity it had on the battlefield — the new one on the way in, the old one on the way out.</param>
/// <param name="ControllerId">Who controlled it as it crossed.</param>
/// <param name="Card">The printed card, whose <see cref="CardType.Token"/> flag is a type here.</param>
public abstract record BattlefieldCrossing(ObjectId Id, Guid ControllerId, CardDefinition Card)
{
    /// <summary>
    /// Compared by the card's oracle id, the way <see cref="GameObject"/> compares its own card.
    /// </summary>
    /// <remarks>
    /// <c>CardDefinition</c> is a class with reference equality, and a state rebuilt from a stored
    /// log has its own instances — so comparing the reference would make <c>Replay(log) == State</c>
    /// fail for every game in which anything had entered or left.
    /// </remarks>
    public virtual bool Equals(BattlefieldCrossing? other) =>
        other is not null
        && GetType() == other.GetType()
        && Id == other.Id
        && ControllerId == other.ControllerId
        && string.Equals(Card.OracleId, other.Card.OracleId, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, ControllerId, Card.OracleId);
}

/// <summary>One permanent that entered the battlefield this turn (CR 400.7).</summary>
/// <remarks>
/// The controller is the one the permanent <em>entered</em> under, which is what every card in
/// this family asks about and is exactly what the move or the creation said. Unlike the leaving
/// controller it needs no computing: control-changing effects apply to permanents that are
/// already there, so at the moment of entry the stored answer is the right one.
/// </remarks>
public sealed record BattlefieldArrival(ObjectId Id, Guid ControllerId, CardDefinition Card)
    : BattlefieldCrossing(Id, ControllerId, Card);

/// <summary>One permanent that left the battlefield this turn (CR 400.7).</summary>
/// <param name="To">Where it went. Only the graveyard is dying (CR 700.4).</param>
public sealed record BattlefieldDeparture(
    ObjectId Id, Guid ControllerId, CardDefinition Card, Zone To)
    : BattlefieldCrossing(Id, ControllerId, Card)
{
    public bool Equals(BattlefieldDeparture? other) =>
        base.Equals(other) && To == other.To;

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), To);
}

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
    /// Permanents that are phased out, and whose untap step brings each one back (CR 702.26a).
    /// </summary>
    /// <remarks>
    /// A phased-out permanent is "treated as though it does not exist" (CR 702.26b), so it is
    /// taken out of <see cref="Battlefield"/> rather than flagged in it. That is the whole of
    /// the implementation: every count, every sweeper, every layer and every legality check in
    /// this engine reads that list, so removing the id from it makes all of them stop seeing the
    /// permanent at once. A flag would have needed a filter added at each of those places, and
    /// the one that got missed would be the bug.
    /// <para>
    /// It is <em>not</em> a zone change (CR 702.26d): the object keeps its id, its counters, its
    /// attachments and <see cref="GameObject.Zone"/> of <c>Battlefield</c>, and nothing that
    /// triggers on leaving or entering the battlefield fires.
    /// </para>
    /// <para>
    /// The value is the player whose untap step phases it back in, which is not always its own
    /// controller: an Aura that phased out along with the permanent it enchants comes back with
    /// that permanent (CR 702.26g), even when somebody else controls the Aura.
    /// </para>
    /// </remarks>
    public ImmutableDictionary<ObjectId, Guid> PhasedOut { get; init; } =
        ImmutableDictionary<ObjectId, Guid>.Empty;

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

    /// <summary>
    /// Prevention effects created by resolved spells and abilities (CR 615.1, 615.3).
    /// </summary>
    /// <remarks>
    /// Held apart from <see cref="FloatingEffects"/> because they are not layered: a prevention
    /// effect never changes a characteristic, so it has no place in CR 613's order and no
    /// timestamp to take. It watches a damage event and reduces it, which is the replacement
    /// machinery's job rather than the characteristics'.
    /// <para>
    /// Also apart from the countdown shields on permanents and players, which are the other kind
    /// of prevention (CR 615.7) and are spent rather than described.
    /// </para>
    /// </remarks>
    public ImmutableList<PreventionEffect> Preventions { get; init; } = [];

    /// <summary>
    /// Every permanent that has entered the battlefield this turn, oldest first (CR 400.7).
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="PermanentState.EnteredOnTurn"/> and not answerable from
    /// it. That says whether <em>this</em> permanent arrived this turn; this counts what arrived,
    /// which is what celebration ("two or more nonland permanents entered the battlefield under
    /// your control this turn") and its thirty-odd relatives ask. Deriving the count by sweeping
    /// the battlefield for permanents stamped with this turn would be free and would be wrong:
    /// a token created and sacrificed in the same turn still entered, and would not be there to
    /// be counted.
    /// </remarks>
    public ImmutableList<BattlefieldArrival> ArrivalsThisTurn { get; init; } = [];

    /// <summary>
    /// Every permanent that has left the battlefield this turn, oldest first (CR 400.7).
    /// </summary>
    /// <remarks>
    /// One recorded fact rather than a flag per question, because the corpus asks about the same
    /// departures seven different ways and every one of them is a filter over this list: morbid's
    /// "if a creature died this turn", "the number of creatures that died this turn", the nontoken
    /// variant of that count, revolt's "if a permanent left the battlefield under your control
    /// this turn", "if a creature left the battlefield under your control this turn", void's "if a
    /// nonland permanent left the battlefield this turn", and Sarevok's "if no permanents left the
    /// battlefield this turn". A flag per shape is seven fields that can disagree with each other;
    /// a list is one that cannot.
    /// <para>
    /// The card is the <em>printed</em> one, which is the same compromise the morbid flag this
    /// replaced already made: the reducer has no ability source, so it cannot compute what the
    /// permanent was as it left, and one that had stopped being a creature on its way out is a
    /// rarity the fold could not see either way. The controller is <b>not</b> that compromise —
    /// it is computed by the engine and carried on the move (see
    /// <see cref="Events.ObjectMoved.LeavingControllerId"/>), because control is layer 2 and the
    /// stored answer would file a stolen permanent against the player it was taken from.
    /// </para>
    /// </remarks>
    public ImmutableList<BattlefieldDeparture> DeparturesThisTurn { get; init; } = [];

    /// <summary>Whether a creature has gone to a graveyard from the battlefield this turn.</summary>
    /// <remarks>
    /// Morbid, and the several later words for the same question, which is asked game-wide: 64
    /// corpus cards say "if a creature died this turn" and none of them say whose. Derived rather
    /// than stored, so it cannot fall out of step with the count beside it.
    /// </remarks>
    public bool CreatureDiedThisTurn => CreaturesDiedThisTurn() > 0;

    /// <summary>
    /// How many creatures have died this turn (CR 700.4).
    /// </summary>
    /// <remarks>
    /// "Died" is battlefield to graveyard and nothing else: a creature exiled or bounced left the
    /// battlefield without dying, and the cards that count deaths mean the graveyard. Nineteen
    /// corpus cards multiply by this — "for each creature that died this turn" and "the number of
    /// creatures that died this turn" — against three that only need "three or more".
    /// </remarks>
    /// <param name="nontokenOnly">
    /// Count only cards. "The number of nontoken creatures that died this turn" is printed on its
    /// own cards, and a token dying would otherwise inflate every one of them.
    /// </param>
    /// <param name="controllerId">
    /// Whose creatures, or null for everybody's. The plain count is what the family asks; the
    /// scope is here because the departure family beside it needs one and the two share a list.
    /// </param>
    public int CreaturesDiedThisTurn(bool nontokenOnly = false, Guid? controllerId = null) =>
        DeparturesThisTurn.Count(gone =>
            gone.To == Zone.Graveyard
            && gone.Card.CardTypes.HasFlag(CardType.Creature)
            && !(nontokenOnly && gone.Card.CardTypes.HasFlag(CardType.Token))
            && (controllerId is not { } who || gone.ControllerId == who));

    /// <summary>
    /// How many permanents have left the battlefield this turn, to any zone.
    /// </summary>
    /// <param name="controllerId">
    /// Whose, or null for everybody's. <b>Nearly every card asking this names a player</b>:
    /// revolt's "if a permanent left the battlefield under your control this turn" and "if a
    /// permanent you controlled left the battlefield this turn" are 26 and 5 corpus lines between
    /// them, and answering either game-wide makes an opponent's creature dying turn the card on —
    /// strictly better than printed, and it would still play. Null is for void alone, which really
    /// is game-wide.
    /// </param>
    /// <param name="types">
    /// Any of these printed card types, or <c>None</c> for any permanent. "If a creature left the
    /// battlefield under your control this turn" is the one card type the family names.
    /// </param>
    /// <param name="nonlandOnly">
    /// Skip lands, which is how void is worded: "if a nonland permanent left the battlefield this
    /// turn or a spell was warped this turn". Sixteen corpus lines ask it that way, and counting a
    /// fetchland's own sacrifice would turn every one of them on for free. Void has no rule number
    /// of its own — it is an ability word, and those have no rules meaning (CR 207.2c); only its
    /// second half is defined, at CR 702.185c.
    /// </param>
    public int PermanentsLeftBattlefieldThisTurn(
        Guid? controllerId = null, CardType types = CardType.None, bool nonlandOnly = false) =>
        DeparturesThisTurn.Count(gone => Matches(gone, controllerId, types, nonlandOnly));

    /// <summary>
    /// How many permanents entered the battlefield this turn, under a player's control or anyone's.
    /// </summary>
    /// <param name="controllerId">
    /// Whose, or null for everybody's. Every measured shape but one names "your control"; the
    /// exception is a single card asking about an opponent's, which the same argument answers.
    /// </param>
    /// <param name="types">Any of these printed card types, or <c>None</c> for any permanent.</param>
    /// <param name="nonlandOnly">
    /// Skip lands — celebration's "two or more nonland permanents entered the battlefield under
    /// your control this turn", the largest single shape in the family at nine corpus lines.
    /// </param>
    /// <param name="except">
    /// A permanent to leave out, which is the whole of what "<em>another</em> creature entered the
    /// battlefield under your control this turn" adds. The asking permanent's own arrival is in
    /// this list like any other, so a reader that forgot this would have every such card answer
    /// yes about itself.
    /// </param>
    public int PermanentsEnteredThisTurn(
        Guid? controllerId = null,
        CardType types = CardType.None,
        bool nonlandOnly = false,
        ObjectId? except = null) =>
        ArrivalsThisTurn.Count(came =>
            Matches(came, controllerId, types, nonlandOnly)
            && (except is not { } mine || came.Id != mine));

    /// <summary>The filter both crossing counts share, so the two cannot drift apart.</summary>
    private static bool Matches(
        BattlefieldCrossing crossing, Guid? controllerId, CardType types, bool nonlandOnly) =>
        (controllerId is not { } who || crossing.ControllerId == who)
        && (types == CardType.None || (crossing.Card.CardTypes & types) != CardType.None)
        && !(nonlandOnly && crossing.Card.CardTypes.HasFlag(CardType.Land));

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
    /// Who has the initiative, if anyone (CR 726.1).
    /// </summary>
    /// <remarks>
    /// The monarch's structural twin, and stored the same way for the same reason: only one
    /// player can have it at a time (CR 726.3), so it is one nullable field rather than a flag
    /// per player, and taking it is the same assignment that takes it away from the previous
    /// holder. There is no initiative in a game until an effect has somebody take it.
    /// </remarks>
    public Guid? InitiativeId { get; init; }

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
    /// Whether this permanent entered the battlefield on the turn now being taken.
    /// </summary>
    /// <remarks>
    /// The comparison, in one place, so that no caller has to remember which side of it the turn
    /// number goes on. An object that is not a permanent — a card in a hand, a spell on the stack,
    /// an ability — has not entered anything, and answers no.
    /// <para>
    /// This is <em>not</em> <see cref="PermanentState.HasSummoningSickness"/>; see the remarks
    /// there for why the two diverge on precisely the turn that matters.
    /// </para>
    /// </remarks>
    public bool EnteredThisTurn(ObjectId id) =>
        TryGetObject(id, out var obj) && EnteredThisTurn(obj);

    /// <inheritdoc cref="EnteredThisTurn(ObjectId)"/>
    public bool EnteredThisTurn(GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.Permanent is { } permanent && permanent.EnteredOnTurn == TurnNumber;
    }

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
        Structural.Same(PhasedOut, other.PhasedOut) &&
        Structural.Same(Stack, other.Stack) &&
        Structural.Same(Exile, other.Exile) &&
        Structural.Same(Command, other.Command) &&
        Structural.Same(Players, other.Players) &&
        Structural.Same(PendingTriggers, other.PendingTriggers) &&
        Structural.Same(FloatingEffects, other.FloatingEffects) &&
        Structural.Same(Preventions, other.Preventions) &&

        // Delayed triggers were missing from this comparison, which is the one omission
        // that hides itself: two states differing only in what is waiting to happen
        // compared equal, so Replay(log) == State - the invariant every behaviour test
        // leans on - would have passed straight through a divergence in them (CR 603.7).
        Structural.Same(Delayed, other.Delayed) &&
        Structural.Same(ArrivalsThisTurn, other.ArrivalsThisTurn) &&
        Structural.Same(DeparturesThisTurn, other.DeparturesThisTurn) &&
        MonarchId == other.MonarchId &&
        InitiativeId == other.InitiativeId &&
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
