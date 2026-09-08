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
        - HasBasicLandType on an ObjectFilter is "a land with a basic land type" (CR 205.3i): write {"hasBasicLandType":true}. Never Supertype Basic, which is a different card, and never a subtypes list of all five, which would be a second spelling of the same thing. A Subtypes list is "any of": ["Forest", "Island"] is "a Forest or Island card", and a named subset of land types is written that way, never as HasBasicLandType with NotSubtypes.
        - PayOnlyFrom on an ActivatedAbility is "activate only using mana produced by [permanents]" (CR 106.6b) — the filter describes the source permanent. PayOnlyWith is the same rider read as a colour. Set one or the other, never both, and leave both out when any mana pays.
        - OfPlayer on an enchant ability is "enchant player"/"enchant opponent" (CR 702.5b), a PlayerFilter. Use "of" for an object and "ofPlayer" for a player; both together is "enchant creature or player", and neither admits anything.
        - AttachedPlayer is "enchanted player" — the player this Aura is attached to (CR 303.4). It is not You: an Aura on an opponent that says "enchanted player discards a card" is AttachedPlayer.
        - Devotion takes "chosenColorKey" when the card counts devotion to a colour it chose (CR 700.5), naming the key of the earlier ChooseValue(Color) step; it takes "colors" when the colour is printed. One or the other.
        - "Add one mana of any color" is a ManaProduction with a chosen-colour key, not five ManaProductions and not a Choose step beside an AddMana. ManaColors is a scenario step's field, not a card's — never write it into a definition.
        - "Of any type that a land an opponent controls could produce" is one ManaProduction with "landCouldProduce":true and "landCouldProduceOf" set to the filter of those lands (CR 106.1). Omit "landCouldProduceOf" for "a land you control could produce"; never write the filter without the flag.
        - "Add one mana of any type that land produced" is a ManaProduction whose "chosenColorKey" is the literal string "tapped-for-mana-color": every TappedForMana firing stores the colour it observed under that key. Never a second AddMana, never a Choose step, and no step on the card produces that key.
        - "Add one mana of any of the exiled card's colors" is a ManaProduction with "colorsOfRemembered" set to the key the card was remembered under (CR 105.2). Never AnyColor, which is every colour.
        - A cost that the effect refers back to takes "rememberAs": SacrificeCost, DiscardCost and ExileFromZoneCost store what paid them under the key, and "if you do, [it]" names them with a Remembered selector. Leave the key out when nothing refers back.
        - UnsupportedContinuous is the ContinuousEffect half of Unsupported, for a static ability's clause this vocabulary cannot say (CR 604.1). Use it rather than dropping the clause — a StaticAbility slot takes a ContinuousEffect and nothing else — and never for a clause a record already spells.
        - IsRemembered on a filter is "that creature" said inside a filter (CR 603.10), naming a key an earlier step remembered. Use the Remembered selector when the objects are simply acted on; use this predicate only when a filter has to require them and hold other conditions too.
        - ManaCostIs on a filter is the printed mana cost as a list of printed symbol strings, "any of" (CR 202.1): ["{0}", "{1}"]. Use ManaValue whenever the card prints a number; this only where the printed symbols themselves are named.
        - NotInCommanderIdentityColors on a filter is "colors not in your commander's color identity" (CR 903.4). Never a Colors or NotColors list, which would fix a set that depends on the deck.
        - "Activate only using mana of the chosen color" is PayOnlyWithChosenColorKey on the ActivatedAbility, naming the earlier ChooseValue(Color) key. It is the third reading of the same rider as PayOnlyWith and PayOnlyFrom: set at most one of the three.
        - Hideaway's exiled card is named by "rememberAs" on the KeywordAbility (CR 702.75a), and the ability that plays it uses a Remembered selector with that key. Set it only for a keyword that puts an object somewhere.
        - "Put one onto the battlefield tapped and the other into your hand" is ONE Search with "upTo":2, "to" and "tapped" for the first card and "othersTo" (with "othersTapped" only for the battlefield) for the rest (CR 701.23a). Never two Searches: two searches see the library twice and each shuffles.
        - Hideaway's "you may play the exiled card without paying its mana cost" is MayPlay with "withoutPayingManaCost":true and a Remembered selector for the exiled card (CR 702.75a, 118.9). Never MayCast: the exiled card may be a land (CR 305.1), and a play permission plays it. Write MayCast only where the card prints "cast".
        - "You may play lands and cast spells from the top of your library. If you cast a spell this way, pay life equal to its mana value rather than pay its mana cost" is ONE MayPlayFromZone with "topOfLibraryOnly":true and "payLifeEqualToManaValueInstead":true (CR 118.9, 601.2f). Never a second StaticAbility for the second sentence, never SpellsCostLifeInsteadOfMana (every spell of a colour, from any zone), and never beside "withoutPayingManaCost": a card prints one alternative cost.
        - "From among them" — a choice among cards an earlier step revealed, looked at or exiled — is Choose with "among" set to the Remembered selector of that step's key, the filter narrowing the candidates and carrying no "in" (CR 700.2). Never an IsRemembered predicate inside the Choose's filter, whose zone would default to the battlefield. "Any number of" is "upTo":true with no "count"; the cards not chosen are the Remembered key with a filter of "also": NotPredicate(IsRemembered(the choice's key)).
        - IsOneOf on a filter is a selector said inside a filter: "whenever equipped creature dies" is Dies with "also": IsOneOf(Attached), and "whenever that creature attacks" in a delayed trigger is IsOneOf(Triggering) (CR 301.5c). Never AttachedTo(Self), which is the Auras and Equipment hanging on this permanent — the other direction. Keep IsRemembered for a remembered key; IsOneOf(Remembered) is a second spelling of it.
        - TotalAmong and GreatestAmong read "of" once per object "among" admits with that object bound as Self: "creatures you control have total power 10 or greater" is TotalAmong with "of": PowerOf(Self). Never Triggering there, and never the ability's own source.
        - A search whose destination depends on the card found — "search your library for a creature or land card, reveal it, put it into your hand. If it's a land card, put it onto the battlefield tapped instead" — is ONE Search with "to": Hand and "instead": a list of SearchDestination objects, here one with "matching": the land filter, "to": Battlefield and "tapped": true (CR 701.23a). The first entry the card found matches wins; a card no entry admits goes to "to". Never two Searches, never an If around the Search, and never an entry whose "to" and "tapped" equal the search's own.
        - DrawnThisTurn on a filter is "cards in your hand that you drew this turn" (CR 121.1): the filter carries "in": "Hand" and the owner, and "also": {"kind":"DrawnThisTurn"}. Never EnteredThisTurn, which is a permanent entering the battlefield, and never a remembered key — another turn's draw step drew those cards, so no step of this card produced them.
        - "Whenever a card is put into a graveyard from anywhere other than the battlefield" is ONE ZoneChange with "to": "Graveyard" and "notFrom": "Battlefield" (CR 603.6), and "whenever a card leaves your graveyard" is ONE ZoneChange with "from": "Graveyard" and no "to". Set "from" or "notFrom", never both, and never a condition about where the object was: the object is gone when the condition is read. Keep Dies for battlefield-to-graveyard and LeavesBattlefield for leaving the battlefield.
        - "Damage that would reduce your life total to less than 1 reduces it to 1 instead" is a DamageDoesNotReduceLifeBelow with "floor": 1, applied to the player (CR 120.4, 119.3). Never PreventDamage, which prevents an amount whatever the life total is, and never CantLoseGame, which is the other sentence such a card prints — two sentences are two abilities.
        - "You may pay {B} rather than pay this spell's mana cost if [condition]" is an AlternativeCost on the FACE — "alternativeCosts": [{"cost": ..., "onlyIf": ...}] (CR 118.9) — not an ability, not "additionalCosts" (which is paid on top of the mana cost, CR 118.8) and not MayPay (which is a choice made at resolution). Omit "onlyIf" when the line is ungated; a face may print more than one alternative and the caster picks one.
        - "If this card is in your opening hand, you may begin the game with it on the battlefield" is a StaticAbility with "where": "Hand" whose effect is MayBeginGameOnBattlefield (CR 103.6). "And you're not the starting player" goes in "onlyIf", "with a luck counter on it" in "withCounters", "if you do, exile a card from your hand" in "ifYouDo". Never a trigger: there is no turn and no stack before the game begins.
        - "Whenever an opponent attacks with creatures" is AttacksWithCreatures, which fires once per declaration of attackers (CR 508.1) — never Attacks, which fires once for each attacking creature and would repeat the ability. "If two or more of those creatures are attacking you and/or planeswalkers you control" is the ability's "if": a Compare over AttackersAgainst with "who": You. Never CountOf with an attacking filter, which counts the whole combat.
        - "Draw that many cards" after a damage trigger is a Draw whose "n" is {"kind":"TriggeringDamage"} (CR 603.10). Never DamageDealtThisTurn, which totals the turn, and never a stored key: no step of this card dealt the damage.
        - "Until end of turn, whenever [event], [effect]" is ONE DelayedTrigger with "until": {"kind":"UntilEndOfTurn"} and "once": false (CR 603.7c). The duration is how long the ability waits; "once" is how many times it fires. Leave "until" out for a one-shot whose own event ends it ("at the beginning of the next end step").
        - "You gain life equal to the life lost this way" is a "storeAs" key on the LoseLife and a GainLife whose "n" is {"kind":"Stored","key": that key} (CR 119.4). The key holds the life actually lost, totalled across the players the instruction took life from; never a second copy of the LoseLife's own amount.
        - "Target land becomes a copy of this land, except it has this ability" is a BecomesCopyOf with "exceptThisAbility": true (CR 707.9b). Never write the ability out inside "except": that nests one reading of the ability inside another with no end. The flag belongs to an activated or triggered ability, never a static or spell ability.
        - "As an additional cost to cast [spells], you may pay [cost]. Those spells cost [mana] less to cast if you paid this way" is ONE OptionalAdditionalCost on a StaticAbility, with "spells" the filter of spells it rides on, "cost" the optional additional cost and "ifPaid" the CostModifier the payment buys (CR 118.8, 601.2f). Never "additionalCosts" on the face, which is this card's own cost, never AlternativeCost, which replaces a mana cost, and never a bare CostModifier, which would reduce the cost whether or not anything was paid. "This effect reduces only the amount of [colour] mana you pay" is "onlyColor" on that CostModifier with "genericOnly": false — set one or the other, never both.
        - "Excess damage is dealt to that creature's controller instead" is "excessTo" on the same DealDamage that dealt the damage (CR 120.3d, 702.19c), normally a ControllerOf over the selector in "to"; "if the creature you control has trample" goes in "excessIf". Never a second DealDamage and never an If around two copies of one damage instruction: the damage is dealt either way, and only where the excess goes is in question.
        - "Choose up to five {P} worth of modes. You may choose the same mode more than once" is ONE ChooseMode with "budget": {"kind":"Fixed","n":5}, "min": 0, "sameModeTwice": true and a "cost" on every mode — the mana printed in front of that mode (CR 700.2). The budget counts those symbols; "max" is not the number of modes when a budget rules them, so leave it out. A mode with a price needs a budget and a budget needs every mode priced.
        - "Whenever a player draws a card except the first one they draw in each of their draw steps" is a Draws with "exceptFirstInDrawStep": true (CR 121.1, 504.1). It is the opposite of "firstEachTurn", which admits that draw alone, and never both; and it is not "duringDrawStep": false, which refuses the whole step.
        - "Sacrifice a creature. If you do, [effect]" — a mandatory cost with an "if you do" — is ONE PayCost with "then" (CR 608.2c, 118.12). MayPay is for "you may pay", where the player chooses; this one the player pays if they can, and "then" runs only when the whole cost was paid. Never a May around a PayCost that carries "then", which is MayPay's sentence written out, and never a "then" that does nothing.
        - A mode count that depends on the game as the spell is cast — "choose one. If you control a commander as you cast this spell, you may choose both instead" — is ONE ChooseMode with "max": 1, "maxIf": a Controls condition over a filter with "isCommander": true, and "maxIfMet": 2 (CR 601.2b, 700.2). The condition is read at announcement, so never an If around the ChooseMode and never two ChooseModes for one list. Write "maxIfMet" only beside "maxIf", and above "max".
        """;

    /// <summary>
    /// How the JSON itself is shaped, said once because the parser is strict (P18): a member no
    /// record has is a rejected answer, and the member fifteen third-reading answers invented was a
    /// "kind" on a record that has no choice of kinds. The bases that carry it are read from the
    /// serializer's own list, kept to the ones a definition can hold — exactly the "kinds" sections
    /// of the reference — so this rule and the reference cannot drift apart.
    /// </summary>
    public static string OutputRules { get; } = BuildOutputRules();

    /// <summary>The polymorphic bases a CardDefinition can hold: every one the definition schema defines.</summary>
    public static IReadOnlyList<Type> DefinitionBases()
    {
        var defined = CardFileSerializer.DefinitionSchema()["$defs"]!.AsObject();
        return CardFileSerializer.PolymorphicBases.Where(b => defined.ContainsKey(b.Name)).ToList();
    }

    private static string BuildOutputRules()
    {
        var bases = string.Join(", ", DefinitionBases().Select(b => b.Name));
        return "Output rules:\n"
            + "- Only a value of one of these bases carries a \"kind\" member, naming which of that base's kinds it is: " + bases + ".\n"
            + "- No other object has a \"kind\". CardDefinition, Face, TypeLine, PrintedStat, TokenSpec, EmblemSpec, ClassLevel, LevelBand, Mode, CounterSpec, SearchDestination, AlternativeCost, ManaProduction, Comparison, ObjectFilter and PlayerFilter are plain objects of their listed parameters, and a \"kind\" on any of them is an unknown member.\n"
            + "- Every member of every object is one of the parameters the reference lists for that record, in camelCase. The parser refuses an answer with any other member, naming it; there is no lenient read.";
    }

    /// <summary>The cached system prefix: the instructions, the output rules, the later spellings, and the generated vocabulary reference.</summary>
    public static string SystemWithVocabulary() => SystemPrompt + "\n\n" + OutputRules + "\n\n" + LaterSpellings + "\n\n" + VocabularyReference.Text();

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
