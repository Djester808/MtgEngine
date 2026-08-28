using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// The mana a player has available (CR 106.4).
/// </summary>
/// <remarks>
/// Empties as each step and phase ends (CR 500.5), which is a turn-based action rather than
/// something a player does. Colourless mana is a kind of its own, not an absence of colour
/// (CR 106.1b), so it is tracked separately from the five colours.
/// </remarks>
/// <summary>How long mana keeps its permission to survive the emptying (CR 500.4).</summary>
public enum ManaPersistence
{
    /// <summary>Nothing is persistent; the pool empties as usual.</summary>
    None,

    /// <summary>"Until end of combat, you don't lose this mana as steps end" - firebending.</summary>
    EndOfCombat,

    /// <summary>"Until end of turn, you don't lose this mana as steps and phases end."</summary>
    EndOfTurn,
}

public sealed record ManaPool
{
    public static readonly ManaPool Empty = new();

    public ImmutableDictionary<ManaColor, int> Colored { get; init; } =
        ImmutableDictionary<ManaColor, int>.Empty;

    /// <summary>Colourless mana, which is its own kind and not "no colour" (CR 106.1b).</summary>
    public int Colorless { get; init; }

    /// <summary>
    /// Mana that may only be spent on some things (CR 106.6), one entry per mana.
    /// </summary>
    /// <remarks>
    /// Held apart from the ordinary pool rather than tagged inside it, because the restriction
    /// travels with the individual mana and not with its colour: a player can hold one green
    /// that may pay for anything and one green that may only pay for a creature spell, and
    /// summing them into "two green" loses the only fact that matters when the bill arrives.
    /// <para>
    /// One entry per mana, so spending is removal rather than arithmetic and two mana under
    /// different restrictions never merge.
    /// </para>
    /// </remarks>
    public ImmutableList<RestrictedMana> Restricted { get; init; } =
        ImmutableList<RestrictedMana>.Empty;

    public int Total => Colored.Values.Sum() + Colorless + Restricted.Count;

    public bool IsEmpty => Total == 0;

    public int this[ManaColor color] => Colored.GetValueOrDefault(color);

    public ManaPool Add(ManaColor color, int amount = 1) => this with
    {
        Colored = Colored.SetItem(color, this[color] + amount),
    };

    public ManaPool AddColorless(int amount = 1) => this with { Colorless = Colorless + amount };

    /// <summary>Adds mana that may only be spent on some things (CR 106.6).</summary>
    public ManaPool AddRestricted(ManaColor? color, ManaRestriction restriction, int amount = 1)
    {
        var added = Restricted;
        for (var i = 0; i < amount; i++)
            added = added.Add(new RestrictedMana(color, restriction));

        return this with { Restricted = added };
    }

    public ManaPool Spend(ManaColor color, int amount = 1)
    {
        var have = this[color];
        return amount > have
            ? throw new InvalidOperationException($"Not enough {color} mana.")
            : this with { Colored = Colored.SetItem(color, have - amount) };
    }

    /// <summary>Both pools' contents together.</summary>
    public ManaPool Plus(ManaPool other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var colored = Colored;
        foreach (var (color, amount) in other.Colored)
            colored = colored.SetItem(color, colored.GetValueOrDefault(color) + amount);

        return this with { Colored = colored, Colorless = Colorless + other.Colorless };
    }

    /// <summary>
    /// The most of each kind that both pools hold, used to clamp a permission down to what is
    /// actually still in the pool.
    /// </summary>
    public ManaPool ClampedTo(ManaPool available)
    {
        ArgumentNullException.ThrowIfNull(available);

        return new ManaPool
        {
            Colored = Colored
                .Select(each => KeyValuePair.Create(
                    each.Key, Math.Min(each.Value, available[each.Key])))
                .Where(each => each.Value > 0)
                .ToImmutableDictionary(),
            Colorless = Math.Min(Colorless, available.Colorless),
        };
    }

    public ManaPool SpendColorless(int amount = 1) =>
        amount > Colorless
            ? throw new InvalidOperationException("Not enough colourless mana.")
            : this with { Colorless = Colorless - amount };

    public bool Equals(ManaPool? other) =>
        other is not null &&
        Colorless == other.Colorless &&
        Colored.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key)
            .SequenceEqual(other.Colored.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key)) &&
        Restricted.OrderBy(r => r.ToString(), StringComparer.Ordinal)
            .SequenceEqual(other.Restricted.OrderBy(r => r.ToString(), StringComparer.Ordinal));

    public override int GetHashCode() =>
        HashCode.Combine(Colorless, Colored.Count, Restricted.Count);

    public override string ToString() =>
        IsEmpty
            ? "(empty)"
            : string.Join(
                ' ',
                Colored.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value}{ManaSymbol.Letter(kv.Key)}")
                    .Concat(Colorless > 0 ? [$"{Colorless}C"] : Array.Empty<string>())
                    .Concat(Restricted.Select(r => r.ToString())));
}

/// <summary>One mana that may only be spent on some things (CR 106.6).</summary>
public readonly record struct RestrictedMana(ManaColor? Color, ManaRestriction Restriction)
{
    public override string ToString() =>
        (Color is { } c ? ManaSymbol.Letter(c).ToString() : "C") + "*";
}

/// <summary>Whether a pool can pay a cost, and what it would cost to do so.</summary>
public static class ManaPayment
{
    /// <summary>
    /// Works out one way to pay the cost from the pool, or null if it cannot be paid.
    /// </summary>
    /// <remarks>
    /// Order matters and is the reason this is not a simple subtraction. The demanding symbols
    /// are paid first — a coloured symbol can only be paid one way, while generic mana takes
    /// anything — because paying generic first can spend the only white mana and then fail on a
    /// {W} that was payable all along. Hybrid sits in between: it has choices, but fewer than
    /// generic, so it goes second.
    /// <para>
    /// <paramref name="variableValue"/> is the value chosen for {X} as the spell was cast
    /// (CR 601.2b); {X} in a cost is otherwise zero.
    /// </para>
    /// </remarks>
    public static ManaPool? Pay(
        ManaPool pool,
        ManaCostSpec cost,
        int variableValue = 0,
        ManaPurpose purpose = ManaPurpose.Other,
        CardType paidFor = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(cost);

        var remaining = pool;

        // Restricted mana goes first, and that is a decision rather than an accident. It can pay
        // for fewer things than ordinary mana can, so spending it last would routinely leave a
        // player holding mana they were allowed to use and a cost they could no longer meet -
        // and the pool empties at end of step either way (CR 500.4), so nothing is saved by
        // hoarding it.
        int TakeRestricted(ManaColor? color)
        {
            var index = remaining.Restricted.FindIndex(
                r => r.Color == color && r.Restriction.Allows(purpose, paidFor));

            if (index < 0)
                return 0;

            remaining = remaining with { Restricted = remaining.Restricted.RemoveAt(index) };
            return 1;
        }

        int TakeAnyRestricted()
        {
            var index = remaining.Restricted.FindIndex(
                r => r.Restriction.Allows(purpose, paidFor));

            if (index < 0)
                return 0;

            remaining = remaining with { Restricted = remaining.Restricted.RemoveAt(index) };
            return 1;
        }
        var generic = variableValue * cost.Symbols.Count(s => s.IsVariable);

        // Coloured and colourless symbols first: exactly one thing pays each of them.
        foreach (var symbol in cost.Symbols.Where(s => !s.IsVariable && !s.IsHybrid))
        {
            if (symbol.IsColorless)
            {
                if (TakeRestricted(null) == 1)
                    continue;

                if (remaining.Colorless < 1)
                    return null;

                remaining = remaining.SpendColorless();
                continue;
            }

            if (symbol.Colors.Count == 1)
            {
                var color = symbol.Colors.Single();
                if (TakeRestricted(color) == 1)
                    continue;

                if (remaining[color] < 1)
                    return null;

                remaining = remaining.Spend(color);
                continue;
            }

            generic += symbol.Generic;
        }

        // Then hybrids, taking whichever half the pool can afford.
        foreach (var symbol in cost.Symbols.Where(s => s.IsHybrid))
        {
            var paid = false;
            foreach (var color in symbol.Colors)
            {
                if (TakeRestricted(color) == 1)
                {
                    paid = true;
                    break;
                }

                if (remaining[color] < 1)
                    continue;

                remaining = remaining.Spend(color);
                paid = true;
                break;
            }

            if (paid)
                continue;

            // A monocoloured hybrid falls back to its generic half; a two-colour hybrid or a
            // phyrexian symbol with no matching mana cannot be paid from the pool at all.
            if (symbol.Generic > 0)
                generic += symbol.Generic;
            else
                return null;
        }

        // Generic last: anything pays it, so it is the least constrained.
        while (generic > 0 && TakeAnyRestricted() == 1)
            generic--;

        foreach (var color in remaining.Colored.Keys.OrderBy(c => c))
        {
            while (generic > 0 && remaining[color] > 0)
            {
                remaining = remaining.Spend(color);
                generic--;
            }
        }

        while (generic > 0 && remaining.Colorless > 0)
        {
            remaining = remaining.SpendColorless();
            generic--;
        }

        return generic > 0 ? null : remaining;
    }

    /// <summary>Whether the pool can pay the cost at all.</summary>
    public static bool CanPay(
        ManaPool pool,
        ManaCostSpec cost,
        int variableValue = 0,
        ManaPurpose purpose = ManaPurpose.Other,
        CardType paidFor = default) =>
        Pay(pool, cost, variableValue, purpose, paidFor) is not null;
}
