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

    /// <summary>
    /// The one-spelling rules for the records the vocabulary gained after the pilot ran (V6 and
    /// V7). <see cref="VocabularyReference"/> lists every record and says how the pilot-era ones
    /// are spelled; these are the later ones, and until they were said here the model had the
    /// record in front of it with nothing telling it which of two readings to write — which is
    /// exactly what cost the pilot 105 cards to disagreement. Each names its record so a test can
    /// prove none of them is missing, and each cites the rule the reading comes from.
    /// </summary>
    public const string LaterSpellings = """
        ## One spelling, continued
        These records were added after the reference's rules were written. They have one reading each:

        - MayPay is the whole of "you may pay [cost]. If you do, [effect]" (CR 601.2b/117.11): the cost in "paid", the effect in "then", "if you don't" in "else", and no "who" when the payer is you. Never a Choose step followed by a condition, and never a Cost inside an ordinary Sequence — an optional cost is not a step.
        - TappedForMana is "whenever [permanent] is tapped for mana" (CR 106.11a). Omit "ofColor" unless the card prints a colour; set "ofChosenColorKey" instead — never both — when the colour is one this card chose earlier, naming the same key the choosing step used.
        - ManaSpent is "when that mana is spent" (CR 603.7a): "manaKey" is the key of the AddMana in this same ability that made the mana, because mana is not an object and cannot be remembered as one. Omit "onSpell" when any spell the mana pays for counts.
        - CreatedTokenThisTurn is "if you created a token this turn" (CR 111.1). Omit "what" when any token counts; name it only when the card describes the token.
        - HasBasicLandType on an ObjectFilter is "a land with a basic land type" (CR 205.3i): write {"hasBasicLandType":true}. Never Supertype Basic, which is a different card, and never a subtypes list, which is joined with "and" and cannot say "any of the five".
        - PayOnlyFrom on an ActivatedAbility is "activate only using mana produced by [permanents]" (CR 106.6b) — the filter describes the source permanent. PayOnlyWith is the same rider read as a colour. Set one or the other, never both, and leave both out when any mana pays.
        - OfPlayer on an enchant ability is "enchant player"/"enchant opponent" (CR 702.5b), a PlayerFilter. Use "of" for an object and "ofPlayer" for a player; both together is "enchant creature or player", and neither admits anything.
        - AttachedPlayer is "enchanted player" — the player this Aura is attached to (CR 303.4). It is not You: an Aura on an opponent that says "enchanted player discards a card" is AttachedPlayer.
        - Devotion takes "chosenColorKey" when the card counts devotion to a colour it chose (CR 700.5), naming the key of the earlier ChooseValue(Color) step; it takes "colors" when the colour is printed. One or the other.
        - "Add one mana of any color" is a ManaProduction with a chosen-colour key, not five ManaProductions and not a Choose step beside an AddMana. ManaColors is a scenario step's field, not a card's — never write it into a definition.
        """;

    /// <summary>The cached system prefix: the instructions, the later spellings, and the generated vocabulary reference.</summary>
    public static string SystemWithVocabulary() => SystemPrompt + "\n\n" + LaterSpellings + "\n\n" + VocabularyReference.Text();

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
