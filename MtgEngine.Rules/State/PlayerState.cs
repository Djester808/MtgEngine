namespace MtgEngine.Rules.State;

/// <summary>What the rules track per player, CR 119 onwards. Cards are objects; this is the rest.</summary>
public sealed record PlayerState(PlayerId Id, string Name, int Life)
{
    /// <summary>Creates a game participant. Id is a zero-based seat, never an account id; names need not be unique.</summary>
    public static PlayerState Create(PlayerId id, string name, int life)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PlayerState(id, name, life);
    }

    /// <summary>
    /// Identifies this participant's library, hand or graveyard (CR 400.1).
    /// A key grants no viewing or movement permission. Read membership from GameState;
    /// movement and viewer projection require future implementations.
    /// </summary>
    public ZoneKey OwnedZone(Cards.Zone zone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Id.Value);
        if (zone is not (Cards.Zone.Library or Cards.Zone.Hand or Cards.Zone.Graveyard))
            throw new ArgumentOutOfRangeException(nameof(zone), "The zone is not owned by an individual player.");
        return ZoneKey.Of(zone, Id);
    }

    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;

    public ManaPool Mana { get; init; } = ManaPool.Empty;

    public bool HasLost { get; init; }

    public int LandsPlayedThisTurn { get; init; }

    public int MaxHandSize { get; init; } = 7;

    public int ObjectsDrawnThisTurn { get; init; }

    /// <summary>Stored count of cards drawn in the current step.</summary>
    public int ObjectsDrawnThisStep { get; init; }

    /// <summary>Stored failed-draw marker; this model does not adjudicate a loss.</summary>
    public bool DrewFromEmptyLibrary { get; init; }

    /// <summary>
    /// Damage dealt by each commander, CR 903.10a - keyed by the COMMANDER, not by the object.
    /// CR 400.7 makes a new object on every zone change, so an ObjectId key started the total
    /// again each time a commander died and was recast: a player could take 14 from one
    /// commander, kill it, take 14 more from the same card, and never reach the 21 the rule
    /// counts "over the course of the game".
    /// </summary>
    public ImmutableDictionary<CommanderIdentity, int> CommanderDamage { get; init; } =
        ImmutableDictionary<CommanderIdentity, int>.Empty;

    /// <summary>Designations a player can have: the city's blessing (702.131) and whatever later rules add.</summary>
    public ImmutableHashSet<string> Designations { get; init; } = [];
}

/// <summary>
/// Persistent commander key, using physical card identity when available.
/// Display metadata uses the original selection, never copied characteristics.
/// </summary>
[Cards.RuleRef("903.10a")]
public readonly record struct CommanderIdentity(PlayerId Owner, string OracleId, CardInstanceId? CardInstanceId = null)
{
    public static CommanderIdentity Of(GameObject card) =>
        new(card.Owner, card.DeckSelection?.OracleId ?? card.Definition.OracleId, card.CardInstanceId);

    public override string ToString() => $"{OracleId} (seat {Owner.Value})";
}

/// <summary>Stored mana totals; payment and production are not implemented.</summary>
public sealed record ManaPool(int White, int Blue, int Black, int Red, int Green, int Colorless)
{
    public static ManaPool Empty { get; } = new(0, 0, 0, 0, 0, 0);
    public int Total => White + Blue + Black + Red + Green + Colorless;
}
