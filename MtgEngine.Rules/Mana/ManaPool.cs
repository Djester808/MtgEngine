using System.Collections.Immutable;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// The mana a player has available (CR 106.4).
/// </summary>
/// <remarks>
/// Empties as each step and phase ends (CR 500.5), which is a turn-based action rather than
/// something a player does. Colourless mana is a kind of its own, not an absence of colour
/// (CR 106.1b), so it is tracked separately from the five colours.
/// </remarks>
public sealed record ManaPool
{
    public static readonly ManaPool Empty = new();

    public ImmutableDictionary<ManaColor, int> Colored { get; init; } =
        ImmutableDictionary<ManaColor, int>.Empty;

    /// <summary>Colourless mana, which is its own kind and not "no colour" (CR 106.1b).</summary>
    public int Colorless { get; init; }

    public int Total => Colored.Values.Sum() + Colorless;

    public bool IsEmpty => Total == 0;

    public int this[ManaColor color] => Colored.GetValueOrDefault(color);

    public ManaPool Add(ManaColor color, int amount = 1) => this with
    {
        Colored = Colored.SetItem(color, this[color] + amount),
    };

    public ManaPool AddColorless(int amount = 1) => this with { Colorless = Colorless + amount };

    public ManaPool Spend(ManaColor color, int amount = 1)
    {
        var have = this[color];
        return amount > have
            ? throw new InvalidOperationException($"Not enough {color} mana.")
            : this with { Colored = Colored.SetItem(color, have - amount) };
    }

    public ManaPool SpendColorless(int amount = 1) =>
        amount > Colorless
            ? throw new InvalidOperationException("Not enough colourless mana.")
            : this with { Colorless = Colorless - amount };

    public bool Equals(ManaPool? other) =>
        other is not null &&
        Colorless == other.Colorless &&
        Colored.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key)
            .SequenceEqual(other.Colored.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key));

    public override int GetHashCode() => HashCode.Combine(Colorless, Colored.Count);

    public override string ToString() =>
        IsEmpty
            ? "(empty)"
            : string.Join(
                ' ',
                Colored.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value}{ManaSymbol.Letter(kv.Key)}")
                    .Concat(Colorless > 0 ? [$"{Colorless}C"] : Array.Empty<string>()));
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
    public static ManaPool? Pay(ManaPool pool, ManaCostSpec cost, int variableValue = 0)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(cost);

        var remaining = pool;
        var generic = variableValue * cost.Symbols.Count(s => s.IsVariable);

        // Coloured and colourless symbols first: exactly one thing pays each of them.
        foreach (var symbol in cost.Symbols.Where(s => !s.IsVariable && !s.IsHybrid))
        {
            if (symbol.IsColorless)
            {
                if (remaining.Colorless < 1)
                    return null;

                remaining = remaining.SpendColorless();
                continue;
            }

            if (symbol.Colors.Count == 1)
            {
                var color = symbol.Colors.Single();
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
    public static bool CanPay(ManaPool pool, ManaCostSpec cost, int variableValue = 0) =>
        Pay(pool, cost, variableValue) is not null;
}
