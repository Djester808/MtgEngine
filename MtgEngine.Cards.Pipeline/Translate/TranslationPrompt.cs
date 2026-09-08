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
        - "Whenever an opponent searches their library" is a Searches whose "who" is a PlayerFilter with "opponent": true (CR 701.19) — the searcher is described, not named, so never a PlayerRef and never EachOpponent. It fires once per search whatever that search finds. Never the Search effect, which is this card telling someone to search, and never a Mills or a ZoneChange: nothing has left the library when it fires.
        - "Each player secretly chooses a number, then those numbers are revealed" is ONE ChooseNumberSecretly with a "key" (CR 700.2, 101.4), and the numbers are read back with HighestChosen, LowestChosen and the PlayerWhoChose player reference ("lowest": true for "each player who chose the lowest"), all naming that same key. Never ChooseValue with Number, which is one player choosing in the open, and never a ForEachPlayer around one, which would let each player see the choices before theirs. The reveal is part of the record; a step nothing reads back is refused.
        - "For each card you have drawn this turn" is a CardsDrawnThisTurn amount and "for each land that entered the battlefield under your control this turn" is a LandsEnteredThisTurn amount, both taking the player (CR 121.1). Never CountOf over a filter with DrawnThisTurn or EnteredThisTurn, which counts what is still in the hand or still on the battlefield and reads a smaller number the moment one leaves.
        - "Enchanted permanent is [a card name]" is a BecomesCopyOfNamed whose "name" is that printed card name (CR 707.9, 613.1a) — the one place a card name is data, because the copied card is not in the game. Never BecomesCopyOf, whose "of" needs an object in the game, never a Named filter, and never ChangeText. The name must be a real card: the pipeline checks it against the corpus and refuses an unknown one.
        - "Target spell that targets a permanent you control" is a Targets predicate in the spell filter "also", holding the filter of what that spell targets (CR 115.1, 601.2c). It is the opposite direction from TargetedBy, which describes the object being pointed at; the filter around it carries "spell": true and "in": "Stack", and a Targets on a permanent filter is refused.
        - "Can not cast spells from anywhere other than their hands" is a CantCastSpells whose "matching" filter has "spell": true and "notCastFrom": "Hand" (CR 601.3); "castFrom" is the same rider read the other way for a card that names the zone it allows. Set one or the other, never both, and only on a filter that describes a spell. Never the CastFromZone condition, which asks where THIS card was cast from.
        - "As long as this permanent is harnessed" is the static ability "while": a Matches over Self whose filter carries "also": {"kind":"IsHarnessed"} (CR 701.64). There is no IsHarnessed condition and no harnessed field: the predicate said through Matches is the one spelling, and every "as long as [this] is [designated]" line is written that way.
        - A mode count that depends on the game as the spell is cast — "choose one. If you control a commander as you cast this spell, you may choose both instead" — is ONE ChooseMode with "max": 1, "maxIf": a Controls condition over a filter with "isCommander": true, and "maxIfMet": 2 (CR 601.2b, 700.2). The condition is read at announcement, so never an If around the ChooseMode and never two ChooseModes for one list. Write "maxIfMet" only beside "maxIf", and above "max".
        - "For each creature destroyed this way" is Destroy with "rememberAs", and the later step reads that key with Remembered (CR 701.8). Never a CountOf over the filter: that counts the board and would count the creatures the destruction did not destroy.
        - "You gain that much life" on a "whenever you gain life" trigger is TriggeringLifeGained, and "that much" on a life-loss trigger is TriggeringLifeLost (CR 603.10) — read like TriggeringDamage, one for each event. Life lost is not damage, so never TriggeringDamage for either, and never a Stored key: nothing on the card stored it.
        - "Modified" is the predicate {"kind":"IsModified"} in a filter's "also" (CR 700.9): a counter of any kind on it, equipped, or enchanted by an Aura its own controller controls. Never a WithCounter reading, which drops the Equipment and the Aura, and never an AnyOf of three predicates.
        - "Can't attack you unless their controller pays {2} for each creature they control that's attacking you" is ONE CantAttack with "player" and "unlessPays" (CR 508.1h). The scaling lives inside the cost — a PayManaCost whose "generic" is Times(AttackersAgainst(You), 2) — and "unless" is the board exception ("unless it's a Zombie"), so never both on one record.
        - "You may cast green creature spells as though they had flash" is MayCastAsThoughFlash with "who" and "what" (CR 702.8): a timing permission granted to a player. Never GrantKeyword with Flash, which needs objects rather than "spells you cast", and never MayPlayFromZone, which is permission to cast from somewhere else.
        - "Create a token attacking that player or a planeswalker they control" sets CreateToken "attacking": true AND "attackingWhom" to the player (CR 508.4). The planeswalker is that player's side of the board and needs nothing of its own; "attackingWhom" without "attacking" is refused.
        - "If an opponent controls three or more creatures" is the player reference {"kind":"AnOpponent"} inside the condition (CR 102.2) — some opponent, at least one. EachOpponent there reads "every opponent", which is a different card in a multiplayer game and the same one in a duel, so the wrong reading survives a two-player test. AnOpponent is only ever read by a condition: never the "who" of an effect.
        - A card that regenerates prints Regenerate and nothing else, even though Scryfall lists Heal beside it off the reminder text (CR 701.19a): never add a Heal effect to satisfy that keyword list.
        - "At the beginning of your next main phase" is AtStep with "step": "Main" (CR 505.1) — either main phase, whichever begins next. Write "PrecombatMain" or "PostcombatMain" only when the card names one.
        - "As an additional cost to cast this spell, sacrifice a creature" is a SacrificeCost in the FACE's "additionalCosts" (CR 118.8), and "the sacrificed creature's power" reads it back with a Remembered selector naming that cost's "rememberAs". The cost stays on the face — never a Sacrifice step inside the spell's effect, which would happen on resolution rather than as it is cast.
        - "When damage is prevented this way, this creature deals that much damage to any other target" is a DamagePrevented trigger with "thisWay": true (CR 615.13), "that much" is TriggeringPreventedDamage, and "any other target" is AnyTarget with "other": true (CR 115.4). Never TriggeringDamage, which is the damage that was dealt and not the damage that was prevented, and never a filter for "other", which would drop the player, planeswalker and battle halves of "any target".
        - "Combat damage can't be prevented" is DamageCantBePrevented with "combat": true (CR 615.12) — omit "combat" for all damage, and "from" for any source. It is not a PreventDamage of any shape and not DealDamage's own "cantBePrevented" rider, which is one instruction of one card's damage.
        - "Play with the top card of your library revealed" is PlayWithTopCardRevealed (CR 401.5): every player sees it. Never MayLookAtTopCard, which is the private half — one player looking — and never a play permission, which is a second sentence and MayPlayFromZone.
        - A replacement on drawing that spares the draw step's own draw sets WouldDraw "exceptFirstInDrawStep": true (CR 121.1), the same field and the same word as the Draws trigger. It is not "firstEachTurn": false, and the two are never both written.
        - "If no mana was spent to cast it" is a Compare of ManaSpentOnTriggering against 0 (CR 400.7d) — the triggering spell's payment, on an ability that triggers on a spell being cast. ManaSpentOnThis there answers about this card instead, which is a different question.
        - "Any number of target creature cards that each have a different mana value" puts the difference in the FILTER, not in Target's "distinct" (CR 601.2c): "distinct" is only ever about the objects chosen being different objects, and it is true by default — write it only as false, for the printed line that lets one object be chosen twice.
        - "Put the rest on the bottom of your library in a random order" is the single placement "BottomRandomOrder" (CR 401.4) — never "Bottom" beside "RandomOrder", and never one of the two alone.
        - "Move any number of +1/+1 counters" is MoveCounters with "anyNumber": true (CR 122.5). "n" is a printed count and "all" is "all of them"; exactly one of the three, or none for a single counter.
        - "Counter target spell. Its controller puts it on top of their library instead" is ONE Counter with "putInto": "Library" and "where": "Top" (CR 701.6a). Never a Counter followed by a Move: that is a second event, and the spell would reach the graveyard first.
        - "Whenever a player casts their second spell each turn" is SpellCast with "nth": 2 (CR 603.2) — that cast alone, not the ones after it. Never an ungated SpellCast beside a "two or more spells this turn" condition, which fires again on the third.
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
