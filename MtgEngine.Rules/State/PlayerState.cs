namespace MtgEngine.Rules.State;

/// <summary>What the rules track per player, CR 119 onwards. Cards are objects; this is the rest.</summary>
public sealed record PlayerState(PlayerId Id, string Name, int Life)
{
    public ImmutableDictionary<string, int> Counters { get; init; } = ImmutableDictionary<string, int>.Empty;

    public ManaPool Mana { get; init; } = ManaPool.Empty;

    public bool HasLost { get; init; }

    public bool HasWon { get; init; }

    public int LandsPlayedThisTurn { get; init; }

    public int MaxHandSize { get; init; } = 7;

    public int ObjectsDrawnThisTurn { get; init; }

    /// <summary>Set when a draw found no card, CR 121.4; the next state-based action check loses the game, CR 704.5b.</summary>
    public bool DrewFromEmptyLibrary { get; init; }

    /// <summary>Damage dealt by each commander, CR 903.10a.</summary>
    public ImmutableDictionary<ObjectId, int> CommanderDamage { get; init; } = ImmutableDictionary<ObjectId, int>.Empty;
}

/// <summary>Mana a player has, CR 106.4. Untyped restrictions are the mana subsystem's to add.</summary>
[Cards.RuleRef("106.4")]
public sealed record ManaPool(int White, int Blue, int Black, int Red, int Green, int Colorless)
{
    public static ManaPool Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public int Total => White + Blue + Black + Red + Green + Colorless;
}
