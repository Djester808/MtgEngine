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
        if (generic < 0)
            throw new ArgumentOutOfRangeException(nameof(generic));
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
        if (string.IsNullOrWhiteSpace(cost))
            return Zero;

        var colored = new Dictionary<ManaColor, int>();
        int generic = 0;
        int i = 0;

        while (i < cost.Length)
        {
            if (char.IsDigit(cost[i]))
            {
                int start = i;
                while (i < cost.Length && char.IsDigit(cost[i]))
                    i++;
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

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if (Generic > 0)
            sb.Append(Generic);
        foreach (var (color, count) in Colored)
        {
            var symbol = color switch
            {
                ManaColor.White => 'W',
                ManaColor.Blue => 'U',
                ManaColor.Black => 'B',
                ManaColor.Red => 'R',
                ManaColor.Green => 'G',
                _ => 'C'
            };
            sb.Append(new string(symbol, count));
        }
        return sb.ToString();
    }

    public bool Equals(ManaCost? other)
    {
        if (other is null)
            return false;
        if (Generic != other.Generic)
            return false;
        if (Colored.Count != other.Colored.Count)
            return false;
        foreach (var (k, v) in Colored)
            if (!other.Colored.TryGetValue(k, out int ov) || ov != v)
                return false;
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

