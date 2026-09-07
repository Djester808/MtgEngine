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
/// How the restricted mana of CR 106.6a is read out of a pool. <see cref="State.ManaPool"/> is a
/// seam (<c>State/PlayerState.cs</c>) and does not carry the blocks yet, so casting reads them
/// through this interface: when the field lands, <see cref="NoRestrictedMana"/> becomes one line
/// returning <c>pool.Restricted</c> and nothing else in casting changes. seam-requests/E3d.md.
/// </summary>
[RuleRef("106.6a")]
public interface IRestrictedMana
{
    /// <summary>The restricted blocks a pool holds, CR 106.6a.</summary>
    ImmutableList<RestrictedBlock> In(ManaPool pool);
}

/// <summary>
/// The reading until the state carries the field: no pool holds restricted mana, so a card whose
/// <see cref="AddMana.SpendOnlyOn"/> is set produces ordinary mana. That is the one gap in this
/// area that refuses nothing, named here and in seam-requests/E3d.md so it is not forgotten, and
/// held by <c>CastingChoiceTests</c>.
/// </summary>
[RuleRef("106.6a")]
public sealed class NoRestrictedMana : IRestrictedMana
{
    public ImmutableList<RestrictedBlock> In(ManaPool pool) => pool.Restricted;
}

/// <summary>
/// Which of a player's mana may pay for a given object, CR 106.6a: all of their ordinary mana,
/// plus every restricted block whose filter matches what is being paid for. A block that matches
/// nothing is simply not offered to the payment, so it stays in the pool rather than being spent
/// on a cost it may not pay.
/// </summary>
[RuleRef("106.6a")]
public sealed class RestrictedPool(IRestrictedMana source)
{
    /// <summary>The pools a game has today: ordinary mana only, until the state seam lands.</summary>
    public static RestrictedPool Pools { get; } = new(new NoRestrictedMana());

    /// <summary>
    /// The mana that may be spent on <paramref name="paidFor"/>, CR 106.6a. The object is the
    /// spell or the ability's source being paid for; nothing being paid for means no restricted
    /// block can match, because a block is spendable only on what its filter accepts.
    /// </summary>
    [RuleRef("106.6a")]
    public ManaPool Spendable(GameState state, ManaPool pool, IQuery query, ObjectId? paidFor, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var blocks = source.In(pool);
        if (blocks.Count == 0)
            return pool;

        var spendable = pool;
        foreach (var block in blocks)
        {
            if (block.Amount <= 0 || paidFor is not { } id)
                continue;
            if (!query.Matches(state, block.SpendOnlyOn, id, context))
                continue;
            for (var i = 0; i < block.Amount; i++)
                spendable = ManaSymbols.Add(spendable, ManaSymbols.OfColor(block.Color));
        }

        return spendable;
    }
}
