using System.Text;
using System.Text.Json;

namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// What the model is asked and how its answer is read back. Copied from the wave-0 pilot's
/// <c>Translator</c> so the stage owns its prompt; the pilot's copy is the one to delete once
/// the coordinator retires <c>Pilot.cs</c>. Structured outputs cannot take the vocabulary's
/// schema (it is recursive), so the constraint is the prompt and the check is the serializer
/// plus <see cref="Linter"/>.
/// </summary>
public static class TranslationPrompt
{
    public const string SystemPrompt = """
        You translate Magic: The Gathering cards into a closed JSON vocabulary that a rules engine executes.
        Output exactly one JSON object, a CardDefinition, and nothing else: no prose, no code fences.

        How to read a card:
        - One Face per printed face, in Scryfall order; role Main for the front, Back for a transform or modal back face, Adventure/Omen for those, Left/Right for split halves.
        - One Ability per line of rules text, in printed order. A line of comma-separated keywords is one KeywordAbility per keyword. Reminder text in parentheses is not an ability.
        - Set "text" on each ability to the printed line it came from. Set "oracleText" on each face to the face's full text.
        - Keyword abilities: use KeywordAbility with cost as printed ("{1}{G}"), n for a number, "of" as an ObjectFilter for "protection from red", "landwalk", "affinity for artifacts", "enchant creature".
        - "When/Whenever/At" is a TriggeredAbility; "[cost]: [effect]" is an ActivatedAbility; an instant or sorcery's text is one SpellAbility; "as long as", "get +1/+1", "can't" and cost changes are StaticAbility; "if ... would ... instead" and "as ... enters" are ReplacementAbility.
        - Selectors: Self for the card itself; Target for "target"; Each for "each"/"all"; Choose for "choose"; Attached for "equipped creature"/"enchanted creature"; Triggering for "it"/"that creature" inside a trigger; Top for "the top card of your library".
        - When a later step refers back to something, give the producing step a key ("key", "rememberAs", "storeAs") and refer to it with Remembered, RememberedPlayer, Stored, ChosenNumber or ChosenPlayer. Keys are lowercase words.
        - Amounts are records: {"kind":"Fixed","n":3}. X is {"kind":"X"}. Mana is printed text: "{2}{G}", "{X}{R}".
        - Colors, card types and supertypes are arrays of names: ["Green"], ["Creature"], ["Legendary"]. Subtypes are strings.
        - "You" is {"kind":"You"}; "each opponent" is {"kind":"EachOpponent"}; "target player" is TargetPlayer.
        - Tokens: a TokenSpec with name, colors, types, subtypes, power, toughness and abilities; copy the token's data exactly when it is given to you, including scryfallOracleId.
        - If any part of the text cannot be expressed in the vocabulary, put {"kind":"Unsupported","reason":"...","text":"..."} in that position. Never approximate.
        - Omit optional properties you do not need. Do not invent properties: only the parameters listed in the vocabulary exist.
        """;

    /// <summary>The cached system prefix: the instructions plus the generated vocabulary reference.</summary>
    public static string SystemWithVocabulary() => SystemPrompt + "\n\n" + VocabularyReference.Text();

    /// <summary>A prompt version that changes whenever the system text does, so stored readings never mix.</summary>
    public static string VersionOf(string label) => label + "-" + Hash(SystemWithVocabulary());

    public static string UserMessage(CorpusCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var payload = new
        {
            name = card.Name,
            layout = card.Layout,
            keywords = card.Keywords,
            faces = card.Faces.Select(f => new { name = f.Name, type_line = f.TypeLine, mana_cost = f.ManaCost, oracle_text = f.OracleText, power = f.Power, toughness = f.Toughness, loyalty = f.Loyalty, defense = f.Defense }),
            tokens = card.Tokens.Select(t => new { scryfall_oracle_id = t.OracleId, name = t.Name, type_line = t.TypeLine, colors = t.Colors, oracle_text = t.OracleText, power = t.Power, toughness = t.Toughness }),
        };
        return "Translate this card. oracleId is \"" + card.OracleId + "\".\n" + JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>What the repair round says about a rejected answer.</summary>
    public static string RepairInstruction(string problem) =>
        "That answer was rejected: " + problem + "\nReturn the corrected CardDefinition JSON object only.";

    public static (CardDefinition? Definition, string? Error) Parse(string text)
    {
        var json = Unfence(text);
        try
        {
            return (CardFileSerializer.ReadDefinition(json), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>A model that wraps its JSON in a fence or a sentence still gets read.</summary>
    private static string Unfence(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && end > firstLine)
                trimmed = trimmed[(firstLine + 1)..end].Trim();
        }

        var start = trimmed.IndexOf('{', StringComparison.Ordinal);
        var stop = trimmed.LastIndexOf('}');
        return start >= 0 && stop > start ? trimmed[start..(stop + 1)] : trimmed;
    }

    public static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return System.Convert.ToHexStringLower(bytes)[..12];
    }
}
