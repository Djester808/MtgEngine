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

    /// <summary>
    /// How many cards this player has drawn since the current step began, CR 121.1. It answers the
    /// one question <see cref="ObjectsDrawnThisTurn"/> cannot — "is this the first card they drew in
    /// this step?", which CR 504.1's draw-step exception is written about — because a draw in the
    /// upkeep raises the turn's counter and not this one. Folded by <c>ObjectDrawn</c> through
    /// <see cref="Drew"/> and cleared by <c>StepBegan</c> through <see cref="StepBegins"/>; see
    /// <c>seam-requests/E2c.md</c>, which asks for those two call sites.
    /// </summary>
    [Cards.RuleRef("121.1")]
    public int ObjectsDrawnThisStep { get; init; }

    /// <summary>Set when a draw found no card, CR 121.4; the next state-based action check loses the game, CR 704.5b.</summary>
    public bool DrewFromEmptyLibrary { get; init; }

    /// <summary>
    /// This player drew a card, CR 121.1: both counters rise, the turn's and the step's. One event
    /// per card drawn, so a card drawn and then discarded still counts. Said here rather than in the
    /// fold so the two counters cannot drift apart at a call site that remembers only one of them.
    /// </summary>
    [Cards.RuleRef("121.1")]
    public PlayerState Drew() => this with
    {
        ObjectsDrawnThisTurn = ObjectsDrawnThisTurn + 1,
        ObjectsDrawnThisStep = ObjectsDrawnThisStep + 1,
    };

    /// <summary>
    /// A step began, CR 500.1: what this player drew in the step that just ended is no longer what
    /// they drew in this one. The turn's counter is untouched — it is cleared by <c>TurnBegan</c>,
    /// CR 121.1 — and an unchanged player is returned unchanged so a step that saw no draw adds
    /// nothing to the log's state churn.
    /// </summary>
    [Cards.RuleRef("500.1")]
    public PlayerState StepBegins() => ObjectsDrawnThisStep == 0 ? this : this with { ObjectsDrawnThisStep = 0 };

    /// <summary>Damage dealt by each commander, CR 903.10a.</summary>
    public ImmutableDictionary<ObjectId, int> CommanderDamage { get; init; } = ImmutableDictionary<ObjectId, int>.Empty;

    /// <summary>Designations a player can have: the city's blessing (702.131) and whatever later rules add.</summary>
    public ImmutableHashSet<string> Designations { get; init; } = [];
}

/// <summary>Mana a player has, CR 106.4. Untyped restrictions are the mana subsystem's to add.</summary>
[Cards.RuleRef("106.4")]
public sealed record ManaPool(int White, int Blue, int Black, int Red, int Green, int Colorless)
{
    public static ManaPool Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Mana that may be spent only on what its filter matches, CR 106.6a. It is otherwise ordinary
    /// mana and empties with the rest at the end of each step and phase, CR 106.6b. In a delta a
    /// negative amount spends a block.
    /// </summary>
    public ImmutableList<Mana.RestrictedBlock> Restricted { get; init; } = [];

    /// <summary>
    /// Where each mana came from, CR 106.6: the permanent whose ability produced it and the key a
    /// "when that mana is spent" delayed trigger (CR 106.6a) or a "spend only mana produced by" 
    /// restriction (CR 106.6b) reads. Mana is not an object, so this is the only handle on it. Kept
    /// beside the totals; in a delta a negative amount spends it. The mana subsystem reads and
    /// writes it; the fold only sums.
    /// </summary>
    [Cards.RuleRef("106.6")]
    public ImmutableList<Mana.ManaProvenance> Provenance { get; init; } = [];

    public int Total => White + Blue + Black + Red + Green + Colorless + Restricted.Sum(r => r.Amount);

    /// <summary>Folds a delta's blocks into a pool's by colour and filter; a block that reaches zero is gone.</summary>
    /// <summary>The same fold for <see cref="Provenance"/>: a block is one source, key and colour; a spent block drops out at zero.</summary>
    public static ImmutableList<Mana.ManaProvenance> FoldProvenance(ImmutableList<Mana.ManaProvenance> current, ImmutableList<Mana.ManaProvenance> delta)
    {
        if (delta.IsEmpty)
            return current;
        var folded = current.ToList();
        foreach (var block in delta)
        {
            var index = folded.FindIndex(b => b.Color == block.Color && b.Source == block.Source && b.Key == block.Key);
            if (index < 0)
                folded.Add(block);
            else
                folded[index] = folded[index] with { Amount = folded[index].Amount + block.Amount };
        }

        return [.. folded.Where(b => b.Amount > 0)];
    }

    public static ImmutableList<Mana.RestrictedBlock> FoldRestricted(ImmutableList<Mana.RestrictedBlock> current, ImmutableList<Mana.RestrictedBlock> delta)
    {
        if (delta.IsEmpty)
            return current;
        var folded = current.ToList();
        foreach (var block in delta)
        {
            var index = folded.FindIndex(b => b.Color == block.Color && b.SpendOnlyOn.Equals(block.SpendOnlyOn));
            if (index < 0)
                folded.Add(block);
            else
                folded[index] = folded[index] with { Amount = folded[index].Amount + block.Amount };
        }

        folded.RemoveAll(b => b.Amount <= 0);
        return folded.Count == 0 ? [] : [.. folded];
    }
}
