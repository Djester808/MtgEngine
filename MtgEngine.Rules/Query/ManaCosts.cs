using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Mana costs as printed, read as numbers: mana value (CR 202.3) and devotion (CR 700.5). The
/// cost string comes from the computed characteristics, so an effect that changed a cost has
/// already been applied by the time this reads it.
/// </summary>
internal static class ManaCosts
{
    /// <summary>
    /// Mana value, CR 202.3: the sum of the symbols. A hybrid symbol counts as its greater half
    /// (CR 202.3b); a Phyrexian symbol counts as its mana half (CR 107.4f); X is the value chosen
    /// for it while the spell is on the stack and zero everywhere else (CR 202.3b).
    /// </summary>
    public static int Value(string? cost, GameObject? obj)
    {
        if (string.IsNullOrEmpty(cost))
            return 0;

        var x = obj is not null && obj.Zone.Zone == Cards.Zone.Stack ? obj.Stack?.X ?? 0 : 0;
        var total = 0;
        foreach (var symbol in Symbols(cost))
            total += SymbolValue(symbol, x);
        return total;
    }

    /// <summary>Devotion, CR 700.5: each mana symbol of one of the given colors in the cost counts one, a hybrid once.</summary>
    public static int Devotion(string? cost, Color colors)
    {
        if (string.IsNullOrEmpty(cost))
            return 0;

        var count = 0;
        foreach (var symbol in Symbols(cost))
        {
            if (symbol.Split('/').Any(half => ColorOf(half) is { } color && (color & colors) != 0))
                count++;
        }

        return count;
    }

    /// <summary>The symbols of a printed cost, without their braces: "{2}{G/U}" is "2" then "G/U".</summary>
    public static IEnumerable<string> Symbols(string cost)
    {
        var start = -1;
        for (var i = 0; i < cost.Length; i++)
        {
            if (cost[i] == '{')
            {
                start = i + 1;
            }
            else if (cost[i] == '}' && start >= 0)
            {
                yield return cost[start..i];
                start = -1;
            }
        }
    }

    private static int SymbolValue(string symbol, int x)
    {
        var greatest = 0;
        foreach (var half in symbol.Split('/'))
        {
            var value = half switch
            {
                "X" or "Y" or "Z" => x, // CR 202.3b
                "P" => 0,               // the life half of a Phyrexian symbol, CR 107.4f
                "" => 0,
                _ when int.TryParse(half, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
                _ => 1,                 // a colored, colorless or snow symbol, CR 107.4
            };
            greatest = Math.Max(greatest, value);
        }

        return greatest;
    }

    private static Color? ColorOf(string half) => half switch
    {
        "W" => Color.White,
        "U" => Color.Blue,
        "B" => Color.Black,
        "R" => Color.Red,
        "G" => Color.Green,
        _ => null,
    };
}
