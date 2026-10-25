namespace MtgEngine.Cards;

/// <summary>
/// The printed type line, CR 205, read straight from the Scryfall JSON. No model sees these
/// lines: supertypes and types are a closed word list and the subtypes are whatever follows the
/// em dash, so a template can build the <see cref="TypeLine"/> itself. A word the list does not
/// know makes the whole face refuse to template rather than guess.
/// </summary>
public static partial class PrintedCardMapping
{
    /// <summary>CR 205.2a. "Tribal" is the old spelling of Kindred and the corpus still prints both.</summary>
    private static readonly IReadOnlyDictionary<string, CardType> TypeWords = new Dictionary<string, CardType>(StringComparer.Ordinal)
    {
        ["Artifact"] = CardType.Artifact,
        ["Battle"] = CardType.Battle,
        ["Conspiracy"] = CardType.Conspiracy,
        ["Creature"] = CardType.Creature,
        ["Dungeon"] = CardType.Dungeon,
        ["Enchantment"] = CardType.Enchantment,
        ["Instant"] = CardType.Instant,
        ["Kindred"] = CardType.Kindred,
        ["Tribal"] = CardType.Kindred,
        ["Land"] = CardType.Land,
        ["Phenomenon"] = CardType.Phenomenon,
        ["Plane"] = CardType.Plane,
        ["Planeswalker"] = CardType.Planeswalker,
        ["Scheme"] = CardType.Scheme,
        ["Sorcery"] = CardType.Sorcery,
        ["Vanguard"] = CardType.Vanguard,
    };

    /// <summary>CR 205.4a.</summary>
    private static readonly IReadOnlyDictionary<string, Supertype> SupertypeWords = new Dictionary<string, Supertype>(StringComparer.Ordinal)
    {
        ["Basic"] = Supertype.Basic,
        ["Legendary"] = Supertype.Legendary,
        ["Ongoing"] = Supertype.Ongoing,
        ["Snow"] = Supertype.Snow,
        ["World"] = Supertype.World,
    };

    /// <summary>Subtypes printed as two words; every other word after the em dash is a subtype of its own.</summary>
    private static readonly IReadOnlySet<string> TwoWordSubtypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Time Lord",
    };

    private static readonly IReadOnlyDictionary<string, Layout> LayoutWords = new Dictionary<string, Layout>(StringComparer.Ordinal)
    {
        ["normal"] = Layout.Normal,
        ["split"] = Layout.Split,
        ["flip"] = Layout.Flip,
        ["transform"] = Layout.Transform,
        ["modal_dfc"] = Layout.ModalDfc,
        ["meld"] = Layout.Meld,
        ["leveler"] = Layout.Leveler,
        ["class"] = Layout.Class,
        ["saga"] = Layout.Saga,
        ["adventure"] = Layout.Adventure,
        ["mutate"] = Layout.Mutate,
        ["prototype"] = Layout.Prototype,
        ["battle"] = Layout.Battle,
        ["case"] = Layout.Case,
        ["omen"] = Layout.Omen,
    };

    /// <summary>The type line as the vocabulary spells it, or null when a word is not a type, a supertype or a subtype.</summary>
    [RuleRef("205")]
    public static TypeLine? ParseTypeLine(string printed)
    {
        if (string.IsNullOrWhiteSpace(printed))
            return null;

        // A token's type line leads with "Token", which is not a card type: CR 111.1.
        var text = printed.Trim();
        if (text.StartsWith("Token ", StringComparison.Ordinal))
            text = text["Token ".Length..];

        var halves = text.Split('—', 2, StringSplitOptions.TrimEntries);
        var types = CardType.None;
        var supertypes = Supertype.None;
        foreach (var word in halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (SupertypeWords.TryGetValue(word, out var supertype))
                supertypes |= supertype;
            else if (TypeWords.TryGetValue(word, out var type))
                types |= type;
            else
                return null;
        }

        if (types == CardType.None)
            return null;

        var subtypes = halves.Length > 1 ? Subtypes(halves[1]) : [];
        return new TypeLine(types, supertypes, subtypes.Count == 0 ? null : subtypes);
    }

    /// <summary>Everything after the em dash, one subtype per word except the two-word ones.</summary>
    private static IReadOnlyList<string> Subtypes(string printed)
    {
        var words = printed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subtypes = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            if (i + 1 < words.Length && TwoWordSubtypes.Contains(words[i] + " " + words[i + 1]))
            {
                subtypes.Add(words[i] + " " + words[i + 1]);
                i++;
                continue;
            }

            subtypes.Add(words[i]);
        }

        return subtypes;
    }

    /// <summary>Scryfall's layout string as the vocabulary's enum; anything the engine does not model is Other.</summary>
    public static Layout LayoutOf(string scryfallLayout) =>
        LayoutWords.TryGetValue(scryfallLayout, out var layout) ? layout : Layout.Other;

    /// <summary>
    /// What a face is to its card. The first face uses Main, including on split cards.
    /// </summary>
    public static FaceRole RoleOf(string scryfallLayout, int index) => index == 0
        ? FaceRole.Main
        : LayoutOf(scryfallLayout) switch
        {
            Layout.Split => FaceRole.Right,
            Layout.Flip => FaceRole.Flipped,
            Layout.Adventure => FaceRole.Adventure,
            Layout.Omen => FaceRole.Omen,
            Layout.Meld => FaceRole.MeldResult,
            _ => FaceRole.Back,
        };
}
