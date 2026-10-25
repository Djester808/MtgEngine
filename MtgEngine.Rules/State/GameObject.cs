using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

/// <summary>Stored object identity, printed definition, ownership, current control and status.</summary>
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
    /// <summary>Persistent deck-card identity. Null for tokens, copies and nonphysical objects.</summary>
    public CardInstanceId? CardInstanceId { get; init; }

    /// <summary>The saved deck selection for this card. Copies and tokens do not inherit it.</summary>
    public StartingCard? DeckSelection { get; init; }

    public PlayerId Controller { get; init; } = InitialController;

    public bool IsCopy { get; init; }

    public bool Tapped { get; init; }

    public bool FaceDown { get; init; }

    public bool PhasedOut { get; init; }

    public bool SummoningSick { get; init; } = true;

    /// <summary>A permanent is an object on the battlefield, including while phased out.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPermanent => Zone.Zone == Cards.Zone.Battlefield;

    public bool IsToken { get; init; }

    public int Damage { get; init; }

    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;

    public ObjectId? AttachedTo { get; init; }

    /// <summary>An Aura attached to a player, CR 303.4f: it entered attached to the player it was cast at. CR 704.5m buries an Aura attached to neither an object nor a player.</summary>
    [RuleRef("303.4f")]
    public PlayerId? AttachedToPlayer { get; init; }

    /// <summary>Stored commander designation, independent of current zone membership.</summary>
    [RuleRef("903.3")]
    public bool IsCommander { get; init; }

    /// <summary>Damage from deathtouch since the last state-based-action check; consumed by the bounded combat provider.</summary>
    [RuleRef("702.2b")]
    public bool DealtDeathtouchDamage { get; init; }

    /// <summary>Designations the rules give an object: suspected (701.60), goaded-by ids, ring-bearer (701.54), harnessed (701.64), monstrous (701.37), and so on.</summary>
    public ImmutableHashSet<string> Designations { get; init; } = [];

    /// <summary>
    /// The values this object's abilities chose as they resolved, by the key the card gave them
    /// (a colour, a creature type, a number), CR 608.2d: kept on the object so a later ability of
    /// the same object reads "the chosen color" outside the resolution that chose it. This model does not resolve abilities or perform zone transitions.
    /// </summary>
    [RuleRef("608.2d")]
    public ImmutableDictionary<string, string> Chosen { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>For a spell or ability on the stack: the ability being resolved, its source and the choices made on casting.</summary>
    public StackEntry? Stack { get; init; }

    public Face Face => Definition.GetFace(FaceIndex);
}

/// <summary>Stored stack metadata only; no casting or resolution behavior.</summary>
public sealed record StackEntry(ObjectId? Source, int? AbilityIndex,
    ImmutableList<ObjectId> Targets, ImmutableList<PlayerId> TargetPlayers,
    ImmutableList<int> Modes, int? X, bool IsCopy);
