using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

/// <summary>
/// One object in the game: a card, token, copy of a spell, ability on the stack or emblem.
/// This holds what is stored; current characteristics are computed by the layer system
/// (CR 613) from <see cref="Definition"/>, counters, status and the active continuous
/// effects, never stored here. <see cref="InitialController"/> is where control started; the
/// controller now is a computed characteristic too.
/// </summary>
[RuleRef("109")]
public sealed partial record GameObject(
    ObjectId Id,
    CardDefinition Definition,
    int FaceIndex,
    PlayerId Owner,
    PlayerId InitialController,
    ZoneKey Zone,
    int Timestamp)
{
    /// <summary>Persistent deck-card identity. Null for tokens, copies and pre-migration logs.</summary>
    public CardInstanceId? CardInstanceId { get; init; }

    /// <summary>The saved deck selection for this card. Copies and tokens do not inherit it.</summary>
    public StartingCard? DeckSelection { get; init; }

    public bool Tapped { get; init; }

    public bool FaceDown { get; init; }

    public bool PhasedOut { get; init; }

    public bool SummoningSick { get; init; } = true;

    public bool IsToken { get; init; }

    public int Damage { get; init; }

    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;

    public ObjectId? AttachedTo { get; init; }

    /// <summary>An Aura attached to a player, CR 303.4f: it entered attached to the player it was cast at. CR 704.5m buries an Aura attached to neither an object nor a player.</summary>
    [RuleRef("303.4f")]
    public PlayerId? AttachedToPlayer { get; init; }

    /// <summary>A commander stays one through every zone change, CR 903.3; the zone system sets this at creation and carries it on every move.</summary>
    [RuleRef("903.3")]
    public bool IsCommander { get; init; }

    /// <summary>Set when a source with deathtouch dealt this permanent damage, CR 702.2b; the state-based action of 704.5h reads it and clears it.</summary>
    [RuleRef("702.2b")]
    public bool DealtDeathtouchDamage { get; init; }

    /// <summary>Designations the rules give an object: suspected (701.60), goaded-by ids, ring-bearer (701.54), harnessed (701.64), monstrous (701.37), and so on.</summary>
    public ImmutableHashSet<string> Designations { get; init; } = [];

    /// <summary>
    /// The values this object's abilities chose as they resolved, by the key the card gave them
    /// (a colour, a creature type, a number), CR 608.2d: kept on the object so a later ability of
    /// the same object reads "the chosen color" outside the resolution that chose it. Folded by
    /// <c>ValueChosen</c>; cleared with the object when it changes zones (CR 400.7).
    /// </summary>
    [RuleRef("608.2d")]
    public ImmutableDictionary<string, string> Chosen { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>For a spell or ability on the stack: the ability being resolved, its source and the choices made on casting.</summary>
    public StackEntry? Stack { get; init; }

    public Face Face => Definition.GetFace(FaceIndex);
}

/// <summary>What sits on the stack besides the object itself: which ability, from which source, with which choices, CR 601.2.</summary>
[RuleRef("601.2")]
public sealed record StackEntry(
    ObjectId? Source,
    int? AbilityIndex,
    ImmutableList<ObjectId> Targets,
    ImmutableList<PlayerId> TargetPlayers,
    ImmutableList<int> Modes,
    int? X,
    bool IsCopy,
    ImmutableDictionary<Color, int>? ManaSpent = null,
    Zone? CastFrom = null,
    ImmutableList<string>? OptionalCostsPaid = null,
    ImmutableDictionary<string, ImmutableList<ObjectId>>? Remembered = null,
    ImmutableDictionary<string, ImmutableList<PlayerId>>? RememberedPlayers = null,
    ImmutableList<ObjectId>? Triggering = null,
    PlayerId? GiftPromisedTo = null,
    ImmutableDictionary<string, string>? Chosen = null,
    ImmutableList<Mana.ManaProvenance>? ManaProvenanceSpent = null);
