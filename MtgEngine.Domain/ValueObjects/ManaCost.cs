using MtgEngine.Domain.Enums;

namespace MtgEngine.Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a mana cost such as {2}{W}{W} or {G}{U}.
/// </summary>
public sealed class ManaCost : IEquatable<ManaCost>
{
    public static readonly ManaCost Zero = new(0, new Dictionary<ManaColor, int>());

    /// <summary>Generic (colorless) mana requirement.</summary>
    public int Generic { get; }

    /// <summary>Colored pip requirements, e.g. White=2 means {W}{W}.</summary>
    public IReadOnlyDictionary<ManaColor, int> Colored { get; }

    /// <summary>Converted mana cost (CMC) / mana value.</summary>
    public int ManaValue => Generic + Colored.Values.Sum();

    /// <summary>Color identity derived from the cost.</summary>
    public IReadOnlySet<ManaColor> ColorIdentity =>
        Colored.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet();

    public bool IsColorless => !ColorIdentity.Any();

    public ManaCost(int generic, Dictionary<ManaColor, int> colored)
    {
        if (generic < 0) throw new ArgumentOutOfRangeException(nameof(generic));
        Generic = generic;
        Colored = colored.Where(kv => kv.Value > 0)
                         .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Parse a mana cost string like "2WW", "RG", "3UBB".
    /// Supports digits for generic and W/U/B/R/G for colored pips.
    /// </summary>
    public static ManaCost Parse(string cost)
    {
        if (string.IsNullOrWhiteSpace(cost)) return Zero;

        var colored = new Dictionary<ManaColor, int>();
        int generic = 0;
        int i = 0;

        while (i < cost.Length)
        {
            if (char.IsDigit(cost[i]))
            {
                int start = i;
                while (i < cost.Length && char.IsDigit(cost[i])) i++;
                generic += int.Parse(cost[start..i]);
            }
            else
            {
                var color = cost[i] switch
                {
                    'W' or 'w' => ManaColor.White,
                    'U' or 'u' => ManaColor.Blue,
                    'B' or 'b' => ManaColor.Black,
                    'R' or 'r' => ManaColor.Red,
                    'G' or 'g' => ManaColor.Green,
                    _ => throw new FormatException($"Unknown mana symbol '{cost[i]}' in cost '{cost}'")
                };
                colored[color] = colored.GetValueOrDefault(color) + 1;
                i++;
            }
        }

        return new ManaCost(generic, colored);
    }

    /// <summary>
    /// Returns true if the given mana pool can pay this cost.
    /// Colored pips must be paid with matching color; generic can be paid with any color.
    /// </summary>
    public bool CanBePaidBy(ManaPool pool)
    {
        var remaining = new Dictionary<ManaColor, int>(pool.Amounts);

        // Pay colored pips first
        foreach (var (color, count) in Colored)
        {
            int available = remaining.GetValueOrDefault(color);
            if (available < count) return false;
            remaining[color] = available - count;
        }

        // Pay generic with whatever is left
        int leftover = remaining.Values.Sum();
        return leftover >= Generic;
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if (Generic > 0) sb.Append(Generic);
        foreach (var (color, count) in Colored)
        {
            var symbol = color switch
            {
                ManaColor.White => 'W',
                ManaColor.Blue  => 'U',
                ManaColor.Black => 'B',
                ManaColor.Red   => 'R',
                ManaColor.Green => 'G',
                _ => 'C'
            };
            sb.Append(new string(symbol, count));
        }
        return sb.ToString();
    }

    public bool Equals(ManaCost? other)
    {
        if (other is null) return false;
        if (Generic != other.Generic) return false;
        if (Colored.Count != other.Colored.Count) return false;
        foreach (var (k, v) in Colored)
            if (!other.Colored.TryGetValue(k, out int ov) || ov != v) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ManaCost mc && Equals(mc);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Generic);
        foreach (var (k, v) in Colored.OrderBy(x => x.Key))
        {
            hash.Add(k);
            hash.Add(v);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// Represents the mana currently in a player's mana pool.
/// </summary>
public sealed class ManaPool
{
    public static readonly ManaPool Empty = new(new Dictionary<ManaColor, int>());

    public IReadOnlyDictionary<ManaColor, int> Amounts { get; }
    public int Total => Amounts.Values.Sum();

    public ManaPool(Dictionary<ManaColor, int> amounts)
    {
        Amounts = amounts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public ManaPool Add(ManaColor color, int count = 1)
    {
        var next = new Dictionary<ManaColor, int>(Amounts);
        next[color] = next.GetValueOrDefault(color) + count;
        return new ManaPool(next);
    }

    public ManaPool Pay(ManaCost cost)
    {
        var remaining = new Dictionary<ManaColor, int>(Amounts);

        foreach (var (color, count) in cost.Colored)
        {
            remaining[color] = remaining.GetValueOrDefault(color) - count;
            if (remaining[color] <= 0) remaining.Remove(color);
        }

        int generic = cost.Generic;
        foreach (var color in remaining.Keys.ToList())
        {
            if (generic <= 0) break;
            int take = Math.Min(generic, remaining[color]);
            remaining[color] -= take;
            generic -= take;
            if (remaining[color] <= 0) remaining.Remove(color);
        }

        if (generic > 0) throw new InvalidOperationException("Cannot pay: insufficient mana.");
        return new ManaPool(remaining);
    }

    public ManaPool Remove(ManaColor color, int count = 1)
    {
        var next = new Dictionary<ManaColor, int>(Amounts);
        int current = next.GetValueOrDefault(color);
        if (current <= count) next.Remove(color);
        else next[color] = current - count;
        return new ManaPool(next);
    }

    public ManaPool Clear() => Empty;
}
