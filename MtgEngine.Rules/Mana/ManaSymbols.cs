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

    /// <summary>
    /// "{G/P}": a Phyrexian symbol, CR 107.4f — one mana of the colours in <see cref="Colors"/>,
    /// or 2 life. It counts as one towards the mana value (CR 202.3d) and its colours are the
    /// card's (CR 202.2), which is why the flag rides beside <see cref="Colors"/> rather than
    /// replacing them.
    /// </summary>
    public bool IsPhyrexian { get; init; }

    /// <summary>The generic half of a monocoloured hybrid symbol, CR 107.4e: "{2/B}" is 2.</summary>
    public int HybridGeneric { get; init; }

    /// <summary>A symbol paid with any type of mana: generic (including "{0}"), X or snow.</summary>
    public bool IsAnyType => Colors == Color.Colorless && !AllowsColorless && HybridGeneric == 0;
}

/// <summary>
/// Printed mana, parsed and paid. The parser reads a cost exactly as it is printed; the payer
/// (<see cref="Pay"/>, in the sibling file) spends it out of a <see cref="ManaPool"/> — coloured
/// symbols from their colour, generic from anything, hybrid either way, Phyrexian from the colour
/// or from 2 life, X from the announcement — and refuses naming CR 601.2h when nothing the player
/// has can cover it, because partial payments are not allowed.
/// </summary>
[RuleRef("106")]
public static partial class ManaSymbols
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

    /// <summary>
    /// The mana value of a parsed cost, CR 202.3: generic plus one per other symbol, with X
    /// counted as announced. CR 202.3d: a Phyrexian symbol counts as one however it is paid, so
    /// it is read before the shape of the rest of the symbol is.
    /// </summary>
    [RuleRef("202.3d")]
    public static int ManaValue(IReadOnlyList<ManaSymbol> cost, int x = 0)
    {
        ArgumentNullException.ThrowIfNull(cost);
        var total = 0;
        foreach (var symbol in cost)
        {
            if (symbol.IsX)
                total += x;
            else if (symbol.IsPhyrexian)
                total++;                        // CR 202.3d: {G/P} counts as 1, paid either way.
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

    /// <summary>
    /// The key under which a stack entry carries the colours an activation announced for the mana
    /// its effect adds, CR 608.2d: one letter per mana in the order the effect adds them, so an
    /// ability that adds mana of any colour and then does more (CR 605.1a) resolves to the colour
    /// its controller chose rather than asking again.
    /// </summary>
    public const string AnnouncedColorsKey = "mana-colors";

    /// <summary>
    /// The colour announced for the <paramref name="index"/>th mana, CR 608.2d: null when nothing
    /// was announced for it, and a letter that names no colour is refused rather than quietly
    /// ignored, because the choice was made and it was not a legal one.
    /// </summary>
    [RuleRef("608.2d")]
    public static Color? ColorAt(string? colors, int index) =>
        string.IsNullOrEmpty(colors) || index >= colors.Length
            ? null
            : ColorFromChoice(colors[index].ToString())
                ?? throw new Engine.RefusedException("That is not a colour of mana to add.", "608.2d");

    /// <summary>Whether a colour is one of the five, CR 105.1, rather than a set of them or colourless.</summary>
    [RuleRef("105.1")]
    public static bool IsSingleColor(Color color) =>
        color is Color.White or Color.Blue or Color.Black or Color.Red or Color.Green;

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

    /// <summary>
    /// A symbol written as halves divided by slashes: a hybrid (CR 107.4e), a monocoloured hybrid
    /// ("{2/B}"), or a Phyrexian symbol (CR 107.4f), whose "P" half is the 2-life alternative and
    /// so joins a symbol that already names one colour ("{G/P}") or two ("{G/U/P}").
    /// </summary>
    [RuleRef("107.4e")]
    private static bool TryHybrid(string body, out ManaSymbol? symbol, out string? error)
    {
        symbol = null;
        error = null;
        var parts = body.Split('/');
        var phyrexian = parts.Any(p => p.Length == 1 && char.ToUpperInvariant(p[0]) == 'P');
        string[] halves = phyrexian
            ? [.. parts.Where(p => p.Length != 1 || char.ToUpperInvariant(p[0]) != 'P')]
            : parts;
        if (halves.Length != 2 && !(phyrexian && halves.Length == 1))
        {
            error = $"Unknown hybrid symbol {{{body}}}.";
            return false;
        }

        var built = new ManaSymbol { IsPhyrexian = phyrexian };
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
        if (symbol.IsPhyrexian)
            parts.Add("P");
        return "{" + string.Join("/", parts) + "}";
    }
}
