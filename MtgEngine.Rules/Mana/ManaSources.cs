using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// Where the mana in a pool came from, CR 106.6. Mana is not an object, so the only handle a rule
/// has on a particular mana is the permanent whose ability produced it and the key the card gave
/// it: "when that mana is spent" (CR 106.6a) reads the key, and "spend only mana produced by
/// artifacts" (CR 106.6b) reads the source. This is the one place that builds those blocks, so a
/// mana ability resolved by the casting system and an <c>AddMana</c> resolved by the interpreter
/// cannot disagree about what the mana remembers.
/// </summary>
[RuleRef("106.6")]
public static class ManaSources
{
    /// <summary>
    /// The delta an "add mana" instruction makes, CR 106.6: the mana itself — a restricted block
    /// when the card said what it may be spent on (CR 106.6a), ordinary mana otherwise — with one
    /// provenance block per colour beside it naming the permanent that produced it.
    /// </summary>
    [RuleRef("106.6")]
    public static ManaPool Added(ManaPool produced, ObjectFilter? spendOnlyOn, ObjectId? source, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(produced);
        var pool = spendOnlyOn is { } only ? RestrictedPool.AsRestricted(produced, only) : produced;
        return pool with { Provenance = Produced(pool, source, key) };
    }

    /// <summary>
    /// The provenance blocks mana just produced carries, CR 106.6: one per colour, restricted and
    /// ordinary mana alike, because a block records where mana came from and not what it may pay.
    /// Mana with no producing permanent — a cost refunded, a test's opening pool — remembers
    /// nothing, so an empty list is the honest answer rather than a block with no source.
    /// </summary>
    [RuleRef("106.6")]
    public static ImmutableList<ManaProvenance> Produced(ManaPool produced, ObjectId? source, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(produced);
        if (source is not { } from)
            return [];

        var blocks = ImmutableList.CreateBuilder<ManaProvenance>();
        var totals = new int[6];
        foreach (var (color, amount) in Every(produced))
            totals[ManaSymbols.Slot(color)] += amount;
        for (var slot = 0; slot < totals.Length; slot++)
        {
            if (totals[slot] > 0)
                blocks.Add(new ManaProvenance(ColorOf(slot), totals[slot], from, key));
        }

        return blocks.ToImmutable();
    }

    /// <summary>
    /// Which provenance blocks a spend takes, CR 106.6a: the same first-in-first-out walk the
    /// restricted pool spends its blocks with, by colour, up to what actually left the pool. The
    /// blocks come back negative, as <see cref="ManaPool.FoldProvenance"/> expects of a delta, so
    /// the same list both empties the pool and reports which mana paid.
    /// </summary>
    [RuleRef("106.6a")]
    public static ImmutableList<ManaProvenance> Spend(ImmutableList<ManaProvenance> held, ManaPool spent)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(spent);
        if (held.IsEmpty)
            return [];

        int[] left = [spent.White, spent.Blue, spent.Black, spent.Red, spent.Green, spent.Colorless];
        var taken = ImmutableList.CreateBuilder<ManaProvenance>();
        foreach (var block in held)
        {
            var slot = ManaSymbols.Slot(block.Color);
            var take = Math.Min(block.Amount, left[slot]);
            if (take <= 0)
                continue;
            left[slot] -= take;
            taken.Add(block with { Amount = -take });
        }

        return taken.ToImmutable();
    }

    /// <summary>
    /// The mana a "spend only mana produced by ..." restriction allows, CR 106.6b: of each colour,
    /// only as much as the matching provenance blocks account for. Mana the filter does not match
    /// is still in the player's pool and still empties as the step ends (CR 106.4) — it simply
    /// cannot pay this cost, exactly as <see cref="ManaSymbols.Only"/> does for the colour half of
    /// the same rule. Mana that remembers no source matches nothing, because nothing produced it.
    /// </summary>
    [RuleRef("106.6b")]
    public static ManaPool From(ManaPool pool, ImmutableList<ManaProvenance> held, Func<ObjectId, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(matches);
        var allowed = new int[6];
        foreach (var block in held)
        {
            if (block.Amount > 0 && matches(block.Source))
                allowed[ManaSymbols.Slot(block.Color)] += block.Amount;
        }

        return new ManaPool(
            Math.Min(pool.White, allowed[0]),
            Math.Min(pool.Blue, allowed[1]),
            Math.Min(pool.Black, allowed[2]),
            Math.Min(pool.Red, allowed[3]),
            Math.Min(pool.Green, allowed[4]),
            Math.Min(pool.Colorless, allowed[5]));
    }

    /// <summary>Every mana in a pool, ordinary and restricted alike, CR 106.6a.</summary>
    private static IEnumerable<(Color Color, int Amount)> Every(ManaPool pool)
    {
        foreach (var pair in ManaSymbols.Amounts(pool))
            yield return pair;
        foreach (var block in pool.Restricted)
            yield return (block.Color, block.Amount);
    }

    private static Color ColorOf(int slot) => slot switch
    {
        0 => Color.White,
        1 => Color.Blue,
        2 => Color.Black,
        3 => Color.Red,
        4 => Color.Green,
        _ => Color.Colorless,
    };
}
