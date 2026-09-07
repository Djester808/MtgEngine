using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// One symbol of a printed mana cost, CR 107.4. A symbol is generic ("{2}"), variable
/// ("{X}", CR 107.3), colored ("{G}"), hybrid ("{G/U}" — either colour, CR 107.4e),
/// monocoloured hybrid ("{2/B}" — one black or two of any type), the colorless symbol
/// ("{C}", CR 107.4c) or snow ("{S}", CR 107.4h).
/// </summary>
[RuleRef("107.4")]
public sealed record ManaSymbol
{
    /// <summary>Generic mana this symbol asks for, CR 107.4b. Paid with any type.</summary>
    public int Generic { get; init; }

    /// <summary>"{X}": generic mana whose amount is announced on casting, CR 107.3.</summary>
    public bool IsX { get; init; }

    /// <summary>The colours that may pay it. Two flags for a hybrid symbol, CR 107.4e.</summary>
    public Color Colors { get; init; }

    /// <summary>"{C}", or the colorless half of "{C/W}": colorless mana may pay it, CR 107.4c.</summary>
    public bool AllowsColorless { get; init; }

    /// <summary>"{S}": one mana of any type from a snow source, CR 107.4h. Snow sources are not tracked in this slice.</summary>
    public bool IsSnow { get; init; }

    /// <summary>The generic half of a monocoloured hybrid symbol, CR 107.4e: "{2/B}" is 2.</summary>
    public int HybridGeneric { get; init; }

    /// <summary>A symbol paid with any type of mana: generic (including "{0}"), X or snow.</summary>
    public bool IsAnyType => Colors == Color.Colorless && !AllowsColorless && HybridGeneric == 0;
}

/// <summary>What paying a mana cost did: the delta for <see cref="ManaChanged"/>, or why it could not be paid.</summary>
public sealed record ManaPayment(ManaPool Delta, Refusal? Refused)
{
    public bool Paid => Refused is null;
}

/// <summary>
/// Printed mana, parsed and paid. The parser reads a cost exactly as it is printed; the payer
/// spends it out of a <see cref="ManaPool"/> — coloured symbols from their colour, generic from
/// anything, hybrid either way, X from the announcement — and refuses naming CR 601.2h when
/// the pool cannot cover it, because partial payments are not allowed.
/// </summary>
[RuleRef("106")]
public static class ManaSymbols
{
    /// <summary>Pool slots in WUBRG order, then colorless, CR 106.1.</summary>
    private const int Colorless = 5;

    private static readonly Color[] Wubrg = [Color.White, Color.Blue, Color.Black, Color.Red, Color.Green];

    /// <summary>Parse printed mana. Throws <see cref="RefusedException"/> naming CR 107.4 on a symbol this slice does not know.</summary>
    public static ImmutableList<ManaSymbol> Parse(string? printed)
    {
        if (!TryParse(printed, out var symbols, out var error))
            throw new RefusedException(error!, "107.4");
        return symbols;
    }

    /// <summary>Parse printed mana without throwing. An empty or absent cost parses to no symbols, CR 202.1.</summary>
    public static bool TryParse(string? printed, out ImmutableList<ManaSymbol> symbols, out string? error)
    {
        symbols = [];
        error = null;
        if (string.IsNullOrWhiteSpace(printed))
            return true;

        var built = ImmutableList.CreateBuilder<ManaSymbol>();
        var text = printed.Trim();
        var at = 0;
        while (at < text.Length)
        {
            if (text[at] != '{')
            {
                error = $"Mana cost is not a sequence of symbols at index {at}.";
                return false;
            }

            var close = text.IndexOf('}', at);
            if (close < 0)
            {
                error = $"Unclosed mana symbol at index {at}.";
                return false;
            }

            var body = text[(at + 1)..close];
            if (!TrySymbol(body, out var symbol, out error))
                return false;
            built.Add(symbol!);
            at = close + 1;
        }

        symbols = built.ToImmutable();
        return true;
    }

    /// <summary>The mana value of a parsed cost, CR 202.3: generic plus one per other symbol, with X counted as announced.</summary>
    public static int ManaValue(IReadOnlyList<ManaSymbol> cost, int x = 0)
    {
        ArgumentNullException.ThrowIfNull(cost);
        var total = 0;
        foreach (var symbol in cost)
        {
            if (symbol.IsX)
                total += x;
            else if (symbol.HybridGeneric > 0)
                total += symbol.HybridGeneric;  // CR 202.3f: {2/B} counts as 2.
            else if (symbol.IsSnow)
                total++;
            else if (symbol.IsAnyType)
                total += symbol.Generic;        // {2} is 2 and {0} is 0.
            else
                total++;                        // a coloured, hybrid or colorless symbol is one.
        }

        return total;
    }

    /// <summary>
    /// Spend a cost out of a pool, CR 601.2h. The order is forced: symbols with the fewest ways
    /// to pay them go first, so a pool that can cover the cost always does. Generic mana spends
    /// colorless first and then WUBRG, which never makes a later coloured symbol unpayable
    /// because the coloured symbols were already paid.
    /// </summary>
    [RuleRef("601.2h")]
    public static ManaPayment Pay(ManaPool pool, IReadOnlyList<ManaSymbol> cost, int? x = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(cost);
        if (cost.Any(s => s.IsX) && x is null)
            return Refuse("X was not announced.", "601.2b");

        int[] available = [pool.White, pool.Blue, pool.Black, pool.Red, pool.Green, pool.Colorless];
        var spent = new int[6];
        var generic = 0;

        // Restrictive symbols first: {C} can only be colorless, a single colour only that colour,
        // a hybrid either of two, CR 107.4e. Then what any type can pay.
        foreach (var symbol in cost.Where(s => !s.IsAnyType).OrderBy(Options))
        {
            if (symbol.HybridGeneric > 0)
            {
                // "{2/B}": one mana of the colour, or the generic half, CR 107.4e.
                if (!TakeOne(available, spent, Slots(symbol)))
                    generic += symbol.HybridGeneric;
                continue;
            }

            if (!TakeOne(available, spent, Slots(symbol)))
                return Refuse($"The pool has no mana that can pay {Print(symbol)}.", "601.2h");
        }

        generic += cost.Where(s => s.IsAnyType).Sum(s => s.IsX ? x!.Value : s.IsSnow ? 1 : s.Generic);
        for (var paid = 0; paid < generic; paid++)
        {
            if (!TakeOne(available, spent, [Colorless, 0, 1, 2, 3, 4]))
                return Refuse($"The pool is {generic - paid} mana short of the generic cost.", "601.2h");
        }

        return new ManaPayment(new ManaPool(-spent[0], -spent[1], -spent[2], -spent[3], -spent[4], -spent[Colorless]), null);
    }

    /// <summary>
    /// What an "add" instruction produces, CR 106.1: each coloured symbol is one mana of that
    /// colour, "{C}" is one colorless, and a number is that much colorless, since there is no
    /// such thing as generic mana in a pool, CR 106.1b.
    /// </summary>
    [RuleRef("106.1")]
    public static ManaPool Produced(string? printed) => Produced(Parse(printed));

    /// <summary>What a parsed production produces. A hybrid or "{X}" in a production is not mana and is ignored.</summary>
    public static ManaPool Produced(IReadOnlyList<ManaSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var made = new int[6];
        foreach (var symbol in symbols)
        {
            if (symbol.Generic > 0)
                made[Colorless] += symbol.Generic;
            else if (symbol.AllowsColorless && symbol.Colors == Color.Colorless)
                made[Colorless]++;
            else if (SingleColor(symbol.Colors) is { } only)
                made[only]++;
        }

        return new ManaPool(made[0], made[1], made[2], made[3], made[4], made[Colorless]);
    }

    /// <summary>One mana of a named colour, for "add one mana of any colour" once the colour is chosen.</summary>
    public static ManaPool OfColor(Color color)
    {
        var made = new int[6];
        made[Slot(color)]++;
        return new ManaPool(made[0], made[1], made[2], made[3], made[4], made[Colorless]);
    }

    /// <summary>The pool slot a colour of mana sits in, CR 106.1: WUBRG in order, then colorless.</summary>
    [RuleRef("106.1")]
    public static int Slot(Color color) => SingleColor(color) ?? Colorless;

    /// <summary>
    /// A pool read as colour and amount, CR 106.1b: there is no generic mana in a pool, so what
    /// is not one of the five colours is <see cref="Color.Colorless"/>.
    /// </summary>
    [RuleRef("106.1b")]
    public static IEnumerable<(Color Color, int Amount)> Amounts(ManaPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        yield return (Color.White, pool.White);
        yield return (Color.Blue, pool.Blue);
        yield return (Color.Black, pool.Black);
        yield return (Color.Red, pool.Red);
        yield return (Color.Green, pool.Green);
        yield return (Color.Colorless, pool.Colorless);
    }

    /// <summary>Read a colour from a cost choice: "W", "white", "{G}".</summary>
    public static Color? ColorFromChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return null;
        var text = choice.Trim().Trim('{', '}');
        foreach (var color in Wubrg)
        {
            if (string.Equals(text, color.ToString(), StringComparison.OrdinalIgnoreCase)
                || (text.Length == 1 && Letter(color) == char.ToUpperInvariant(text[0])))
            {
                return color;
            }
        }

        return string.Equals(text, "C", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "colorless", StringComparison.OrdinalIgnoreCase)
            ? Color.Colorless
            : null;
    }

    /// <summary>
    /// The part of a pool a spend restriction allows, CR 106.6b: "activate only using mana
    /// produced by a creature", read as the colours that may be spent. What is not allowed is
    /// still in the player's pool and still empties as the step ends, CR 106.4 — it simply
    /// cannot pay this cost, so the payer is shown a pool with only the allowed colours in it.
    /// </summary>
    [RuleRef("106.6b")]
    public static ManaPool Only(ManaPool pool, Color allowed)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new ManaPool(
            allowed.HasFlag(Color.White) ? pool.White : 0,
            allowed.HasFlag(Color.Blue) ? pool.Blue : 0,
            allowed.HasFlag(Color.Black) ? pool.Black : 0,
            allowed.HasFlag(Color.Red) ? pool.Red : 0,
            allowed.HasFlag(Color.Green) ? pool.Green : 0,
            allowed == Color.Colorless ? pool.Colorless : 0);
    }

    public static ManaPool Add(ManaPool left, ManaPool right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new ManaPool(
            left.White + right.White,
            left.Blue + right.Blue,
            left.Black + right.Black,
            left.Red + right.Red,
            left.Green + right.Green,
            left.Colorless + right.Colorless);
    }

    private static bool TrySymbol(string body, out ManaSymbol? symbol, out string? error)
    {
        symbol = null;
        error = null;
        if (body.Length == 0)
        {
            error = "Empty mana symbol.";
            return false;
        }

        if (body.Contains('P', StringComparison.OrdinalIgnoreCase))
        {
            // CR 107.4f: a Phyrexian symbol may be paid with 2 life. Not in this slice.
            error = $"Phyrexian mana {{{body}}} is not supported yet.";
            return false;
        }

        if (body.Contains('/', StringComparison.Ordinal))
            return TryHybrid(body, out symbol, out error);

        if (body.All(char.IsAsciiDigit))
        {
            symbol = new ManaSymbol { Generic = int.Parse(body, System.Globalization.CultureInfo.InvariantCulture) };
            return true;
        }

        if (body.Length == 1)
        {
            switch (char.ToUpperInvariant(body[0]))
            {
                case 'X':
                    symbol = new ManaSymbol { IsX = true };
                    return true;
                case 'C':
                    symbol = new ManaSymbol { AllowsColorless = true };
                    return true;
                case 'S':
                    symbol = new ManaSymbol { IsSnow = true };
                    return true;
                default:
                    if (FromLetter(body[0]) is { } color)
                    {
                        symbol = new ManaSymbol { Colors = color };
                        return true;
                    }

                    break;
            }
        }

        error = $"Unknown mana symbol {{{body}}}.";
        return false;
    }

    private static bool TryHybrid(string body, out ManaSymbol? symbol, out string? error)
    {
        symbol = null;
        error = null;
        var halves = body.Split('/');
        if (halves.Length != 2)
        {
            error = $"Unknown hybrid symbol {{{body}}}.";
            return false;
        }

        var built = new ManaSymbol();
        foreach (var half in halves)
        {
            if (half.All(char.IsAsciiDigit) && half.Length > 0)
                built = built with { HybridGeneric = int.Parse(half, System.Globalization.CultureInfo.InvariantCulture) };
            else if (half.Length == 1 && char.ToUpperInvariant(half[0]) == 'C')
                built = built with { AllowsColorless = true };
            else if (half.Length == 1 && FromLetter(half[0]) is { } color)
                built = built with { Colors = built.Colors | color };
            else
            {
                error = $"Unknown hybrid symbol {{{body}}}.";
                return false;
            }
        }

        symbol = built;
        return true;
    }

    /// <summary>How many pool slots could pay this symbol; the payer takes the tightest first.</summary>
    private static int Options(ManaSymbol symbol) => Slots(symbol).Count;

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

    private static bool TakeOne(int[] available, int[] spent, IReadOnlyList<int> slots)
    {
        foreach (var slot in slots)
        {
            if (available[slot] <= 0)
                continue;
            available[slot]--;
            spent[slot]++;
            return true;
        }

        return false;
    }

    private static int? SingleColor(Color color)
    {
        for (var i = 0; i < Wubrg.Length; i++)
        {
            if (color == Wubrg[i])
                return i;
        }

        return null;
    }

    private static char Letter(Color color) => color switch
    {
        Color.White => 'W',
        Color.Blue => 'U',
        Color.Black => 'B',
        Color.Red => 'R',
        Color.Green => 'G',
        _ => 'C',
    };

    private static Color? FromLetter(char letter) => char.ToUpperInvariant(letter) switch
    {
        'W' => Color.White,
        'U' => Color.Blue,
        'B' => Color.Black,
        'R' => Color.Red,
        'G' => Color.Green,
        _ => null,
    };

    private static string Print(ManaSymbol symbol)
    {
        if (symbol.IsX)
            return "{X}";
        if (symbol.Generic > 0)
            return $"{{{symbol.Generic}}}";
        var parts = new List<string>();
        if (symbol.HybridGeneric > 0)
            parts.Add(symbol.HybridGeneric.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var color in Wubrg)
        {
            if (symbol.Colors.HasFlag(color) && symbol.Colors != Color.Colorless)
                parts.Add(Letter(color).ToString());
        }

        if (symbol.AllowsColorless)
            parts.Add("C");
        if (symbol.IsSnow)
            parts.Add("S");
        return "{" + string.Join("/", parts) + "}";
    }

    private static ManaPayment Refuse(string reason, string rule) => new(ManaPool.Empty, new Refusal(reason, rule));
}
