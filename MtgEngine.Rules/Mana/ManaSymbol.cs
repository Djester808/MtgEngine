using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Rules.Mana;

/// <summary>
/// One mana symbol from a cost (CR 107.4).
/// </summary>
/// <remarks>
/// The engine needs its own, because <c>MtgEngine.Domain.ValueObjects.ManaCost</c> is lossy by
/// design: <c>CardParser</c> strips hybrid, phyrexian, X, snow and colourless symbols before
/// parsing, so <c>{2/W}</c> arrives as <c>2W</c> and <c>{X}</c> vanishes. That is a fine trade
/// for deck statistics, where only the mana value matters, and useless for paying a cost, where
/// <c>{2/W}</c> means "two generic <em>or</em> one white" and getting it wrong changes what a
/// player can cast.
/// </remarks>
public sealed record ManaSymbol
{
    private ManaSymbol()
    {
    }

    /// <summary>Colours this symbol can be paid with. Empty for generic and colourless.</summary>
    public ImmutableHashSet<ManaColor> Colors { get; private init; } = [];

    /// <summary>Generic mana this symbol asks for, as in <c>{3}</c> or the 2 in <c>{2/W}</c>.</summary>
    public int Generic { get; private init; }

    /// <summary>True for <c>{X}</c>, whose value is chosen as the spell is cast (CR 601.2b).</summary>
    public bool IsVariable { get; private init; }

    /// <summary>True for <c>{C}</c>, which only colourless mana pays (CR 107.4c).</summary>
    public bool IsColorless { get; private init; }

    /// <summary>True for <c>{W/P}</c>, payable with two life instead (CR 107.4f).</summary>
    public bool IsPhyrexian { get; private init; }

    /// <summary>True when more than one thing can pay it: hybrid, or phyrexian.</summary>
    public bool IsHybrid => IsPhyrexian || Colors.Count > 1 || (Colors.Count == 1 && Generic > 0);

    /// <summary>What this symbol contributes to mana value (CR 202.3). {X} counts as zero.</summary>
    public int ManaValue => IsVariable ? 0 : Math.Max(Generic, Colors.IsEmpty ? (IsColorless ? 1 : 0) : 1);

    public static ManaSymbol Generic0(int amount) => new() { Generic = amount };

    public static ManaSymbol Colored(ManaColor color) => new() { Colors = [color] };

    public static ManaSymbol Colorless() => new() { IsColorless = true };

    public static ManaSymbol Variable() => new() { IsVariable = true };

    /// <summary>A hybrid of two colours, <c>{W/U}</c> (CR 107.4e).</summary>
    public static ManaSymbol Hybrid(ManaColor first, ManaColor second) =>
        new() { Colors = [first, second] };

    /// <summary>Monocoloured hybrid, <c>{2/W}</c> (CR 107.4d).</summary>
    public static ManaSymbol MonoHybrid(int generic, ManaColor color) =>
        new() { Colors = [color], Generic = generic };

    /// <summary>Phyrexian, <c>{W/P}</c> (CR 107.4f).</summary>
    public static ManaSymbol Phyrexian(ManaColor color) =>
        new() { Colors = [color], IsPhyrexian = true };

    public override string ToString()
    {
        if (IsVariable)
            return "{X}";

        if (IsColorless)
            return "{C}";

        if (IsPhyrexian)
            return $"{{{Letter(Colors.Single())}/P}}";

        if (Colors.Count == 2)
            return $"{{{string.Join('/', Colors.Select(Letter))}}}";

        if (Colors.Count == 1 && Generic > 0)
            return $"{{{Generic}/{Letter(Colors.Single())}}}";

        return Colors.Count == 1 ? $"{{{Letter(Colors.Single())}}}" : $"{{{Generic}}}";
    }

    internal static char Letter(ManaColor color) => color switch
    {
        ManaColor.White => 'W',
        ManaColor.Blue => 'U',
        ManaColor.Black => 'B',
        ManaColor.Red => 'R',
        ManaColor.Green => 'G',
        _ => 'C',
    };

    internal static ManaColor? FromLetter(char letter) => char.ToUpperInvariant(letter) switch
    {
        'W' => ManaColor.White,
        'U' => ManaColor.Blue,
        'B' => ManaColor.Black,
        'R' => ManaColor.Red,
        'G' => ManaColor.Green,
        _ => null,
    };
}

/// <summary>
/// A whole mana cost, symbol by symbol (CR 202.1).
/// </summary>
public sealed record ManaCostSpec
{
    public static readonly ManaCostSpec Free = new() { Symbols = [] };

    public ImmutableList<ManaSymbol> Symbols { get; init; } = [];

    /// <summary>Mana value: the total, with {X} as zero anywhere but the stack (CR 202.3b).</summary>
    public int ManaValue => Symbols.Sum(s => s.ManaValue);

    public bool HasVariable => Symbols.Any(s => s.IsVariable);

    /// <summary>
    /// Parses a Scryfall-style cost such as <c>{2}{W/U}{X}</c>.
    /// </summary>
    /// <remarks>
    /// Every symbol is kept. The pattern is anchored and bounded, and the input is card text
    /// from our own store rather than anything a player typed, but the timeout is here anyway —
    /// the house rule is that no regex runs untimed over text this code did not author.
    /// </remarks>
    public static ManaCostSpec Parse(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost))
            return Free;

        var symbols = ImmutableList.CreateBuilder<ManaSymbol>();

        foreach (Match match in SymbolPattern.Matches(cost))
        {
            var body = match.Groups[1].Value;
            var parsed = ParseSymbol(body);
            if (parsed is not null)
                symbols.Add(parsed);
        }

        return new ManaCostSpec { Symbols = symbols.ToImmutable() };
    }

    private static ManaSymbol? ParseSymbol(string body)
    {
        if (string.Equals(body, "X", StringComparison.OrdinalIgnoreCase))
            return ManaSymbol.Variable();

        if (string.Equals(body, "C", StringComparison.OrdinalIgnoreCase))
            return ManaSymbol.Colorless();

        if (int.TryParse(body, out var generic))
            return ManaSymbol.Generic0(generic);

        var parts = body.Split('/');
        if (parts.Length == 1)
        {
            var color = ManaSymbol.FromLetter(parts[0][0]);
            return color is null ? null : ManaSymbol.Colored(color.Value);
        }

        if (parts.Length != 2)
            return null;

        // {W/P}: phyrexian (CR 107.4f).
        if (string.Equals(parts[1], "P", StringComparison.OrdinalIgnoreCase))
        {
            var color = ManaSymbol.FromLetter(parts[0][0]);
            return color is null ? null : ManaSymbol.Phyrexian(color.Value);
        }

        // {2/W}: monocoloured hybrid (CR 107.4d).
        if (int.TryParse(parts[0], out var hybridGeneric))
        {
            var color = ManaSymbol.FromLetter(parts[1][0]);
            return color is null ? null : ManaSymbol.MonoHybrid(hybridGeneric, color.Value);
        }

        // {W/U}: hybrid (CR 107.4e).
        var first = ManaSymbol.FromLetter(parts[0][0]);
        var second = ManaSymbol.FromLetter(parts[1][0]);
        return first is null || second is null ? null : ManaSymbol.Hybrid(first.Value, second.Value);
    }

    private static readonly Regex SymbolPattern = new(
        @"\{([^}]{1,4})\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    public override string ToString() => string.Concat(Symbols);

    public bool Equals(ManaCostSpec? other) =>
        other is not null && Symbols.SequenceEqual(other.Symbols);

    public override int GetHashCode() => Symbols.Count;
}
