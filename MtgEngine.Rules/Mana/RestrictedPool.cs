using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// One block of mana that may be spent only on what its filter matches, CR 106.6a. It is
/// otherwise ordinary mana and empties with the rest at the end of each step and phase, CR 106.6b.
/// </summary>
[RuleRef("106.6a")]
public sealed record RestrictedBlock(Color Color, int Amount, ObjectFilter SpendOnlyOn);

/// <summary>
/// How the restricted mana of CR 106.6a is read out of a pool. The field landed on
/// <see cref="State.ManaPool"/> in the seams after wave 6 (seam-requests/E3d.md), so the one
/// implementation reads it; the interface stays because it is the seam a test can stand in for
/// without a static anywhere.
/// </summary>
[RuleRef("106.6a")]
public interface IRestrictedMana
{
    /// <summary>The restricted blocks a pool holds, CR 106.6a.</summary>
    ImmutableList<RestrictedBlock> In(ManaPool pool);
}

/// <summary>The blocks a pool carries, CR 106.6a, which is all a game ever has.</summary>
[RuleRef("106.6a")]
public sealed class NoRestrictedMana : IRestrictedMana
{
    public ImmutableList<RestrictedBlock> In(ManaPool pool) => pool.Restricted;
}

/// <summary>
/// What a payment may spend, CR 106.6a: the ordinary mana with every matching restricted block
/// folded into it, beside the blocks that were folded in, so the spend can be taken back out of
/// the block that paid rather than out of the ordinary counts.
/// </summary>
[RuleRef("106.6a")]
public sealed record SpendableMana(ManaPool Pool, ImmutableList<RestrictedBlock> Offered);

/// <summary>
/// Which of a player's mana may pay for a given object, CR 106.6a: all of their ordinary mana,
/// plus every restricted block whose filter matches what is being paid for. A block that matches
/// nothing is simply not offered to the payment, so it stays in the pool rather than being spent
/// on a cost it may not pay. This is the one reading of what a cost may use.
/// </summary>
[RuleRef("106.6a")]
public sealed class RestrictedPool(IRestrictedMana source)
{
    /// <summary>The pools a game has: the blocks the player's own pool carries, CR 106.6a.</summary>
    public static RestrictedPool Pools { get; } = new(new NoRestrictedMana());

    /// <summary>
    /// Mana produced with a spend restriction, CR 106.6a: what "add {G}. Spend this mana only to
    /// cast creature spells" puts into the pool is one block per colour produced and no ordinary
    /// mana at all. CR 106.6b empties it with the rest at the end of the step regardless.
    /// </summary>
    [RuleRef("106.6a")]
    public static ManaPool AsRestricted(ManaPool produced, ObjectFilter spendOnlyOn)
    {
        var blocks = ImmutableList.CreateBuilder<RestrictedBlock>();
        foreach (var (color, amount) in ManaSymbols.Amounts(produced))
        {
            if (amount > 0)
                blocks.Add(new RestrictedBlock(color, amount, spendOnlyOn));
        }

        return ManaPool.Empty with { Restricted = blocks.ToImmutable() };
    }

    /// <summary>
    /// The delta a spend makes on the pool, CR 106.6a: mana a restricted block offered comes off
    /// that block, and only what is left over comes off the ordinary counts. Restricted mana is
    /// taken first because a block can pay nothing but what its filter matches, while ordinary
    /// mana can still pay anything else in the same step.
    /// </summary>
    [RuleRef("106.6a")]
    public static ManaPool DeltaFor(ManaPool spent, ImmutableList<RestrictedBlock> offered)
    {
        ArgumentNullException.ThrowIfNull(spent);
        int[] left = [spent.White, spent.Blue, spent.Black, spent.Red, spent.Green, spent.Colorless];
        var taken = ImmutableList.CreateBuilder<RestrictedBlock>();
        foreach (var block in offered)
        {
            var slot = ManaSymbols.Slot(block.Color);
            var take = Math.Min(block.Amount, left[slot]);
            if (take <= 0)
                continue;
            left[slot] -= take;
            taken.Add(block with { Amount = -take });
        }

        return new ManaPool(-left[0], -left[1], -left[2], -left[3], -left[4], -left[5]) { Restricted = taken.ToImmutable() };
    }

    /// <summary>
    /// The mana that may be spent on <paramref name="paidFor"/>, CR 106.6a. The object is the
    /// spell or the ability's source being paid for; nothing being paid for means no restricted
    /// block can match, because a block is spendable only on what its filter accepts.
    /// </summary>
    [RuleRef("106.6a")]
    public ManaPool Spendable(GameState state, ManaPool pool, IQuery query, ObjectId? paidFor, EffectContext context) =>
        Offer(state, pool, query, paidFor, context).Pool;

    /// <summary>
    /// <see cref="Spendable"/>, with the blocks it folded in kept beside the pool so the payment
    /// can report which block paid, CR 106.6a.
    /// </summary>
    [RuleRef("106.6a")]
    public SpendableMana Offer(GameState state, ManaPool pool, IQuery query, ObjectId? paidFor, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var blocks = source.In(pool);
        if (blocks.Count == 0)
            return new SpendableMana(pool, []);

        var spendable = pool;
        var offered = ImmutableList.CreateBuilder<RestrictedBlock>();
        foreach (var block in blocks)
        {
            if (block.Amount <= 0 || paidFor is not { } id)
                continue;
            if (!query.Matches(state, block.SpendOnlyOn, id, context))
                continue;
            offered.Add(block);
            for (var i = 0; i < block.Amount; i++)
                spendable = ManaSymbols.Add(spendable, ManaSymbols.OfColor(block.Color));
        }

        return new SpendableMana(spendable, offered.ToImmutable());
    }
}
