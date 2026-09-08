using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// What paying a mana cost did: the delta the mana-changed event carries, the life that paid the
/// Phyrexian symbols (CR 107.4f), or why the cost could not be paid at all.
/// </summary>
public sealed record ManaPayment(ManaPool Delta, Refusal? Refused, int LifePaid = 0)
{
    public bool Paid => Refused is null;
}

/// <summary>The payer: which mana, and which life, a printed cost takes out of a player.</summary>
public static partial class ManaSymbols
{
    /// <summary>
    /// Spend a cost out of a pool, CR 601.2h. The order is forced: symbols with the fewest ways
    /// to pay them go first, so a pool that can cover the cost always does. Generic mana spends
    /// colorless first and then WUBRG, which never makes a later coloured symbol unpayable
    /// because the coloured symbols were already paid.
    /// <para>
    /// A Phyrexian symbol (CR 107.4f) is paid with one mana of its colour or with 2 life, so it
    /// has one more way to be paid than any other symbol and is taken last of the restrictive
    /// ones. <paramref name="life"/> is the payer's life total, which CR 119.4 caps the life half
    /// at — a player with less than 2 life cannot choose it. <paramref name="preferLife"/> is the
    /// payer announcing the other choice (CR 601.2h), and falls back to the mana when the life is
    /// not there. When the generic part then comes up short, a Phyrexian symbol that took mana
    /// gives it back and pays life instead, so the pool covers the cost whenever any split of it
    /// could — a partial payment is never made (CR 601.2h).
    /// </para>
    /// </summary>
    [RuleRef("601.2h")]
    public static ManaPayment Pay(ManaPool pool, IReadOnlyList<ManaSymbol> cost, int? x = null, int life = 0, bool preferLife = false)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(cost);
        if (cost.Any(s => s.IsX) && x is null)
            return Refuse("X was not announced.", "601.2b");

        int[] available = [pool.White, pool.Blue, pool.Black, pool.Red, pool.Green, pool.Colorless];
        var spent = new int[6];
        var lifeLeft = Math.Max(0, life);
        var lifePaid = 0;
        var generic = 0;

        // The mana slots the Phyrexian symbols took, newest last: each one can still be given
        // back for 2 life if the generic part turns out to need that mana (CR 107.4f).
        var refundable = new List<int>();

        // Restrictive symbols first: {C} can only be colorless, a single colour only that colour,
        // a hybrid either of two, CR 107.4e. Then what any type can pay.
        foreach (var symbol in cost.Where(s => !s.IsAnyType).OrderBy(Options))
        {
            if (symbol.IsPhyrexian)
            {
                if (!PayPhyrexian(symbol, available, spent, refundable, preferLife, ref lifeLeft, ref lifePaid))
                    return Refuse($"There is neither mana nor 2 life to pay {Print(symbol)}.", "107.4f");
                continue;
            }

            if (symbol.HybridGeneric > 0)
            {
                // "{2/B}": one mana of the colour, or the generic half, CR 107.4e.
                if (!TakeOne(available, spent, Slots(symbol), out _))
                    generic += symbol.HybridGeneric;
                continue;
            }

            if (!TakeOne(available, spent, Slots(symbol), out _))
                return Refuse($"The pool has no mana that can pay {Print(symbol)}.", "601.2h");
        }

        var owed = generic + cost.Where(s => s.IsAnyType).Sum(s => s.IsX ? x!.Value : s.IsSnow ? 1 : s.Generic);
        while (owed > 0)
        {
            if (TakeOne(available, spent, [Colorless, 0, 1, 2, 3, 4], out _))
            {
                owed--;
                continue;
            }

            // CR 107.4f: the mana a Phyrexian symbol took is the mana the generic part is short
            // of, and 2 life pays that symbol instead, so the whole cost is paid rather than none.
            if (refundable.Count == 0 || lifeLeft < 2)
                return Refuse($"The pool is {owed} mana short of the generic cost.", "601.2h");

            var slot = refundable[^1];
            refundable.RemoveAt(refundable.Count - 1);
            available[slot]++;
            spent[slot]--;
            lifeLeft -= 2;
            lifePaid += 2;
        }

        var delta = new ManaPool(-spent[0], -spent[1], -spent[2], -spent[3], -spent[4], -spent[Colorless]);
        return new ManaPayment(delta, null, lifePaid);
    }

    /// <summary>
    /// One Phyrexian symbol, CR 107.4f: one mana of the colour it names, or 2 life. The payer's
    /// announcement (CR 601.2h) picks which is tried first; CR 119.4 forbids the life half to a
    /// player who does not have 2 life, so an announced life payment they cannot make still takes
    /// the mana rather than refusing a cost the pool can pay.
    /// </summary>
    [RuleRef("107.4f")]
    private static bool PayPhyrexian(
        ManaSymbol symbol,
        int[] available,
        int[] spent,
        List<int> refundable,
        bool preferLife,
        ref int lifeLeft,
        ref int lifePaid)
    {
        if (!preferLife && TakeOne(available, spent, Slots(symbol), out var taken))
        {
            refundable.Add(taken);
            return true;
        }

        if (lifeLeft >= 2)
        {
            lifeLeft -= 2;
            lifePaid += 2;
            return true;
        }

        if (preferLife && TakeOne(available, spent, Slots(symbol), out var instead))
        {
            refundable.Add(instead);
            return true;
        }

        return false;
    }

    /// <summary>
    /// How many pool slots could pay this symbol; the payer takes the tightest first. A Phyrexian
    /// symbol has one more way than any symbol can have slots (CR 107.4f: 2 life), so it sorts
    /// after every other restrictive symbol and never takes mana one of them needed.
    /// </summary>
    private static int Options(ManaSymbol symbol) => Slots(symbol).Count + (symbol.IsPhyrexian ? 10 : 0);

    private static List<int> Slots(ManaSymbol symbol)
    {
        var slots = new List<int>();
        for (var i = 0; i < Wubrg.Length; i++)
        {
            if (symbol.Colors.HasFlag(Wubrg[i]) && symbol.Colors != Color.Colorless)
                slots.Add(i);
        }

        if (symbol.AllowsColorless)
            slots.Add(Colorless);
        return slots;
    }

    private static bool TakeOne(int[] available, int[] spent, IReadOnlyList<int> slots, out int taken)
    {
        foreach (var slot in slots)
        {
            if (available[slot] <= 0)
                continue;
            available[slot]--;
            spent[slot]++;
            taken = slot;
            return true;
        }

        taken = -1;
        return false;
    }

    private static ManaPayment Refuse(string reason, string rule) => new(ManaPool.Empty, new Refusal(reason, rule));
}
