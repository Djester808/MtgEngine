using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// States what is structurally true about a card, so the model weighs arithmetic instead
/// of performing it.
/// </summary>
/// <remarks>
/// There is deliberately no text parsing in this file — no regexes at all.
/// <list type="bullet">
/// <item>
/// <b>Here: fields and arithmetic.</b> Power, toughness, mana value, colour identity, types,
/// creature types, keywords, Game Changer. All columns in the card data. Comparing them is
/// exactly what the model gets wrong: it once called a 3/2 an enabler for a "power 4 or
/// greater" trigger.
/// </item>
/// <item>
/// <b>Elsewhere: reading prose.</b> What the commander requires comes from
/// <see cref="ICommanderAnalysis"/>, which has the model read the sentence once and return a
/// number. How a card's own text should be read is doctrine §0.2.
/// </item>
/// </list>
/// Parsing prose in C# was tried twice and failed the same way twice: matching "enters
/// tapped" flagged every land with a conditional clause as a tapped land, and matching
/// "power N or greater" missed the ~180 printings phrased "or more" or "greater than" —
/// silently, which was the real defect. Enumerating phrasings is a treadmill; a card printed
/// next year would inherit nothing.
/// </remarks>
internal static class CardFactSheet
{
    /// <summary>The card fields the checker needs, so definitions and DTOs share a path.</summary>
    internal readonly record struct FactCard(
        string OracleId,
        string Name,
        string ManaCost,
        int Cmc,
        string? OracleText,
        string TypeLine,
        CardType CardTypes,
        IReadOnlyList<string> Subtypes,
        int? Power,
        int? Toughness,
        IReadOnlyList<ManaColor> ColorIdentity,
        IReadOnlyList<string> Keywords,
        bool GameChanger);

    // ---- The fact sheet -------------------------------------------------

    /// <summary>
    /// States which of the commander's requirements this card meets. Misses are reported as
    /// loudly as hits: left to infer it, the model credits cards with triggers they cannot
    /// turn on (doctrine §9.2).
    /// </summary>
    internal static string For(FactCard card, CommanderRequirements req, DeckProfile? profile)
    {
        var facts = new List<string>();

        AddThresholdFacts(card, req, facts);
        AddColourFacts(card, req, facts);
        AddTribeFacts(card, req, facts);
        AddKeywordFacts(card, req, facts);

        // Mana value is free and the doctrine costs it out constantly — ramp priority,
        // curve, and removal efficiency all key off it.
        facts.Add($"mana value {card.Cmc}");

        if (card.GameChanger)
            facts.Add("is on the official Game Changers list");

        return facts.Count == 0 ? string.Empty : $"  [FACTS: {string.Join("; ", facts)}]";
    }

    private static void AddThresholdFacts(FactCard card, CommanderRequirements req, List<string> facts)
    {
        foreach (var t in req.Thresholds)
        {
            int? actual = t.Attribute switch
            {
                "power" => card.Power,
                "toughness" => card.Toughness,
                "mana value" => card.Cmc,
                _ => null,
            };

            // A threshold on a stat this card does not have says nothing about it.
            // Reporting "power n/a" on an artifact is noise that invites a penalty.
            if (actual is null)
                continue;

            facts.Add(t.IsMetBy(actual)
                ? $"{t.Attribute} {actual} MEETS the commander's \"{t.Describe()}\" requirement"
                : $"{t.Attribute} {actual} does NOT meet the commander's \"{t.Describe()}\" requirement");
        }
    }

    /// <summary>
    /// Colour identity is a field, and for a land it is also the set of colours it can
    /// produce — both mana symbols in the text and basic land types feed it. So fixing is
    /// answered from a field, with no "Add {B}" clause parsed anywhere.
    /// </summary>
    private static void AddColourFacts(FactCard card, CommanderRequirements req, List<string> facts)
    {
        if (req.Colours.Count == 0)
            return;

        var isLand = card.CardTypes.HasFlag(CardType.Land);

        if (card.ColorIdentity.Count == 0)
        {
            facts.Add(isLand
                ? "colourless identity -- produces no coloured mana of its own"
                : "colourless identity -- legal in any deck");
            return;
        }

        var outside = card.ColorIdentity.Where(c => !req.Colours.Contains(c)).ToArray();
        if (outside.Length > 0)
        {
            // Should never reach a scoring prompt; the pool filters on identity. Stated
            // rather than dropped so a filtering bug is visible instead of silent.
            facts.Add($"colour identity {string.Join("", card.ColorIdentity)} is OUTSIDE " +
                      $"the commander's {string.Join("", req.Colours)} -- ILLEGAL in this deck");
            return;
        }

        var covered = req.Colours.Where(card.ColorIdentity.Contains).ToArray();

        if (isLand && covered.Length > 0)
        {
            facts.Add(covered.Length == req.Colours.Count && req.Colours.Count > 1
                ? $"land whose identity covers ALL {covered.Length} of the commander's colours " +
                  $"({string.Join("/", covered)}) -- one card counting as a source for each"
                : $"land whose identity covers {string.Join("/", covered)} of the commander's colours");
        }
    }

    private static void AddTribeFacts(FactCard card, CommanderRequirements req, List<string> facts)
    {
        var shared = req.Tribes
            .Where(t => card.Subtypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (shared.Length > 0)
            facts.Add($"is a {string.Join("/", shared)}, a creature type this deck cares about");
    }

    private static void AddKeywordFacts(FactCard card, CommanderRequirements req, List<string> facts)
    {
        var shared = card.Keywords
            .Where(k => req.NamedKeywords.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (shared.Length > 0)
            facts.Add($"has {string.Join("/", shared)}, named in the commander's text");
    }

    // ---- Adapters --------------------------------------------------------

    internal static IEnumerable<string> KeywordNames(KeywordAbility keywords) =>
        Enum.GetValues<KeywordAbility>()
            .Where(k => k != default && keywords.HasFlag(k))
            .Select(k => k.ToString());

    internal static IEnumerable<string> TypeNames(CardType types) =>
        Enum.GetValues<CardType>()
            .Where(t => t != CardType.None && types.HasFlag(t))
            .Select(t => t.ToString());

    /// <summary>Renders types the way a card reads: "Legendary Creature — Wolf".</summary>
    internal static string TypeLineOf(
        IEnumerable<string> supertypes, IEnumerable<string> cardTypes, IEnumerable<string> subtypes)
    {
        var head = string.Join(" ", supertypes.Concat(cardTypes));
        var tail = string.Join(" ", subtypes);
        return tail.Length > 0 ? $"{head} — {tail}" : head;
    }

    internal static FactCard From(string oracleId, CardDefinition d) => new(
        oracleId, d.Name, d.ManaCostRaw, d.Cmc, d.OracleText,
        TypeLineOf(d.Supertypes, TypeNames(d.CardTypes), d.Subtypes),
        d.CardTypes, d.Subtypes, d.Power, d.Toughness, d.ColorIdentity,
        [.. KeywordNames(d.Keywords)], d.GameChanger);
}
