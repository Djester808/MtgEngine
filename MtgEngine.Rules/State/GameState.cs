using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

/// <summary>
/// The whole game at one instant. Immutable: every event produces a new state, and
/// <c>GameReducer.Replay(log)</c> must equal the state it was folded from. Nothing here is
/// derived; anything that can be computed from the log and the definitions is computed.
/// </summary>
public sealed record GameState
{
    public ImmutableDictionary<string, LibraryInspection> LibraryInspections { get; init; } = ImmutableDictionary<string, LibraryInspection>.Empty;
    public ImmutableDictionary<PlayerId, ObjectId> RevealedLibraryTops { get; init; } = ImmutableDictionary<PlayerId, ObjectId>.Empty;
    public ImmutableHashSet<ObjectId> PreviouslyRevealedLibraryCards { get; init; } = [];

    public ImmutableHashSet<ManualRequest> ManualRequests { get; init; } = [];

    public CardReveal? LastReveal { get; init; }
    public ImmutableDictionary<PlayerId, CardReveal> LastLooks { get; init; } = ImmutableDictionary<PlayerId, CardReveal>.Empty;

    public required ImmutableList<PlayerState> Players { get; init; }

    public required ImmutableDictionary<ObjectId, GameObject> Objects { get; init; }

    /// <summary>Ordered contents of every zone. Libraries are top-first; the stack is bottom-first.</summary>
    public required ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>> Zones { get; init; }

    public required TurnState Turn { get; init; }

    public PriorityState Priority { get; init; } = PriorityState.None;

    public CombatState? Combat { get; init; }

    public PendingChoice? Pending { get; init; }

    public OpeningHandsState? OpeningHands { get; init; }

    public ImmutableList<ActiveEffect> ActiveEffects { get; init; } = [];

    public ImmutableList<DelayedTriggerState> DelayedTriggers { get; init; } = [];

    public RngState Rng { get; init; } = new(1, 0);

    public int NextObjectId { get; init; } = 1;

    public int NextTimestamp { get; init; } = 1;

    public int NextChoiceId { get; init; } = 1;

    public GameOutcome? Outcome { get; init; }

    /// <summary>The format the game was created in, so a variant rule such as CR 903.10a can ask.</summary>
    public string Format { get; init; } = "commander";

    [RuleRef("725.1")]
    public PlayerId? Monarch { get; init; }

    [RuleRef("726.1")]
    public PlayerId? Initiative { get; init; }

    /// <summary>What happened this turn, for the conditions that ask; reset when a turn begins.</summary>
    public TurnHistory History { get; init; } = TurnHistory.Empty;

    /// <summary>CR 903.9a: commanders newly moved to graveyard or exile, awaiting an owner decision.</summary>
    public ImmutableHashSet<ObjectId> CommanderReturnCandidates { get; init; } = [];

    /// <summary>CR 903.8: command-zone casts per commander and caster, retained across turns and zone changes.</summary>
    public ImmutableDictionary<(CommanderIdentity Commander, PlayerId Caster), int> CommanderCasts { get; init; } =
        ImmutableDictionary<(CommanderIdentity Commander, PlayerId Caster), int>.Empty;

    [RuleRef("903.8")]
    public int CommandZoneCasts(GameObject commander, PlayerId caster) =>
        CommanderCasts.GetValueOrDefault((CommanderIdentity.Of(commander), caster));

    /// <summary>Resolve a game-local seat without silently accepting an inconsistent stored identity.</summary>
    public PlayerState Player(PlayerId id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id.Value);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id.Value, Players.Count);
        var player = Players[id.Value];
        if (player.Id != id)
            throw new InvalidOperationException("The stored player identity does not match its seat.");
        return player;
    }

    public ImmutableList<ObjectId> In(ZoneKey zone) => Zones.TryGetValue(zone, out var list) ? list : [];

    /// <summary>
    /// How many cards a player drew this turn, CR 121.1: one per draw event, so a card drawn and
    /// then discarded, played or exiled still counts. It is not a count of their hand, which is the
    /// reading this exists to refuse. Kept by <c>ObjectDrawn</c> on the player and cleared by
    /// <c>TurnBegan</c>.
    /// </summary>
    [RuleRef("121.1")]
    public int CardsDrawnThisTurn(PlayerId who) => Player(who).ObjectsDrawnThisTurn;

    /// <summary>
    /// How many lands entered the battlefield under a player's control this turn, CR 121.1: however
    /// they entered — played, put onto the battlefield by a spell, or created as a token — and a land
    /// that has since left still counts, which is why this walks <see cref="TurnHistory"/> and not
    /// the board. Each entry is counted once; the controller is the one it entered under (CR 110.2a),
    /// so a later change of control does not move the count.
    /// <para>
    /// The land is read from the printed face the object entered as: with no layer system to ask from
    /// here, that is the honest answer, and a query arm that needs the computed type passes its own
    /// characteristics (<c>seam-requests/E2c.md</c>). An entry whose object neither still exists nor
    /// was kept as a departed object is not counted — nothing in the state says what it was.
    /// </para>
    /// </summary>
    /// <summary>The objects that left the battlefield this turn, by id. First writer wins: the
    /// earliest record of an id is the one the old scan found.</summary>
    private static Dictionary<ObjectId, GameObject> Index(ImmutableList<GameObject> left)
    {
        var byId = new Dictionary<ObjectId, GameObject>();
        foreach (var o in left)
        {
            if (!byId.ContainsKey(o.Id))
                byId[o.Id] = o;
        }

        return byId;
    }

    [RuleRef("121.1")]
    public int LandsEnteredThisTurn(PlayerId who)
    {
        var counted = new HashSet<ObjectId>();
        var lands = 0;

        // The departed are indexed once rather than scanned per entry: this is reached from
        // Query.Amounts through PerTurn, once per player per evaluation, and query amounts are
        // re-derived on every condition check - so the nested scan was O(entered x departed)
        // every time anything asked a condition.
        Dictionary<ObjectId, GameObject>? departed = null;
        foreach (var id in History.EnteredBattlefield)
        {
            if (counted.Contains(id))
                continue;
            GameObject? entered;
            if (Objects.TryGetValue(id, out var live))
            {
                entered = live;
            }
            else
            {
                departed ??= Index(History.LeftBattlefield);
                entered = departed.GetValueOrDefault(id);
            }

            if (entered is null)
                continue; // CR 111.7: a token that ceased to exist is in neither place; the token record below may still say what it was.
            counted.Add(id);
            if (EnteredAsLand(entered, who))
                lands++;
        }

        // CR 111.1: a token created on the battlefield is in the turn's token record as it was made,
        // which is the one description left of a token that has since ceased to exist (CR 111.7).
        if (History.TokensCreated.TryGetValue(who, out var tokens))
        {
            foreach (var token in tokens)
            {
                if (token.Zone.Zone == Zone.Battlefield && counted.Add(token.Id) && EnteredAsLand(token, who))
                    lands++;
            }
        }

        return lands;
    }

    /// <summary>A land, CR 305.1, that entered under this player's control, CR 110.2a.</summary>
    [RuleRef("305.1")]
    private static bool EnteredAsLand(GameObject entered, PlayerId who) =>
        entered.InitialController == who && entered.Face.Types.Types.HasFlag(CardType.Land);
}

[RuleRef("500")]
public sealed record TurnState(int Number, PlayerId ActivePlayer, Step Step, int TurnOrderIndex)
{
    /// <summary>Extra turns waiting to be taken, most recent first, CR 500.7.</summary>
    public ImmutableList<PlayerId> ExtraTurns { get; init; } = [];

    public bool IsExtraTurn { get; init; }
}

[RuleRef("117")]
public sealed record PriorityState(PlayerId? Holder, ImmutableList<PlayerId> PassedInSuccession)
{
    public static PriorityState None { get; } = new(null, []);
}

[RuleRef("506")]
public sealed record CombatState(
    PlayerId AttackingPlayer,
    ImmutableDictionary<ObjectId, CombatTarget> Attackers,
    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers,
    bool AttackersDeclared,
    bool BlockersDeclared)
{
    public ImmutableList<PlayerId> DefendingPlayers { get; init; } = [];

    public ImmutableList<PlayerId> BlockersDeclaredBy { get; init; } = [];
}

/// <summary>What an attacker attacks: a player, or a planeswalker or battle, CR 506.1.</summary>
public sealed record CombatTarget(PlayerId? Player, ObjectId? Permanent);

/// <summary>A continuous effect in force, with what created it, when, and until when, CR 611.</summary>
[RuleRef("611")]
public sealed record ActiveEffect(
    int Id,
    ContinuousEffect Effect,
    ObjectId? Source,
    PlayerId Controller,
    ImmutableList<ObjectId> AppliesTo,
    ImmutableList<PlayerId> AppliesToPlayers,
    Duration Until,
    int Timestamp,
    bool FromStaticAbility,
    ImmutableDictionary<string, ImmutableList<ObjectId>>? Remembered = null);

/// <summary>
/// A delayed triggered ability waiting for its event, CR 603.7.
/// <para>
/// <paramref name="Until"/> is the duration the ability was created with, CR 603.7c: an ability set
/// up "until end of turn" stops existing when that duration ends, whether or not it ever triggered,
/// and the cleanup step drops it (CR 514.2, <c>TurnSystem</c>). It answers a different question from
/// <paramref name="Once"/> — "until end of turn, whenever a creature dies …" is both once-only false
/// and dated — so a trigger sets whichever of the two it printed. None means the ability waits for
/// its event with no deadline, which is every delayed trigger written before this field existed, so
/// no existing construction changes.
/// </para>
/// </summary>
[RuleRef("603.7")]
public sealed record DelayedTriggerState(int Id, Trigger When, Effect Do, ObjectId? Source, PlayerId Controller, bool Once, ImmutableDictionary<string, ImmutableList<ObjectId>> Remembered, Duration? Until = null);

/// <summary>The game stops and asks. Nothing else happens until this is answered by an event.</summary>
public sealed record PendingChoice(
    ChoiceId Id,
    PlayerId Player,
    string Kind,
    string Prompt,
    ImmutableList<ChoiceOption> Options,
    int Min,
    int Max,
    ChoiceContext Context);

public sealed record ChoiceOption(string Id, string Label, ObjectId? Object = null, PlayerId? Player = null);

/// <summary>
/// Where a question came from, so the answer finds its way back: the source and ability, the
/// path inside the effect tree, free-form keys, and for a replacement-order question (CR 616.1)
/// the event the choice is about, which must survive the halt for the loop to resume.
/// <para>
/// It is a locator, not a continuation: resumption is a branch per kind, so a halted game
/// replays from its log rather than from a captured closure.
/// </para>
/// </summary>
public sealed record ChoiceContext(ObjectId? Source, int? AbilityIndex, string? EffectPath, ImmutableDictionary<string, string> Extra, Events.GameEvent? Event = null)
{
    /// <summary>Private effect tree for an off-stack ability whose source may leave during payment.</summary>
    public Effect? OffStackEffect { get; init; }
    /// <summary>Private inner effect interrupted while the outer effect proposed an event.</summary>
    public ChoiceContext? ReplacementContinuation { get; init; }
    /// <summary>Read-only legacy data; effect payment continuations are no longer executed.</summary>
    public ChoiceContext? EffectPaymentContinuation { get; init; }
    /// <summary>Legacy privacy metadata only; these payments cannot be resumed.</summary>
    public Casting.RemainingCostPayment? RemainingPayment { get; init; }
    /// <summary>Server-only remainder of interrupted library draw operations.</summary>
    public ImmutableList<Library.PendingDrawBatch> DeferredDraws { get; init; } = [];

    /// <summary>Server-only shuffles owed after the interrupted operation completes.</summary>
    public ImmutableList<PlayerId> DeferredShuffles { get; init; } = [];

    /// <summary>Server-only continuation of a simultaneous zone-move batch.</summary>
    public ImmutableList<Events.ObjectMoved> DeferredZoneMoves { get; init; } = [];
    /// <summary>Inherited replacement keys per deferred source object; never per-card consumed keys.</summary>
    public ImmutableDictionary<int, ImmutableHashSet<string>> DeferredMoveHistory { get; init; }
        = ImmutableDictionary<int, ImmutableHashSet<string>>.Empty;

    /// <summary>Private evaluation board for an interrupted simultaneous zone move.</summary>
    public string? ZoneMoveSnapshot { get; init; }

    /// <summary>Private events awaiting trigger collection after the current operation's choices finish.</summary>
    public ImmutableList<Events.GameEvent> DeferredTriggerEvents { get; init; } = [];

    /// <summary>Abilities already detected, waiting for the current operation to finish.</summary>
    public ImmutableList<Triggers.PendingTrigger> DeferredTriggers { get; init; } = [];

    public bool AdvanceAfterZoneBatch { get; init; }
}

/// <summary>
/// The facts about this turn that conditions ask for: morbid, revolt, raid, "entered this
/// turn", damage dealt this turn. Folded by the events that make each fact; cleared by
/// <c>TurnBegan</c>. The query layer only reads it.
/// </summary>
public sealed record TurnHistory(
    ImmutableList<ObjectId> EnteredBattlefield,
    ImmutableList<GameObject> Died,
    ImmutableList<GameObject> LeftBattlefield,
    ImmutableList<SpellCastRecord> SpellsCast,
    ImmutableList<ObjectId> AttackedWith,
    ImmutableDictionary<ObjectId, int> DamageDealtBy,
    ImmutableDictionary<ObjectId, int> DamageReceivedBy,
    ImmutableDictionary<PlayerId, int> DamageReceivedByPlayer,
    ImmutableDictionary<(ObjectId Source, PlayerId To), int> DamageDealtToPlayer,
    ImmutableDictionary<(ObjectId Source, int AbilityIndex), int> AbilitiesResolvedThisTurn)
{
    public static TurnHistory Empty { get; } = new(
        [], [], [], [], [],
        ImmutableDictionary<ObjectId, int>.Empty,
        ImmutableDictionary<ObjectId, int>.Empty,
        ImmutableDictionary<PlayerId, int>.Empty,
        ImmutableDictionary<(ObjectId, PlayerId), int>.Empty,
        ImmutableDictionary<(ObjectId, int), int>.Empty);

    /// <summary>
    /// The tokens each player created this turn, as they were created (CR 111.1), so "you created
    /// a token this turn" still answers for a token that has since been sacrificed, the way
    /// <see cref="Died"/> keeps the departed object. Folded by <c>ObjectCreated</c>.
    /// </summary>
    [RuleRef("111.1")]
    public ImmutableDictionary<PlayerId, ImmutableList<GameObject>> TokensCreated { get; init; } = ImmutableDictionary<PlayerId, ImmutableList<GameObject>>.Empty;

    /// <summary>
    /// The lands played this turn, with the zone each came from and the permission it was played
    /// under, CR 305.1 with 116.2a. A land play is not a cast, so <see cref="SpellsCast"/> never
    /// answers for one; a once-per-turn permission that admits lands reads this and the casts
    /// together, each by its own <c>Permission</c>. Folded by the land play, cleared by
    /// <c>TurnBegan</c> with the rest of this record.
    /// </summary>
    [RuleRef("116.2a")]
    public ImmutableList<LandPlayRecord> LandsPlayed { get; init; } = [];
}

/// <summary>
/// A spell cast this turn, CR 601. <paramref name="From"/> is the zone it was cast from, and
/// <paramref name="Permission"/> is the <see cref="ActiveEffect.Id"/> of the effect that allowed
/// the cast where the rules otherwise would not have — a <c>MayPlayFromZone</c>, CR 116.2a. None
/// means the cast needed no permission, which is every ordinary cast from a hand.
/// <para>
/// A once-per-turn permission counts what it has allowed by this field and never by
/// <paramref name="From"/> alone: two such permissions over one zone would otherwise share a
/// single count, and a cast made from that zone by another road — flashback, an instruction under
/// CR 608.2g — would spend it.
/// </para>
/// </summary>
[RuleRef("116.2a")]
public sealed record SpellCastRecord(ObjectId Spell, PlayerId By, Zone From, int? Permission = null, StaticPlayPermission? StaticPermission = null);

/// <summary>
/// A land played this turn, CR 305.1 with 116.2a. A land play is not a cast, so it is recorded
/// separately and nothing in <see cref="TurnHistory.SpellsCast"/> answers for it; without this a
/// land played under a once-per-turn permission is not counted at all and the permission grants a
/// second land. <paramref name="Permission"/> is the <see cref="ActiveEffect.Id"/> of the effect
/// that allowed the play, none for the ordinary one-per-turn land drop of CR 305.2.
/// </summary>
[RuleRef("116.2a")]
public sealed record LandPlayRecord(ObjectId Land, PlayerId By, Zone From, int? Permission = null, StaticPlayPermission? StaticPermission = null);

/// <summary>A deterministic generator. The seed is in the log; the counter advances with every draw.</summary>
public readonly record struct RngState(ulong Seed, int Counter);

public sealed record GameOutcome(ImmutableList<PlayerId> Winners, bool Draw, string Reason);
