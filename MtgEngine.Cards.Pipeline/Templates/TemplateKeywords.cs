using System.Globalization;
using System.Text.RegularExpressions;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// Keyword lines, CR 702. A keyword-only face's whole text is keywords and their parameters, so
/// the printed line is enough: "Equip {1}" is a cost, "Crew 2" is a number, "Protection from red"
/// and "Islandwalk" are filters. A line this file cannot spell returns null and the face is left
/// to the translator rather than templated wrongly.
/// </summary>
public static partial class Templates
{
    private static readonly IReadOnlyDictionary<string, Color> ColorWords = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Color.White,
        ["blue"] = Color.Blue,
        ["black"] = Color.Black,
        ["red"] = Color.Red,
        ["green"] = Color.Green,
    };

    /// <summary>Type nouns as they are printed inside a keyword's parameter, singular and plural.</summary>
    private static readonly IReadOnlyDictionary<string, CardType> TypeNouns = new Dictionary<string, CardType>(StringComparer.OrdinalIgnoreCase)
    {
        ["artifact"] = CardType.Artifact,
        ["artifacts"] = CardType.Artifact,
        ["creature"] = CardType.Creature,
        ["creatures"] = CardType.Creature,
        ["enchantment"] = CardType.Enchantment,
        ["enchantments"] = CardType.Enchantment,
        ["instant"] = CardType.Instant,
        ["instants"] = CardType.Instant,
        ["sorcery"] = CardType.Sorcery,
        ["sorceries"] = CardType.Sorcery,
        ["land"] = CardType.Land,
        ["lands"] = CardType.Land,
        ["planeswalker"] = CardType.Planeswalker,
        ["planeswalkers"] = CardType.Planeswalker,
        ["battle"] = CardType.Battle,
        ["battles"] = CardType.Battle,
    };

    /// <summary>The land types a landwalk keyword can name, CR 702.14.</summary>
    private static readonly IReadOnlySet<string> LandSubtypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Desert", "Cave", "Gate", "Locus", "Sphere", "Tower", "Urza's",
    };

    /// <summary>
    /// The keyword abilities one printed line spells, one ability per keyword, or null when the
    /// line is not keyword-only. The line is kept on every ability it produced as its Text.
    /// </summary>
    [RuleRef("702")]
    public static IReadOnlyList<KeywordAbility>? KeywordLine(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);
        var line = Reminder().Replace(printed, string.Empty).Trim().TrimEnd('.').Trim();
        if (line.Length == 0)
            return null;

        var text = printed.Trim();

        // Whole line first: "Protection from red, white, and blue" is one keyword with a comma in it.
        if (Keyword(line) is { } whole)
            return [whole with { Text = text }];

        var abilities = new List<KeywordAbility>();
        foreach (var part in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Keyword(part) is not { } keyword)
                return null;
            abilities.Add(keyword with { Text = text });
        }

        return abilities.Count == 0 ? null : abilities;
    }

    /// <summary>One keyword and its parameter, or null when the words are not a keyword this stage knows.</summary>
    [RuleRef("702")]
    public static KeywordAbility? Keyword(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);
        var text = printed.Trim().TrimEnd('.').Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith("Protection from ", StringComparison.OrdinalIgnoreCase))
            return ProtectionFrom(text["Protection from ".Length..]);

        if (text.EndsWith("walk", StringComparison.OrdinalIgnoreCase))
            return Landwalk(text[..^"walk".Length].Trim());

        if (text.StartsWith("Affinity for ", StringComparison.OrdinalIgnoreCase))
            return NounFilter(text["Affinity for ".Length..]) is { } affinity ? new KeywordAbility(KeywordKind.Affinity, Of: affinity) : null;

        if (text.StartsWith("Enchant ", StringComparison.OrdinalIgnoreCase))
            return NounFilter(text["Enchant ".Length..]) is { } enchant ? new KeywordAbility(KeywordKind.Enchant, Of: enchant) : null;

        // CR 702.145 spells the pair as one keyword; the printed line names only one half of it.
        if (text.Equals("Daybound", StringComparison.OrdinalIgnoreCase) || text.Equals("Nightbound", StringComparison.OrdinalIgnoreCase))
            return new KeywordAbility(KeywordKind.DayboundAndNightbound, Detail: text);

        var cost = CostKeyword().Match(text);
        if (cost.Success && Typecycling(cost.Groups[1].Value) is { } typecycling)
            return new KeywordAbility(KeywordKind.Cycling, Cost: cost.Groups[2].Value, Of: typecycling);

        if (cost.Success && VocabularyNames.Keyword(cost.Groups[1].Value) is { } costKind)
            return new KeywordAbility(costKind, Cost: cost.Groups[2].Value);

        var number = NumberKeyword().Match(text);
        if (number.Success && VocabularyNames.Keyword(number.Groups[1].Value) is { } numberKind)
            return new KeywordAbility(numberKind, N: new Fixed(int.Parse(number.Groups[2].Value, CultureInfo.InvariantCulture)));

        return VocabularyNames.Keyword(text) is { } plain ? new KeywordAbility(plain) : null;
    }

    /// <summary>"Protection from red", "protection from creatures", "protection from red and from white", CR 702.16.</summary>
    [RuleRef("702.16")]
    private static KeywordAbility? ProtectionFrom(string rest)
    {
        var colors = Color.Colorless;
        var types = CardType.None;
        foreach (var raw in ProtectionSplit().Split(rest))
        {
            var word = raw.Trim().TrimEnd('.').Trim();
            if (word.StartsWith("from ", StringComparison.OrdinalIgnoreCase))
                word = word["from ".Length..].Trim();
            if (word.Length == 0)
                continue;

            if (ColorWords.TryGetValue(word, out var color))
                colors |= color;
            else if (TypeNouns.TryGetValue(word, out var type))
                types |= type;
            else
                return null;
        }

        if (colors != Color.Colorless && types != CardType.None)
            return null;
        if (colors != Color.Colorless)
            return new KeywordAbility(KeywordKind.Protection, Of: new ObjectFilter(AnyColor: colors));
        if (types != CardType.None)
            return new KeywordAbility(KeywordKind.Protection, Of: new ObjectFilter(AnyTypes: types));
        return null;
    }

    /// <summary>"Islandwalk", "Forestwalk", "Nonbasic landwalk", CR 702.14.</summary>
    [RuleRef("702.14")]
    private static KeywordAbility? Landwalk(string stem)
    {
        if (LandSubtypes.Contains(stem))
            return new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Types: CardType.Land, Subtypes: [stem]));

        if (stem.Equals("land", StringComparison.OrdinalIgnoreCase))
            return new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Types: CardType.Land));

        if (stem.Equals("nonbasic land", StringComparison.OrdinalIgnoreCase))
            return new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Types: CardType.Land, NotSupertypes: Supertype.Basic));

        if (stem.Equals("snow land", StringComparison.OrdinalIgnoreCase))
            return new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Snow));

        if (stem.Equals("legendary land", StringComparison.OrdinalIgnoreCase))
            return new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Legendary));

        return null;
    }

    /// <summary>
    /// What "Forestcycling", "Landcycling" or "Slivercycling" fetches, CR 702.29e. Typecycling is
    /// cycling with a card type in front of it, so it is the Cycling keyword with the type in Of;
    /// null when the word is not a typecycling variant.
    /// </summary>
    [RuleRef("702.29e")]
    private static ObjectFilter? Typecycling(string word)
    {
        const string suffix = "cycling";
        if (!word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = word[..^suffix.Length].Trim();
        if (stem.Length == 0)
            return null;
        if (stem.Equals("land", StringComparison.OrdinalIgnoreCase))
            return new ObjectFilter(Types: CardType.Land);
        if (stem.Equals("basic land", StringComparison.OrdinalIgnoreCase))
            return new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic);
        if (LandSubtypes.Contains(stem))
            return new ObjectFilter(Types: CardType.Land, Subtypes: [stem]);

        // "Slivercycling", "Wizardcycling": the stem is a subtype, printed capitalised and singular.
        return char.IsUpper(stem[0]) && !stem.Contains(' ', StringComparison.Ordinal)
            ? new ObjectFilter(Subtypes: [stem])
            : null;
    }

    /// <summary>The noun phrase a keyword parameter names: "creature", "artifacts", "creature you control".</summary>
    private static ObjectFilter? NounFilter(string printed)
    {
        var text = printed.Trim().TrimEnd('.').Trim();
        PlayerRef? controlled = null;
        if (text.EndsWith(" you control", StringComparison.OrdinalIgnoreCase))
        {
            controlled = new You();
            text = text[..^" you control".Length].Trim();
        }

        if (TypeNouns.TryGetValue(text, out var type))
            return new ObjectFilter(Types: type, ControlledBy: controlled);

        if (text.Equals("permanent", StringComparison.OrdinalIgnoreCase) || text.Equals("permanents", StringComparison.OrdinalIgnoreCase))
            return new ObjectFilter(Permanent: true, ControlledBy: controlled);

        if (LandSubtypes.Contains(text))
            return new ObjectFilter(Types: CardType.Land, Subtypes: [text], ControlledBy: controlled);

        return null;
    }

    /// <summary>True when a printed line is nothing but reminder text, CR 207.2.</summary>
    public static bool IsBlank(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);
        return Reminder().Replace(printed, string.Empty).Trim().Length == 0;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z' \-]*?)\s+((?:\{[^}]+\})+)$")]
    private static partial Regex CostKeyword();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z' \-]*?)\s+(\d+)$")]
    private static partial Regex NumberKeyword();

    [GeneratedRegex(@",\s*(?:and\s+)?|\s+and\s+|\s+or\s+")]
    private static partial Regex ProtectionSplit();
}
