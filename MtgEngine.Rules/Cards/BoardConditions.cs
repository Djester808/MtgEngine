using System.Globalization;
using System.Text.RegularExpressions;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// Reads a printed condition about the board into a question the engine can ask.
/// </summary>
/// <remarks>
/// A third small grammar beside the target phrase and the trigger clause, and it exists for the
/// same reason: the conditions recur far more than the sentences containing them do. "Unless you
/// control two or more other lands" is one condition across a whole cycle of lands, and the same
/// counting question turns up again in intervening-if clauses and in cost reductions.
/// <para>
/// It is deliberately narrow. A condition read too loosely produces a land that comes in untapped
/// when it should not, which is a strictly better card and one nothing downstream would notice —
/// so anything not understood returns null and leaves the whole line unread.
/// </para>
/// </remarks>
public static partial class BoardConditions
{
    /// <summary>The question a printed condition asks, or null if it is not one we read.</summary>
    /// <remarks>
    /// A clause no single reader recognises is offered to <see cref="Joined"/> before it is given
    /// up on, and the fall-through has to be here rather than at the bottom of the reader chain.
    /// Several readers claim a clause on its opening words and then return null on the part they
    /// cannot read - "you control a Desert or there is a Desert card in your graveyard" is taken
    /// by the controls-a-noun reader, which then cannot name that noun - so a combinator sitting
    /// after them would never be reached by the clauses it exists for.
    /// </remarks>
    public static Func<GameState, IAbilitySource, GameObject, bool>? Parse(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var text = condition.Trim().TrimEnd('.');

        // "Activate only if ~ entered this turn or if you control a basic land" - English repeats
        // the conjunction's "if" and a parser cannot, so the second half arrives here still
        // carrying it and every reader refuses a clause that begins with a word none of them
        // expect. Stripped here rather than in the combinator because this method is what the
        // combinator hands each half back to, so one strip covers a clause of any depth.
        //
        // It is meaning-preserving rather than a guess: the caller has already cut the clause
        // out from behind its own "if", so a second one can only be this repeat.
        if (text.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
            text = text["if ".Length..];

        return Single(text) ?? Joined(text);
    }

    /// <summary>One condition, with no "and" or "or" holding two of them together.</summary>
    private static Func<GameState, IAbilitySource, GameObject, bool>? Single(string condition)
    {
        var text = condition;

        // "You control no Islands" is "you control 0 or fewer Islands" in the words a card
        // actually uses. Rewritten rather than given its own reader, so the noun goes through
        // exactly the same filter vocabulary and the two can never disagree about what an
        // Island is.
        // "There are no creatures on the battlefield" asks about everybody's board at once, which
        // is the same question the controls-reader answers once it is allowed to ignore whose
        // permanent it is. Rewritten rather than given a reader, so the noun keeps going through
        // one filter vocabulary.
        var emptyEverywhere = NoneOnBattlefieldLine().Match(text);
        if (emptyEverywhere.Success)
            text = $"a player controls no {emptyEverywhere.Groups["what"].Value}";

        // "There are five or more Islands on the battlefield", "there is a Mountain on the
        // battlefield" - the counted and singular halves of the emptiness rewrite above, and the
        // two nobody had written down. Rewritten onto the same two readers that answer "you
        // control ..." so the noun keeps going through one filter vocabulary, and onto the
        // subject that names no side, because a question about the battlefield is about
        // everybody's permanents (CR 400.1).
        var anywhere = OnBattlefieldLine().Match(text);
        if (anywhere.Success)
        {
            text = anywhere.Groups["a"].Success
                ? $"a player controls a {anywhere.Groups["what"].Value}"
                : $"a player controls {anywhere.Groups["n"].Value} or more "
                    + anywhere.Groups["what"].Value;
        }

        var emptyBoard = NoneLine().Match(text);
        if (emptyBoard.Success)
            text = $"you control 0 or fewer {emptyBoard.Groups["what"].Value}";

        // "No opponent controls a white or blue creature" is "your opponents control no ..."
        // in the words a card happens to use, and the two are the same question at any number of
        // seats. Rewritten rather than given a reader, so the noun keeps going through one filter
        // vocabulary and the negation keeps one place to live.
        var noneOfTheirs = NoOpponentControlsLine().Match(text);
        if (noneOfTheirs.Success)
            text = "your opponents control no " + noneOfTheirs.Groups["what"].Value;

        // Rewritten for the same reason: one reader for a count, whatever words ask for it.
        var oneInGraveyard = OneInGraveyardLine().Match(text);
        if (oneInGraveyard.Success)
        {
            text = "there are one or more "
                + oneInGraveyard.Groups["what"].Value.Trim()
                + " cards in your graveyard";
        }

        // "Seven or more cards are in your graveyard" is the counting reader's own question with
        // the pile moved to the back of the sentence, and nine lines print it that way -
        // threshold, mostly. Rewritten rather than given a reader, so the count and the card
        // filter have one place to live and cannot disagree about what a Land card is.
        var pileFirst = GraveyardCountReversedLine().Match(text);
        if (pileFirst.Success)
        {
            text = "there are "
                + pileFirst.Groups["n"].Value
                + " or more "
                + pileFirst.Groups["what"].Value
                + "cards in your graveyard";
        }

        // "Creatures you control have total power 8 or greater" - a sum rather than a tally, so
        // it cannot go through the counting reader above: eight 1/1s and one 8/8 both pass, and
        // counting creatures would tell them apart when the card does not.
        var totalPower = TotalPowerLine().Match(text);
        if (totalPower.Success
            && EffectPhrase.Specs.ParseGroup(totalPower.Groups["what"].Value.Trim()) is
            { Kind: Abilities.TargetKind.Permanent } summed)
        {
            var wantedPower = Number(totalPower.Groups["n"].Value);
            var atLeast = !totalPower.Groups["dir"].Value.StartsWith(
                "less", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) =>
            {
                // Power as it is now, not as printed: a lord's creatures have the power the lord
                // gives them, which is the whole reason a card asks this rather than a count.
                var total = state.Battlefield
                    .Select(state.GetObject)
                    .Where(o => summed.ObjectFilter?.Invoke(
                        state, abilities, o, source.ControllerId) != false)
                    .Sum(o => State.Characteristics.Of(state, abilities, o).Power ?? 0);

                return atLeast ? total >= wantedPower : total <= wantedPower;
            };
        }

        // "If you cast it from your hand" - a fact about how the permanent got here, which only
        // the move to the stack knew and which travels with the card from there.
        if (CastFromLine().Match(text) is { Success: true } from)
        {
            var wanted = from.Groups["zone"].Value.ToLowerInvariant() switch
            {
                "hand" => Zone.Hand,
                "graveyard" => Zone.Graveyard,
                "exile" => Zone.Exile,
                _ => (Zone?)null,
            };

            if (wanted is not { } zone)
                return null;

            return (_, _, source) => source.CastFromZone == zone;
        }

        // "You control three or more creatures with different powers" — how many *distinct*
        // powers are on the board, not how many creatures. Three 2/2s are one power and fail it;
        // a 1/1, a 2/2 and a 3/3 pass. Power is computed rather than printed, so a lord's bonus
        // counts and two creatures a lord has pulled level stop being different (CR 613.4).
        if (DistinctPowersLine().Match(text) is { Success: true } spread
            && EffectPhrase.Specs.ParseGroup("each " + spread.Groups["what"].Value.Trim()) is
            { Kind: Abilities.TargetKind.Permanent } varied)
        {
            var leastDistinct = Number(spread.Groups["n"].Value);

            return (state, abilities, source) =>
            {
                var powers = new HashSet<int>();

                foreach (var id in state.Battlefield)
                {
                    var permanent = state.GetObject(id);
                    var now = Characteristics.Of(state, abilities, permanent);

                    if (now.ControllerId != source.ControllerId)
                        continue;

                    if (varied.ObjectFilter?.Invoke(
                            state, abilities, permanent, source.ControllerId) == false)
                    {
                        continue;
                    }

                    powers.Add(now.Power ?? 0);
                }

                return powers.Count >= leastDistinct;
            };
        }

        // "You control three or more lands with the same name", "seven or more lands with
        // different names" - a question about sameness across a group rather than about how many
        // of them there are, and the two halves of it are each other's opposite: three copies of
        // one land satisfy the first and fail the second. It sits beside the distinct-powers
        // reader because it is that same shape over another characteristic, the way the
        // graveyard's mana-value count sits beside its type count.
        if (SameNameLine().Match(text) is { Success: true } alike
            && EffectPhrase.Specs.ParseGroup("each " + alike.Groups["what"].Value.Trim()) is
            { Kind: Abilities.TargetKind.Permanent } sameSpec)
        {
            var least = Number(alike.Groups["n"].Value);
            var distinct = alike.Groups["different"].Success;

            return (state, abilities, source) =>
            {
                var names = new List<string>();

                foreach (var id in state.Battlefield)
                {
                    var permanent = state.GetObject(id);

                    // A face-down permanent has no name at all (CR 707.2), so it is neither the
                    // same as anything nor different from it - it is simply not counted.
                    if (permanent.Permanent?.IsFaceDown == true)
                        continue;

                    if (Characteristics.Of(state, abilities, permanent).ControllerId
                        != source.ControllerId)
                    {
                        continue;
                    }

                    if (sameSpec.ObjectFilter?.Invoke(
                            state, abilities, permanent, source.ControllerId) == false)
                    {
                        continue;
                    }

                    // A name is not a computed characteristic here: nothing in this engine
                    // changes one, so the printed English name is the name (CR 201.2).
                    names.Add(permanent.Card.Name);
                }

                if (distinct)
                    return names.Distinct(StringComparer.Ordinal).Count() >= least;

                // "Three or more lands with the same name" wants three that agree, not three
                // distinct names - so it is the largest group and never the count of groups.
                return names.Count != 0
                    && names.GroupBy(name => name, StringComparer.Ordinal)
                        .Max(group => group.Count()) >= least;
            };
        }

        // "You have no cards in hand", "you have seven or more cards in hand". The hand is a
        // hidden zone, so this counts rather than looks: how many a player holds is public
        // (CR 400.2), which is exactly why a card may ask.
        if (HandCountLine().Match(text) is { Success: true } held)
        {
            // "You have a card in hand" is "one or more" said the short way, and it is the
            // opposite end of the same pattern's "no cards in hand" - one card prints it, and
            // the reader that already answers the empty hand could not answer the full one.
            var wantedHeld = held.Groups["none"].Success ? 0
                : held.Groups["a"].Success ? 1
                : Number(held.Groups["n"].Value);

            // "Fewer than seven" is a strict comparison and "seven or fewer" is not, and the
            // cards that ask are the ones the difference decides: Kozilek draws up to seven,
            // so reading "fewer than seven" as "seven or fewer" draws a card off a full hand.
            var compare = held.Groups["none"].Success ? "exactly"
                : held.Groups["exactly"].Success ? "exactly"
                : held.Groups["a"].Success ? "more"
                : held.Groups["under"].Success
                    ? held.Groups["under"].Value.StartsWith("more", StringComparison.OrdinalIgnoreCase)
                        ? "over"
                        : "under"
                    : held.Groups["dir"].Value.StartsWith("more", StringComparison.OrdinalIgnoreCase)
                        ? "more"
                        : "fewer";

            var mine = !held.Groups["who"].Value
                .StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

            bool Holds(int count) => compare switch
            {
                "exactly" => count == wantedHeld,
                "more" => count >= wantedHeld,
                "over" => count > wantedHeld,
                "under" => count < wantedHeld,
                _ => count <= wantedHeld,
            };

            return (state, _, source) => mine
                ? Holds(state.GetPlayer(source.ControllerId).Hand.Count)
                : state.TurnOrder
                    .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                    .Any(id => Holds(state.GetPlayer(id).Hand.Count));
        }

        // "If you have more cards in hand than each opponent" - two counts compared rather than
        // one count against a number, so it cannot go through the reader above however the
        // words look. "Each" is the whole of it: at more than two seats this is true only when
        // you are ahead of every one of them, and reading it as "any" would fire on the table's
        // second-largest hand.
        if (LargestHandLine().Match(text) is { Success: true } compared)
        {
            // "An opponent has more cards in hand than you" is the same two counts compared the
            // other way round, and it is *not* the negation of the clause above: an opponent
            // holding exactly as many cards as you satisfies neither. So the two quantifiers are
            // written out rather than one being derived from the other - "each opponent" is all
            // of them and "an opponent" is any one of them (CR 102.1), which only a table of
            // three can tell apart.
            var theirs = compared.Groups["theirs"].Success;

            return (state, _, source) =>
            {
                var mine = state.GetPlayer(source.ControllerId).Hand.Count;

                var others = state.TurnOrder
                    .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                    .Select(id => state.GetPlayer(id).Hand.Count);

                return theirs ? others.Any(held => held > mine) : others.All(held => mine > held);
            };
        }

        var counted = CountLine().Match(text);
        if (counted.Success)
            return Counting(counted);

        var graveyard = GraveyardCountLine().Match(text);
        if (graveyard.Success)
        {
            // "An opponent has eight or more cards in their graveyard" is the same count asked
            // about somebody else, and at more than two players "an opponent" means *any* of
            // them - so it is a search rather than a lookup, which is the only part that differs.
            var theirs = graveyard.Groups["who"].Success;

            // "No cards" is the numbered comparison with both halves at their limits: nothing
            // wanted, and at most that. Written as the two values rather than as a third branch,
            // so the counting below stays one expression - but written out here rather than left
            // to the number reader, whose default for a word it does not recognise is one.
            var emptyPile = graveyard.Groups["none"].Success;
            var wanted = emptyPile
                ? 0
                : Number(theirs ? graveyard.Groups["n2"].Value : graveyard.Groups["n"].Value);
            var orMore = !emptyPile
                && (theirs ? graveyard.Groups["dir2"].Value : graveyard.Groups["dir"].Value)
                    .StartsWith("more", StringComparison.OrdinalIgnoreCase);
            var anyOpponent = theirs
                && graveyard.Groups["who"].Value.StartsWith("an", StringComparison.OrdinalIgnoreCase);

            // "Four or more creature cards in your graveyard" - the same count over a filtered
            // pile. The noun goes through the shared card-filter vocabulary, so a word that
            // vocabulary cannot name leaves the condition unread rather than counting the whole
            // graveyard and answering a question the card did not ask.
            //
            // "And/or" is the cards' own shorthand for a list a card answers any of, and the
            // filter vocabulary spells that "or" (CR 109.4). Normalised here rather than taught
            // to that vocabulary, because the slash is punctuation this sentence uses and not a
            // word the filter grammar has any other use for.
            var noun = Either().Replace(
                (theirs ? graveyard.Groups["what2"].Value : graveyard.Groups["what"].Value).Trim(),
                " or ");

            string? filter = null;

            if (noun.Length > 0)
            {
                filter = EffectPhrase.CardFilterNamed(noun);
                if (filter is null)
                    return null;
            }

            bool Counts(GameState state, ObjectId id) =>
                filter is null
                || (state.TryGetObject(id, out var card)
                    && Abilities.SearchFilters.Matches(filter, card.Card));

            return (state, abilities, source) =>
            {
                if (!anyOpponent)
                {
                    var mine = state.GetPlayer(source.ControllerId).Graveyard
                        .Count(id => Counts(state, id));

                    return orMore ? mine >= wanted : mine <= wanted;
                }

                return state.TurnOrder
                    .Where(id => id != source.ControllerId)
                    .Select(id => state.GetPlayer(id).Graveyard.Count(card => Counts(state, card)))
                    .Any(count => orMore ? count >= wanted : count <= wanted);
            };
        }

        // "Four or more card types among cards in your graveyard" counts distinct *types*, not
        // cards, which is a different question from the count above and the reason delirium is
        // hard to reach with two cards and easy with four. Token and Other are not card types a
        // card can be printed with (CR 205.2a), so they are not counted even if one appears.
        var kinds = GraveyardTypeCountLine().Match(text);
        if (kinds.Success)
        {
            var wantedKinds = Number(kinds.Groups["n"].Value);
            var orMoreKinds = kinds.Groups["dir"].Value
                .StartsWith("more", StringComparison.OrdinalIgnoreCase);

            // "Four or more permanent types" is a narrower list than delirium's, not a synonym
            // for it (CR 110.4 against CR 205.2a): an instant and a sorcery are two card types
            // and no permanent types at all, so counting them here would open the door on a
            // graveyard the printed card leaves it shut on.
            var tallied = kinds.Groups["kind"].Value
                .StartsWith("permanent", StringComparison.OrdinalIgnoreCase)
                ? PermanentCardTypes
                : CardTypesForDelirium;

            return (state, _, source) =>
            {
                var seen = tallied.Count(
                    type => state.GetPlayer(source.ControllerId).Graveyard.Any(
                        id => state.GetObject(id).Card.CardTypes.HasFlag(type)));

                return orMoreKinds ? seen >= wantedKinds : seen <= wantedKinds;
            };
        }

        // "There are five or more mana values among cards in your graveyard" - delirium's shape
        // over a different characteristic, and the same distinction from a count: five cards can
        // be one mana value and two cards can be two. Sits beside the type count rather than
        // beside the card count for exactly that reason.
        var values = GraveyardManaValueCountLine().Match(text);
        if (values.Success)
        {
            var wantedValues = Number(values.Groups["n"].Value);
            var orMoreValues = values.Groups["dir"].Value
                .StartsWith("more", StringComparison.OrdinalIgnoreCase);

            return (state, _, source) =>
            {
                // The mana value of a card in a graveyard is the one its cost gives it
                // (CR 202.3); nothing on this side of the stack can be holding an X.
                var distinct = state.GetPlayer(source.ControllerId).Graveyard
                    .Select(id => state.GetObject(id).Card.Cmc)
                    .Distinct()
                    .Count();

                return orMoreValues ? distinct >= wantedValues : distinct <= wantedValues;
            };
        }

        var turn = YourTurnLine().Match(text);
        if (turn.Success)
        {
            var negated = turn.Groups["not"].Success;
            return (state, abilities, source) =>
                (state.ActivePlayerId == source.ControllerId) != negated;
        }

        var life = LifeLine().Match(text);
        if (life.Success)
        {
            var wanted = Number(life.Groups["n"].Value);
            var orMore = life.Groups["dir"].Value.StartsWith("more", StringComparison.OrdinalIgnoreCase);

            // "Exactly 1 life" is a window, the way the hand count already reads one, and both
            // thresholds are wrong for it in a way that plays: at or above leaves the reward on
            // for a healthy player, at or below leaves it on for a dying one.
            var exactly = life.Groups["exactly"].Success;
            var who = life.Groups["who"].Value.ToLowerInvariant();

            return (state, abilities, source) =>
            {
                // "An opponent has ..." is true if any one of them does, and "a player has ..."
                // is true if anyone at all does — including the controller, which is the whole
                // difference between the two and the reason they cannot share a branch (CR 109.5).
                var totals = who switch
                {
                    "you" => [state.GetPlayer(source.ControllerId).Life],
                    "a player" => state.TurnOrder
                        .Where(id => !state.GetPlayer(id).HasLost)
                        .Select(id => state.GetPlayer(id).Life)
                        .ToList(),
                    _ => state.TurnOrder
                        .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                        .Select(id => state.GetPlayer(id).Life)
                        .ToList(),
                };

                return totals.Any(total => exactly
                    ? total == wanted
                    : orMore ? total >= wanted : total <= wanted);
            };
        }

        // "As long as your devotion to black is less than five, ~ isn't a creature" - CR 700.5,
        // and the whole God cycle turns on it. A count of *symbols* rather than of permanents,
        // which is why it cannot go through the counting reader: one permanent costing {B}{B}{B}
        // is three devotion and three permanents costing {1} are none.
        var devoted = DevotionLine().Match(text);
        if (devoted.Success)
        {
            var wantedColours = new List<Domain.Enums.ManaColor>();

            foreach (var word in new[] { devoted.Groups["c1"].Value, devoted.Groups["c2"].Value })
            {
                if (word.Length == 0)
                    continue;

                if (ColourNamed(word) is not { } named)
                    return null;

                wantedColours.Add(named);
            }

            var threshold = Number(devoted.Groups["n"].Value);

            // "Less than five" is strict and "five or greater" is not. Both wordings are in the
            // corpus and they are each other's complement, so reading one as the other turns
            // every God in the cycle on and off exactly one permanent early.
            var below = devoted.Groups["less"].Success;

            return (state, abilities, source) =>
            {
                var symbols = 0;

                foreach (var id in state.Battlefield)
                {
                    var obj = state.GetObject(id);

                    // Control is computed (CR 613.1b); a permanent an opponent has taken stops
                    // counting towards your devotion the moment they take it.
                    if (Characteristics.Of(state, abilities, obj).ControllerId != source.ControllerId)
                        continue;

                    foreach (var symbol in Mana.ManaCostSpec.Parse(obj.Card.ManaCostRaw).Symbols)
                    {
                        // A hybrid symbol is each of its colours (CR 202.2b), so {W/U} counts
                        // towards white, towards blue, and once towards white-and-black's
                        // sibling - never twice, which is why this counts symbols and not
                        // colours. CR 700.5 asks for symbols that *are* one of the named
                        // colours, so a two-colour devotion is a union rather than a sum.
                        if (wantedColours.Any(symbol.Colors.Contains))
                            symbols++;
                    }
                }

                return below ? symbols < threshold : symbols >= threshold;
            };
        }

        // "As long as red is the most common color among all permanents or is tied for most
        // common" - the five Djinns, each naming its own colour - and "unless it shares a color
        // with the most common color", which asks the same census about the creature an Aura is
        // on. A count of *permanents* per colour, which is what separates it from devotion
        // above: a permanent costing {R}{R}{R} is three devotion and one red permanent.
        var census = MostCommonColourLine().Match(text);
        if (census.Success)
        {
            // Empty for the "shares a color" arm, which names no colour and asks about the
            // subject's own. The alternation admits only the five words, so a named arm always
            // has an answer here and nothing claims a clause it then cannot read.
            var hue = ColourNamed(census.Groups["colour"].Value);

            return (state, abilities, source) =>
            {
                var commonest = MostCommonColours(state, abilities);

                if (hue is { } wanted)
                    return commonest.Contains(wanted);

                // "It" on an Aura is the permanent it is attached to and never the Aura, which
                // has a colour of its own and is not what the sentence is about.
                return Subject(state, source, pronoun: true) is { } about
                    && Characteristics.Of(state, abilities, about).Colors.Any(commonest.Contains);
            };
        }

        // "You have no cards in hand", "an opponent has no cards in hand". Whose hand it is was
        // the only thing missing, and it is the same question either way - "an opponent" means
        // any one of them (CR 102.1), which in a two-player game is the other player and in a
        // larger one is a real disjunction rather than a synonym for "the opponent".
        var emptyHand = EmptyHandLine().Match(text);
        if (emptyHand.Success)
        {
            var mine = emptyHand.Groups["who"].Value.StartsWith(
                "you", StringComparison.OrdinalIgnoreCase);

            // "A player has no cards in hand" is anybody at all, the asker included, which is a
            // third answer rather than a synonym for either of the two above (CR 109.5) - and it
            // differs from them at the only board that matters to the cards printing it, the one
            // where the empty hand is your own.
            var anyone = emptyHand.Groups["anyone"].Success;

            return (state, abilities, source) => anyone
                ? state.TurnOrder.Any(other => state.GetPlayer(other).Hand.IsEmpty)
                : mine
                    ? state.GetPlayer(source.ControllerId).Hand.IsEmpty
                    : state.TurnOrder.Any(
                        other => other != source.ControllerId
                            && state.GetPlayer(other).Hand.IsEmpty);
        }

        // "An opponent has more life than you", "you have more life than each opponent" - two
        // life totals compared rather than one against a number, which is why it cannot go
        // through the threshold reader above however alike the words look. "Each" is what the
        // extra seats decide: it is true only when you are ahead of every one of them, and
        // reading it as "any" would fire on the table's second-largest total.
        var richer = LifeComparisonLine().Match(text);
        if (richer.Success)
        {
            var theirs = richer.Groups["theirs"].Success;
            var more = richer.Groups["dir"].Value
                .StartsWith("more", StringComparison.OrdinalIgnoreCase);
            var every = richer.Groups["each"].Value
                .StartsWith("each", StringComparison.OrdinalIgnoreCase);

            return (state, _, source) =>
            {
                var mine = state.GetPlayer(source.ControllerId).Life;

                var others = state.TurnOrder
                    .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                    .Select(id => state.GetPlayer(id).Life)
                    .ToList();

                if (theirs)
                    return others.Any(life => more ? life > mine : life < mine);

                bool Beats(int life) => more ? mine > life : mine < life;

                return every ? others.Count > 0 && others.TrueForAll(Beats) : others.Exists(Beats);
            };
        }

        // "If this permanent is an enchantment" — a question about the source's own computed
        // types, which is why it is not simply read off the card: something may have made it one.
        var isType = SourceIsTypeLine().Match(text);
        if (isType.Success)
        {
            if (EffectPhrase.Specs.Parse("target " + isType.Groups["what"].Value.Trim()) is not
                { Kind: Abilities.TargetKind.Permanent } wanted)
            {
                return null;
            }

            return (state, abilities, source) =>
                wanted.ObjectFilter?.Invoke(
                    state, abilities, source, source.ControllerId) != false;
        }

        var strong = ControlsWithPowerLine().Match(text);
        if (strong.Success)
        {
            var threshold = Number(strong.Groups["n"].Value);
            var direction = strong.Groups["dir"].Value;
            var orMore = direction.Equals("greater", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("more", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => state.Battlefield.Any(id =>
            {
                var computed = Characteristics.Of(state, abilities, state.GetObject(id));
                if (!computed.IsCreature || computed.ControllerId != source.ControllerId)
                    return false;

                var power = computed.Power ?? 0;
                return orMore ? power >= threshold : power <= threshold;
            });
        }

        // "If an opponent was dealt damage this turn" — bloodthirst's condition, and a dozen
        // others printed the long way round.
        var bloodied = DamagedThisTurnLine().Match(text);
        if (bloodied.Success)
        {
            var theirs = bloodied.Groups["who"].Value.Contains(
                "opponent", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => state.TurnOrder.Any(id =>
                (theirs ? id != source.ControllerId : id == source.ControllerId)
                && state.GetPlayer(id).WasDealtDamageThisTurn);
        }

        if (DiedThisTurnLine().Match(text) is { Success: true } deaths)
        {
            var nobody = deaths.Groups["none"].Success;
            return (state, _, _) => state.CreatureDiedThisTurn != nobody;
        }

        // "Three or more creatures died this turn" - the same fact asked as a number. Read before
        // nothing else claims it, and answered from the count rather than the flag: a bool says
        // whether any died and these ask how many.
        if (CreaturesDiedCountLine().Match(text) is { Success: true } toll)
        {
            var least = Number(toll.Groups["n"].Value);
            return (state, _, _) => state.CreaturesDiedThisTurn() >= least;
        }

        // "~ entered this turn", and the pronoun that means the same permanent. Not summoning
        // sickness, which is the tempting substitute and a different question: sickness runs until
        // its controller's *next* untap step (CR 302.6), so a creature that arrived on your turn
        // still has it all through the opponent's turn while this is already false.
        //
        // "That creature entered this turn" is deliberately not read. It names whatever the
        // trigger was about, and a board condition is not given the trigger's subject - it would
        // answer about the permanent asking instead, which is a different card.
        if (SelfEnteredThisTurnLine().IsMatch(text))
            return (state, _, source) => state.EnteredThisTurn(source.Id);

        // "A permanent left the battlefield under your control this turn", and the same sentence
        // with the possessive moved. Scoped to the asker's own permanents in both spellings,
        // because that is what every printed one says - a game-wide reading would answer yes when
        // an opponent's permanent died, which is a strictly easier card than the one printed.
        if (LeftBattlefieldThisTurnLine().IsMatch(text))
        {
            return (state, _, source) =>
                state.PermanentsLeftBattlefieldThisTurn(source.ControllerId) >= 1;
        }

        // "Two or more nonland permanents entered the battlefield under your control this turn",
        // "you had another creature enter the battlefield under your control this turn", "an
        // opponent had an artifact enter the battlefield under their control this turn". One
        // reader for the whole family: the count, the noun, the word "another" and whose board it
        // is are the only things that vary across it, and the two word orders say the same thing.
        if (EnteredUnderYourControlLine().Match(text) is { Success: true } arrived)
        {
            if (Crossings(arrived) is not { } arrival)
                return null;

            return (state, _, source) => arrival.Happened(state.ArrivalsThisTurn, state, source);
        }

        // "If a creature died under your control this turn", "if another Human died under your
        // control this turn". Not the game-wide death count above: these name a side, and
        // answering them game-wide turns the card on when an opponent's creature dies - strictly
        // better than printed, and it would still play (CR 700.4).
        if (DiedUnderControlLine().Match(text) is { Success: true } lost)
        {
            if (Crossings(lost) is not { } death)
                return null;

            return (state, _, source) => death.Happened(
                state.DeparturesThisTurn.Where(gone => gone.To == Zone.Graveyard),
                state,
                source);
        }

        // "You descended this turn" (CR 700.11): a permanent card was put into your graveyard from
        // anywhere. Not the same question as descend 4, which reads the graveyard's contents now
        // and needs no record of the turn at all.
        if (DescendedThisTurnLine().IsMatch(text))
            return (state, _, source) => state.GetPlayer(source.ControllerId).TimesDescendedThisTurn >= 1;

        // "If {U} was spent to cast this spell", "if {R}{R} was spent to cast it", "if at least
        // four mana was spent", "if no mana was spent". Four questions about one record - the
        // mana that actually paid, kept on the object since it was cast (CR 202.2) - so they are
        // read as one shape with the symbols counted rather than as four readers.
        var spending = ManaSpentLine().Match(text);
        if (spending.Success)
        {
            var wanted = new Dictionary<Domain.Enums.ManaColor, int>();

            foreach (Match symbol in ManaSymbolsIn().Matches(spending.Groups["symbols"].Value))
            {
                // One letter, one colour (CR 105.1). Written out rather than shared with the
                // filter vocabulary's table, which reads words: this reads the symbols a cost is
                // printed in, and the two are different alphabets for the same five things.
                var colour = symbol.Groups["c"].Value.ToUpperInvariant() switch
                {
                    "W" => Domain.Enums.ManaColor.White,
                    "U" => Domain.Enums.ManaColor.Blue,
                    "B" => Domain.Enums.ManaColor.Black,
                    "R" => Domain.Enums.ManaColor.Red,
                    "G" => Domain.Enums.ManaColor.Green,
                    _ => (Domain.Enums.ManaColor?)null,
                };

                if (colour is not { } named)
                    return null;

                wanted[named] = wanted.GetValueOrDefault(named) + 1;
            }

            var least = spending.Groups["n"].Success ? Number(spending.Groups["n"].Value) : 0;
            var none = spending.Groups["none"].Success;
            var noColour = spending.Groups["nocolour"].Success;

            // "If at least three white mana was spent to cast ~" - the same record asked for one
            // colour rather than for the total, and the symbol form above cannot express it: a
            // number in words has no {W} to repeat, and spelling it out as three symbols would
            // make "at least three" into "exactly these three".
            var onlyColour = spending.Groups["colour"].Success
                ? ColourNamed(spending.Groups["colour"].Value)
                : null;

            if (spending.Groups["colour"].Success && onlyColour is null)
                return null;

            // "If at least three mana of the same color was spent to cast it" - which colour is
            // not named, so it is the largest single colour rather than the coloured total: three
            // mana of three different colours is not three of the same one.
            var sameColour = spending.Groups["same"].Success
                ? Number(spending.Groups["same"].Value)
                : 0;

            return (state, abilities, source) =>
            {
                if (!state.TryGetObject(source.Id, out var self))
                    return false;

                var spent = self.ManaSpent;
                var total = spent.Colorless + spent.Colored.Sum(each => each.Value);
                var coloured = spent.Colored.Sum(each => each.Value);

                if (none)
                    return total == 0;

                if (noColour)
                    return coloured == 0;

                if (onlyColour is { } single)
                    return spent.Colored.GetValueOrDefault(single) >= least;

                if (sameColour > 0)
                {
                    return spent.Colored.Count > 0
                        && spent.Colored.Values.Max() >= sameColour;
                }

                if (least > 0)
                    return total >= least;

                return wanted.All(each => spent.Colored.GetValueOrDefault(each.Key) >= each.Value);
            };
        }

        // "It has a depletion counter on it" - a question about this permanent's own counters,
        // which is not the same as a count of anything on the board and so has nowhere else to
        // go. Any kind, because the kinds are open-ended: a card names whichever it puts on.
        var bearing = HasCounterLine().Match(text);
        if (bearing.Success)
        {
            var kind = bearing.Groups["kind"].Value.Trim().ToLowerInvariant();
            var least = bearing.Groups["n"].Success ? Number(bearing.Groups["n"].Value) : 1;

            return (_, _, source) =>
                source.Permanent?.Counters.GetValueOrDefault(kind) >= least;
        }

        // "If ~ has counters on it" - any counter at all, of any kind, which the readers on
        // either side of it cannot ask: both are given a name to look for, and a card that moves
        // "all counters from ~" does not care which kinds they are (CR 122.1).
        var bearingAny = AnyCounterLine().Match(text);
        if (bearingAny.Success)
        {
            var pronoun = bearingAny.Groups["it"].Success;

            // "As long as ~ has four or more counters on it" - the same nameless question with a
            // threshold, and the sum across every kind is what it asks: two +1/+1 counters and
            // two quest counters are four counters on the permanent (CR 122.1a). The named
            // readers cannot answer it, because a name is exactly what this clause does not give.
            var least = bearingAny.Groups["n"].Success
                ? Number(bearingAny.Groups["n"].Value)
                : 1;

            return (state, _, source) =>
                Subject(state, source, pronoun) is { } self
                && self.Permanent is { } carried
                && carried.Counters.Values.Sum() >= least;
        }

        // "There are three or more brick counters on ~" - the same question as the line above
        // with the subject moved to the back, and thirty-nine lines print it that way: every
        // permanent that accumulates counters and then does something at a threshold. Nothing
        // in front of this claims a clause beginning "there are ... counters", so it is safe
        // where it sits; the graveyard readers above are anchored on "cards in your graveyard".
        var accumulated = ThereAreCountersLine().Match(text);
        if (accumulated.Success)
            return CountersOn(accumulated);

        // "As long as it's attacking alone" - a question about the declaration this permanent is
        // part of. The engine reads "~ attacks alone" as a trigger already; this is the same fact
        // asked continuously rather than at the moment of declaring, so it cannot reuse that.
        if (AttackingAloneLine().IsMatch(text))
        {
            return (state, _, source) =>
                state.Combat.Attackers.Count == 1
                && state.Combat.Attackers.ContainsKey(source.Id);
        }

        // "If a white creature is attacking", "if three or more creatures are attacking" - the
        // Trap cycle's alternative cost, and a question about the declaration rather than about
        // the board. Nobody's side is named and none of these cards names one: the trap is cast
        // by whoever is being attacked, so a reading scoped to the asker would be true exactly
        // when the printed card is false.
        var assault = AttackingNounLine().Match(text);
        if (assault.Success)
        {
            var many = assault.Groups["n"].Success ? Number(assault.Groups["n"].Value) : 1;

            // "Exactly one creature is attacking" is a window rather than a threshold, and the
            // card asking it - a trap that punishes a lone attacker - is turned off by a second
            // one arriving. "Or more" would leave it on for a whole team.
            var exactly = assault.Groups["exactly"].Success;

            // The noun goes through the shared target grammar, so every filter it knows arrives
            // here already working and a word it cannot name leaves the clause unread - and it
            // is folded by that grammar's own plural, which knows a tribe and a land type as
            // well as the six card types.
            //
            // This arm used to fold with the six-word fork this file kept, and alone among the
            // three readers that used it never gained the SingularWord fallback the other two
            // did. So "if three or more Goblins are attacking" reached the target grammar still
            // plural, was read as the creature type "Goblins", which no card has, and compiled
            // into a condition that counts zero for ever. Not an unread line: a card that plays
            // wrong in silence.
            var noun = EffectPhrase.Specs.FoldPlural(assault.Groups["what"].Value.Trim());

            if (EffectPhrase.Specs.Parse("target " + noun) is not
                { Kind: Abilities.TargetKind.Permanent } attacking)
            {
                return null;
            }

            return (state, abilities, source) =>
            {
                var count = state.Combat.Attackers.Keys.Count(id =>
                    state.TryGetObject(id, out var attacker)
                    && attacker.Zone == Zone.Battlefield
                    && attacking.ObjectFilter?.Invoke(
                        state, abilities, attacker, source.ControllerId) != false);

                return exactly ? count == many : count >= many;
            };
        }

        // "If ~ is blocked" (CR 509.1h): an attacking creature with one or more blockers declared
        // for it. Not the negation of "~ is blocking" beside it - that is the other side of the
        // same combat, and a creature can be neither.
        if (SelfBlockedLine().IsMatch(text))
            return (state, _, source) => !state.Combat.BlockersOf(source.Id).IsEmpty;

        // "An opponent has three or more poison counters" - the tally is in the state for the
        // rule that ends the game at ten (CR 704.5c); nothing could ask it short of that.
        var poisoned = PoisonCountLine().Match(text);
        if (poisoned.Success)
        {
            var wantedPoison = Number(poisoned.Groups["n"].Value);
            var theirs = poisoned.Groups["who"].Value
                .StartsWith("an", StringComparison.OrdinalIgnoreCase);

            return (state, _, source) => theirs
                ? state.TurnOrder.Any(id => id != source.ControllerId
                    && state.GetPlayer(id).PoisonCounters >= wantedPoison)
                : state.GetPlayer(source.ControllerId).PoisonCounters >= wantedPoison;
        }

        // "If defending player is poisoned" - CR 122.1f defines the word as one or more poison
        // counters, so this is the tally above at a threshold of one rather than a second record.
        // It is a reader of its own because of the subject: these are the only cards that ask a
        // poison question of the player this permanent is attacking, and that player is knowable
        // only from the combat state.
        var envenomed = PoisonedLine().Match(text);
        if (envenomed.Success)
        {
            var defending = envenomed.Groups["who"].Value
                .StartsWith("defending", StringComparison.OrdinalIgnoreCase);

            return (state, _, source) =>
            {
                if (defending)
                {
                    // Outside combat "defending player" names nobody, so the condition is simply
                    // false - which is the right answer for a bonus that only applies while this
                    // creature is attacking somebody.
                    return state.Combat.Attackers.TryGetValue(source.Id, out var attacking)
                        && state.GetPlayer(attacking.DefendingPlayer).PoisonCounters > 0;
                }

                return state.TurnOrder.Any(
                    id => id != source.ControllerId
                        && state.GetPlayer(id).PoisonCounters > 0);
            };
        }

        // "You've drawn your second card this turn" as the cards usually spell it: a count of
        // draws, which the state has kept all along for the cards that care. Sits beside the
        // spell count because it is the same question about a different tally.
        var drawnThisTurn = CardsDrawnThisTurnLine().Match(text);
        if (drawnThisTurn.Success)
        {
            var least = drawnThisTurn.Groups["n"].Success
                ? Number(drawnThisTurn.Groups["n"].Value)
                : 1;

            return (state, _, source) =>
                state.GetPlayer(source.ControllerId).CardsDrawnThisTurn >= least;
        }

        // "You've cast a noncreature spell this turn", "you've cast two or more spells this
        // turn" - how many and of what kind are the two things these vary by, so they are read
        // as two groups rather than as a reader each.
        var castThisTurn = SpellsCastThisTurnLine().Match(text);
        if (castThisTurn.Success)
        {
            var least = castThisTurn.Groups["n"].Success ? Number(castThisTurn.Groups["n"].Value) : 1;
            var kind = castThisTurn.Groups["kind"].Value.Trim();

            // "You've cast an instant or sorcery spell this turn" - a kind the two tallies below
            // cannot express, answered from the cards themselves. The state keeps them for
            // exactly this family (CR 601.2i), and the noun goes through the shared card-filter
            // vocabulary, so a word that vocabulary cannot name leaves the condition unread
            // rather than counting every spell and answering a wider question than was asked.
            var named = kind.Length > 0
                && !kind.Equals("noncreature", StringComparison.OrdinalIgnoreCase)
                && !kind.Equals("creature", StringComparison.OrdinalIgnoreCase);

            string? filter = null;

            if (named)
            {
                filter = EffectPhrase.CardFilterNamed(Either().Replace(kind, " or "));
                if (filter is null)
                    return null;
            }

            var lowered = kind.ToLowerInvariant();

            // "You haven't cast a spell this turn" is the same tally read the other way round,
            // and two cards print it. Negating the answer rather than the count is the whole of
            // it: "not two or more" is "fewer than two", which is what inverting the comparison
            // would have had to say and what a second threshold would have got wrong.
            var idle = castThisTurn.Groups["not"].Success;

            return (state, abilities, source) =>
            {
                var player = state.GetPlayer(source.ControllerId);

                if (filter is { } wanted)
                {
                    return (player.SpellCardsCastThisTurn
                        .Count(card => Abilities.SearchFilters.Matches(wanted, card)) >= least)
                        != idle;
                }

                // Creature spells are the difference between the two counts rather than a third
                // one. Naming the kind and then counting every spell would answer a narrower
                // question with a wider number, which is worse than leaving the line unread.
                var count = lowered switch
                {
                    "noncreature" => player.NoncreatureSpellsCastThisTurn,
                    "creature" => player.SpellsCastThisTurn - player.NoncreatureSpellsCastThisTurn,
                    _ => player.SpellsCastThisTurn,
                };

                return (count >= least) != idle;
            };
        }

        // "If this card is in your graveyard", "as long as ~ is on the battlefield", "if ~ is
        // exiled", "only if ~ is on the stack" - a card asking where it is. The answer is where
        // the source is now rather than anything the event said, and it is one reader for all
        // seven zones (CR 400.1) because the preposition is the only thing that varies between
        // them. A reader per preposition would be a second place for that answer to drift.
        var where = InZoneLine().Match(text);
        if (where.Success)
        {
            if (ZoneNamed(where.Groups["z1"].Value) is not { } one)
                return null;

            // "~ is in the command zone or on the battlefield" is one question about one object,
            // so the "or" is read here rather than left to the disjunction combinator: that one
            // needs both halves to stand alone, and "on the battlefield" is not a clause.
            Zone? second = null;

            if (where.Groups["z2"].Success)
            {
                second = ZoneNamed(where.Groups["z2"].Value);
                if (second is null)
                    return null;
            }

            // "Your graveyard" is a possessive and "exile" is not: a library, a hand and a
            // graveyard belong to a player while the battlefield, the stack, exile and the
            // command zone are shared (CR 400.1). So whose pile it is, is asked only where the
            // card says whose - asking it of a shared zone would answer a question nobody put.
            var mine = where.Groups["z1"].Value.StartsWith(
                "in your", StringComparison.OrdinalIgnoreCase);

            var negated = where.Groups["not"].Success;

            return (state, abilities, source) =>
            {
                // No object is no answer, in both directions: reading a card that has ceased to
                // exist as one that "isn't on the battlefield" is the fail-open half of this
                // pair, and it is the half that turns a static on for a permanent that is gone.
                if (!state.TryGetObject(source.Id, out var self))
                    return false;

                var here = (self.Zone == one || (second is { } other && self.Zone == other))
                    && (!mine || self.OwnerId == source.ControllerId);

                return here != negated;
            };
        }

        // "If an opponent controls more lands than you" - two counts compared rather than one
        // count against a number, which is why it cannot go through the counting reader. The
        // noun is read by the same grammar either way.
        var contest = MoreThanLine().Match(text);
        if (contest.Success)
        {
            var singular = EffectPhrase.SingularWord(contest.Groups["what"].Value.Trim());
            if (EffectPhrase.Specs.Parse("target " + singular) is not
                { Kind: Abilities.TargetKind.Permanent } spec)
            {
                return null;
            }

            var challenger = contest.Groups["who"].Value
                .StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

            var strictly = !contest.Groups["dir"].Value
                .StartsWith("fewer", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) =>
            {
                int CountFor(Guid id) => state.Battlefield.Count(objectId =>
                {
                    var obj = state.GetObject(objectId);

                    return Characteristics.Of(state, abilities, obj).ControllerId == id
                        && spec.ObjectFilter?.Invoke(
                            state, abilities, obj, id) != false;
                });

                var mine = CountFor(source.ControllerId);

                // "An opponent" is any one of them, so the comparison is asked of each and
                // answered by the first that beats it (CR 102.1).
                var others = state.TurnOrder
                    .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                    .Select(CountFor);

                if (!challenger)
                    return others.All(theirs => strictly ? mine > theirs : mine < theirs);

                return others.Any(theirs => strictly ? theirs > mine : theirs < mine);
            };
        }

        // "If it was kicked" - CR 702.33d, and the flag survives resolution for exactly this
        // reason. Read from the source rather than from the state, because the question is
        // about this permanent and no other, however many copies of it are in play.
        //
        // "If it wasn't kicked" is three printed cards and the same flag read the other way
        // round. The object still has to be found for either answer: a permanent that has gone
        // must not satisfy a clause about what it was not, any more than one about what it was.
        if (WasKickedLine().Match(text) is { Success: true } kicked)
        {
            var unkicked = kicked.Groups["not"].Success;

            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self) && self.WasKicked != unkicked;
        }

        // "If it was kicked twice" - CR 702.33d: a spell with two kicker costs, or multikicker,
        // may be kicked multiple times, and the count survives resolution exactly as the flag
        // does (CR 607.2). Read as at-least rather than exactly: a triple-kicked spell has been
        // kicked twice.
        if (KickedTwiceLine().IsMatch(text))
        {
            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self) && self.TimesKicked >= 2;
        }

        // "If it was kicked with its {W} kicker" - CR 702.33f: the clause is linked to one of
        // the two kicker abilities an "and/or" card prints (CR 607.2), and it names the cost in
        // the card's own spelling, which is the identity the payment was recorded under.
        if (KickedWithLine().Match(text) is { Success: true } which)
        {
            var cost = which.Groups["cost"].Value.ToUpperInvariant();

            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self)
                && self.KickedWith.Contains(cost, StringComparer.OrdinalIgnoreCase);
        }

        // "If it was cast using teamwork" - CR 702.194b: whether the caster declared the
        // intention to pay the teamwork cost, recorded on the spell and carried across the one
        // move that turns it into a permanent (CR 607.2), exactly as kicker's flag is.
        if (TeamworkCastLine().IsMatch(text))
        {
            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self) && self.WasTeamwork;
        }

        // "If the gift was promised" - CR 702.174k, and the chosen opponent rides the
        // resolution move for exactly this reason: a permanent's gift trigger asks about the
        // spell that became it (CR 607.2). Read from the source the way kicker's flag is, and
        // fail-closed the same way too - an object that has gone answers neither the promise
        // nor its absence.
        if (GiftPromisedLine().Match(text) is { Success: true } gifted)
        {
            var unpromised = gifted.Groups["not"].Success;

            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self)
                && (self.GiftedTo is not null) != unpromised;
        }

        // "When ~ enters, if it was bargained, ..." - CR 702.166b: a spell has been bargained
        // once its controller declares the intention to pay that cost. The sentence form of this
        // ("If this spell was bargained, destroy that creature instead") was already read, and
        // seven cards ask it as an intervening-if on the permanent that arrives instead - which
        // is a different object from the spell (CR 400.7), so the flag has to have ridden the
        // zone change, exactly as kicker's does.
        if (WasBargainedLine().IsMatch(text))
        {
            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self) && self.WasBargained;
        }

        // "If you cast it" - not every permanent was cast, and the ones that were reached the
        // battlefield the long way (CR 601). Who cast it is compared against whoever controls
        // the ability asking, so a permanent taken from the player who cast it stops paying.
        if (WasCastLine().IsMatch(text))
        {
            return (state, abilities, source) =>
                state.TryGetObject(source.Id, out var self)
                && self.CastBy == source.ControllerId;
        }

        // "Unless you have max speed" (CR 702.179e): a player has max speed when their speed is
        // exactly the ceiling, which is 4. Read as the comparison rather than as a flag, because
        // speed is a number the game already keeps and a second field for the top of its range
        // would be a place for the two to disagree.
        if (MaxSpeedLine().IsMatch(text))
            return (state, _, source) => state.GetPlayer(source.ControllerId).Speed >= 4;

        // "As long as an opponent owns a card in exile" - exile is a shared zone (CR 400.1), so
        // the question is whose card it is rather than whose pile: an opponent's card you exiled
        // yourself is still theirs. Asked of the owner and never of a controller, because an
        // exiled card has no controller to ask.
        var banished = OwnsExiledLine().Match(text);
        if (banished.Success)
        {
            var mine = banished.Groups["who"].Value
                .StartsWith("you", StringComparison.OrdinalIgnoreCase);

            return (state, _, source) => state.Exile.Any(id =>
                state.TryGetObject(id, out var card)
                && (card.OwnerId == source.ControllerId) == mine);
        }

        // "As long as you have the city's blessing" (CR 702.131a). A designation rather than a
        // count: a player who had ten permanents and lost nine still has it, so this asks the
        // player and never re-counts the board.
        var blessed = CitysBlessingLine().Match(text);
        if (blessed.Success)
        {
            var mine = blessed.Groups["who"].Value.Equals(
                "you", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => mine
                ? state.GetPlayer(source.ControllerId).HasCitysBlessing
                : state.TurnOrder.Any(
                    other => other != source.ControllerId
                        && state.GetPlayer(other).HasCitysBlessing);
        }

        // "If no spells were cast last turn" and "if a player cast two or more spells last turn"
        // - the two halves of the day-night cycle as a werewolf prints it, and the only two
        // questions in the corpus about the turn before this one. Asked of every player, because
        // "no spells" means nobody's and "a player" means anybody's.
        var lastTurn = SpellsLastTurnLine().Match(text);
        if (lastTurn.Success)
        {
            var none = lastTurn.Groups["none"].Success;
            var wanted = none ? 0 : NumberWord(lastTurn.Groups["n"].Value);

            return (state, _, _) => none
                ? state.TurnOrder.All(who => state.GetPlayer(who).SpellsCastLastTurn == 0)
                : state.TurnOrder.Any(who => state.GetPlayer(who).SpellsCastLastTurn >= wanted);
        }

        // "If you've completed a dungeon" (CR 309.7), on 21 cards. A fact about a player's whole
        // game rather than about the board, so it is answered from the list of dungeons they have
        // finished and not by looking for a dungeon anywhere - a player who has completed one
        // owns no dungeon at all, which is exactly the moment the condition becomes true.
        var delved = CompletedDungeonLine().Match(text);
        if (delved.Success)
        {
            var mine = !delved.Groups["opponent"].Success;

            return (state, _, source) => mine
                ? state.GetPlayer(source.ControllerId).HasCompletedADungeon
                : state.ActivePlayers().Any(who =>
                    who != source.ControllerId && state.GetPlayer(who).HasCompletedADungeon);
        }

        // "If you're the monarch" (CR 725.1). One designation held by at most one player, so
        // this is a comparison against a single field rather than a question asked of each.
        var crowned = MonarchLine().Match(text);
        if (crowned.Success)
        {
            // "If there is no monarch" is the third state of the same field, and CR 725.1 is why
            // it is a state at all: there is no monarch in a game until an effect makes one. The
            // six cards that ask it are the ones that hand the crown out, so reading it as
            // "somebody else is the monarch" would make each of them fire exactly when it must
            // not - and would still compile.
            if (crowned.Groups["nobody"].Success)
                return (state, _, _) => state.MonarchId is null;

            var mine = crowned.Groups["who"].Value.Equals(
                "you", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => mine
                ? state.MonarchId == source.ControllerId
                : state.MonarchId is { } held && held != source.ControllerId;
        }

        // "If you have the initiative" (CR 726.1) - the monarch's twin, held the same way: one
        // designation on the state, at most one holder (CR 726.3). Only the "you" arm is read,
        // because that is the only arm the corpus prints - every "an opponent has it" wording
        // arrives inside a longer clause this vocabulary does not read, and an arm no card
        // exercises is an arm no test can keep honest.
        if (InitiativeLine().IsMatch(text))
            return (state, _, source) => state.InitiativeId == source.ControllerId;

        // "If it's night", "if it's neither day nor night" (CR 731.1). A designation the game
        // itself has rather than a player, so it sits beside the monarch — and it has three
        // states where the monarch has two: a game begins as neither and stays that way until
        // something makes it one, after which it is always exactly one of the two (CR 731.1,
        // CR 731.2c).
        //
        // The third state is the whole reason this is a comparison against one field rather than
        // a pair of flags. Ten of the thirteen cards printing a day-night condition ask for
        // "neither", and every one of them is a permanent that makes it day as it arrives — so
        // reading "neither" as "not day" would answer yes in the games where it is night, and the
        // card would set the sun back up.
        //
        // Day is read as well, though no corpus card asks for it alone: the three are one field's
        // three values, and answering two of them would be a reader that refuses a clause it
        // already knows the answer to.
        if (DayNightLine().Match(text) is { Success: true } sky)
        {
            var wanted = sky.Groups["what"].Value.StartsWith(
                "neither", StringComparison.OrdinalIgnoreCase)
                ? (bool?)null
                : sky.Groups["what"].Value.Equals("day", StringComparison.OrdinalIgnoreCase);

            return (state, _, _) => wanted is { } designation
                ? state.IsDay == designation
                : state.IsDay is null;
        }

        // "As long as you control your commander" - the lieutenant cycle. Being a commander is
        // an attribute of the card rather than of the object (CR 903.3) and survives every zone
        // change, so this compares oracle ids: a commander that has died and been recast is a
        // new object each time and is still the same commander.
        if (ControlsCommanderLine().Match(text) is { Success: true } commander)
        {
            // "Your commander" and "a commander" are different questions and the rules say so
            // outright: CR 903.3d reads "controlling a commander" as *a permanent on the
            // battlefield that is a commander*, whoever designated it. So a commander taken
            // from an opponent answers the second clause and not the first, and reading the
            // two alike would either offer the free cast that is not printed or refuse the one
            // that is. Twenty-two cards say "a commander".
            var anyones = commander.Groups["any"].Success;

            return (state, abilities, source) =>
            {
                var mine = state.GetPlayer(source.ControllerId).CommanderOracleId;
                if (!anyones && mine is null)
                    return false;

                return state.Battlefield.Any(id =>
                {
                    var obj = state.GetObject(id);

                    // Control is computed, not stored (CR 613.1b): a commander an opponent has
                    // stolen is one you no longer control, which is exactly the situation these
                    // cards are printed to reward you for avoiding.
                    if (Characteristics.Of(state, abilities, obj).ControllerId
                        != source.ControllerId)
                    {
                        return false;
                    }

                    // Being a commander is an attribute of the card rather than of the object
                    // (CR 903.3) and survives every zone change, so this compares oracle ids: a
                    // commander that has died and been recast is a new object each time and is
                    // still the same commander.
                    if (!anyones)
                        return string.Equals(obj.Card.OracleId, mine, StringComparison.Ordinal);

                    return state.TurnOrder.Any(
                        seat => state.GetPlayer(seat).CommanderOracleId is { } theirs
                            && string.Equals(obj.Card.OracleId, theirs, StringComparison.Ordinal));
                });
            };
        }

        // "If you have a full party" (CR 700.8c). A count of four roles rather than of four
        // creatures, and the rule for taking it is what makes it worth a reader of its own.
        if (FullPartyLine().IsMatch(text))
            return (state, abilities, source) => HasFullParty(state, abilities, source.ControllerId);

        // "If you dealt combat damage to a player this turn" - twenty-four cards ask it and
        // none of them could be read, in any of the three places a condition is asked from: a
        // static's "as long as", a trigger's intervening if, and an activation restriction. One
        // reader here answers all three, which is the whole reason conditions live in one place.
        var connected = DealtCombatDamageLine().Match(text);
        if (connected.Success)
        {
            var mine = connected.Groups["who"].Value.Equals(
                "you", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => mine
                ? state.GetPlayer(source.ControllerId).DealtCombatDamageToPlayerThisTurn
                : state.TurnOrder.Any(
                    other => other != source.ControllerId
                        && state.GetPlayer(other).DealtCombatDamageToPlayerThisTurn);
        }

        // "If you attacked this turn" - a fact about the player, not about any creature that
        // did the attacking. Asked by eighty-odd lines, and answered from the flag the reducer
        // sets when attackers are declared, so it stays true after every one of them has died.
        var attacked = AttackedThisTurnLine().Match(text);
        if (attacked.Success)
        {
            // "No creatures attacked this turn" is the whole table's flags rather than one
            // seat's, and it cannot be reached by negating a single player: at three seats, one
            // player not having attacked says nothing about the other two.
            if (attacked.Groups["nobody"].Success)
            {
                return (state, _, _) => state.TurnOrder.All(
                    id => !state.GetPlayer(id).AttackedThisTurn);
            }

            var theirs = attacked.Groups["who"].Value
                .StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

            var negated = attacked.Groups["not"].Success;

            return (state, _, source) => state.TurnOrder
                .Where(id => theirs ? id != source.ControllerId : id == source.ControllerId)
                .Any(id => state.GetPlayer(id).AttackedThisTurn) != negated;
        }

        // "If you gained 3 or more life this turn", "if an opponent lost life this turn" - one
        // shape covering both directions and both depths. Whose life it is, which way it went,
        // and how much of it are three independent choices, so they are read as three groups
        // rather than as a reader each.
        var moved = LifeMovedThisTurnLine().Match(text);
        if (moved.Success)
        {
            var least = moved.Groups["n"].Success ? Number(moved.Groups["n"].Value) : 1;
            var gained = !moved.Groups["dir"].Value.StartsWith("lost", StringComparison.OrdinalIgnoreCase);
            var either = moved.Groups["dir"].Value.Contains(" or ", StringComparison.OrdinalIgnoreCase);
            var theirs = moved.Groups["who"].Value.StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) => state.TurnOrder
                .Where(id => theirs ? id != source.ControllerId : id == source.ControllerId)
                .Any(id =>
                {
                    var player = state.GetPlayer(id);

                    // Losing life is tracked as a flag rather than a total, so "lost N or more"
                    // cannot be asked - and no card asks it. A card that did would go unread
                    // here rather than be answered with the wrong number.
                    var lost = player.LostLifeThisTurn;

                    if (either)
                        return player.LifeGainedThisTurn >= least || lost;

                    return gained ? player.LifeGainedThisTurn >= least : lost;
                });
        }

        // "If this land is tapped", "if this permanent is an enchantment" - the source asking
        // about itself. Worth its own reader rather than a filter over the battlefield: the
        // question is about one known object, and phrasing it as a search would find the wrong
        // one whenever a second copy is in play.
        var itself = SelfStateLine().Match(text);
        if (itself.Success)
        {
            var wantsTapped = itself.Groups["tapped"].Value
                .Equals("tapped", StringComparison.OrdinalIgnoreCase);

            var pronoun = itself.Groups["it"].Success;

            return (state, abilities, source) =>
                Subject(state, source, pronoun) is { } self
                && (self.Permanent?.IsTapped ?? false) == wantsTapped;
        }

        // "Unless it's paired with a creature with soulbond" - the soulbond status, asked from
        // one end (CR 702.95b). The pairing itself comes from the shared reader every consumer
        // of the status uses; what this adds is the qualifier, answered off the partner's
        // computed keywords so that a partner that loses its abilities stops satisfying it.
        var bonded = SelfPairedLine().Match(text);
        if (bonded.Success)
        {
            var needsSoulbond = bonded.Groups["soulbond"].Success;

            return (state, abilities, source) =>
                Subject(state, source, bonded.Groups["it"].Success) is { } self
                && state.PairedPartnerOf(self) is { } partner
                && (!needsSoulbond
                    || Characteristics.Of(state, abilities, partner)
                        .Has(Domain.Enums.KeywordAbility.Soulbond));
        }

        // "Activate only if ~'s power is 3 or greater", "as long as its power is 2 or less" -
        // one permanent's power rather than a search of the board, which is why it is not the
        // controls-with-power reader with the subject changed: that one answers "is there such a
        // creature", and with two copies of this card in play it would find the wrong one.
        var mighty = SelfPowerLine().Match(text);
        if (mighty.Success)
        {
            var threshold = Number(mighty.Groups["n"].Value);
            var atLeast = mighty.Groups["dir"].Value
                    .StartsWith("greater", StringComparison.OrdinalIgnoreCase)
                || mighty.Groups["dir"].Value
                    .StartsWith("more", StringComparison.OrdinalIgnoreCase);

            // "Its power" on an Aura is the host's - an Aura has no power at all - and the
            // possessive on "enchanted creature's power" says the same thing in full.
            var elsewhere = mighty.Groups["it"].Success || mighty.Groups["host"].Success;

            return (state, abilities, source) =>
            {
                if (Subject(state, source, elsewhere) is not { } self)
                    return false;

                // Computed rather than printed (CR 613.4): a card asking about its own power is
                // one whose power something else is expected to have changed.
                var power = Characteristics.Of(state, abilities, self).Power ?? 0;

                return atLeast ? power >= threshold : power <= threshold;
            };
        }

        // "As long as ~ is monstrous", "as long as ~ is attacking", "as long as ~ is equipped"
        // - three more questions a permanent asks about itself, each answered somewhere the
        // tapped question is not: a designation, the combat state, and what is attached to it.
        // "As long as it has a +1/+1 counter on it", "as long as ~ has a divinity counter on
        // it". A separate reader from the adjectives beside it because it is a different sentence
        // - "is monstrous" against "has a counter" - and because the counter has a name that has
        // to be carried through rather than matched against a list.
        var carrying = SelfCounterLine().Match(text);
        if (carrying.Success)
            return CountersOn(carrying);

        var standing = SelfConditionLine().Match(text);
        if (standing.Success)
        {
            var asked = standing.Groups["how"].Value.ToLowerInvariant();

            var pronoun = standing.Groups["it"].Success;

            return (state, abilities, source) =>
            {
                if (Subject(state, source, pronoun) is not { } self)
                    return false;

                return asked switch
                {
                    "monstrous" => self.Permanent?.IsMonstrous ?? false,
                    "saddled" => self.Permanent?.IsSaddled ?? false,

                    // A designation, not a count of what put it there (CR 702.112b).
                    "renowned" => self.Permanent?.IsRenowned ?? false,

                    // Asked of the permanent rather than of the computed card, because a
                    // face-down permanent has no abilities and no printed characteristics to
                    // compute from (CR 707.2) - being face down is a fact about the object.
                    "face down" => self.Permanent?.IsFaceDown ?? false,
                    "attacking" => state.Combat.Attackers.ContainsKey(self.Id),
                    "blocking" => state.Combat.Blockers.Values.Any(list => list.Contains(self.Id)),

                    // CR 301.5c: equipped means a piece of Equipment is attached to it, which is
                    // a fact about the Equipment rather than about this permanent - so it is
                    // asked of the battlefield rather than of the creature.
                    "equipped" => state.Battlefield
                        .Select(state.GetObject)
                        .Any(other => other.Permanent?.AttachedTo == self.Id
                            && other.Card.Subtypes.Contains(
                                "Equipment", StringComparer.OrdinalIgnoreCase)),

                    "enchanted" => state.Battlefield
                        .Select(state.GetObject)
                        .Any(other => other.Permanent?.AttachedTo == self.Id
                            && other.Card.Subtypes.Contains(
                                "Aura", StringComparer.OrdinalIgnoreCase)),

                    _ => false,
                };
            };
        }

        // "As long as ~ is attached to a creature" - an Aura or an Equipment asking about its
        // own host (CR 701.3a). It is the opposite end of the question the adjectives above
        // answer: "~ is equipped" asks what is attached to this permanent, and this asks what
        // this permanent is attached to. The noun goes through the shared target grammar, so
        // every filter that grammar knows arrives here already working.
        var carriedBy = AttachedToLine().Match(text);
        if (carriedBy.Success)
        {
            if (EffectPhrase.Specs.Parse("target " + carriedBy.Groups["what"].Value.Trim()) is not
                { Kind: Abilities.TargetKind.Permanent } host)
            {
                return null;
            }

            return (state, abilities, source) =>
                source.Permanent?.AttachedTo is { } worn
                && state.TryGetObject(worn, out var wearer)
                && wearer.Zone == Zone.Battlefield
                && host.ObjectFilter?.Invoke(
                    state, abilities, wearer, source.ControllerId) != false;
        }

        // "Whenever ~ attacks, if it's modified" - CR 700.9, which is three facts this file can
        // already ask one at a time and no card ever spells out: a counter on it, an Equipment
        // attached to it, or an Aura attached to it that its own controller controls.
        var altered = ModifiedLine().Match(text);
        if (altered.Success)
        {
            var pronoun = altered.Groups["it"].Success;

            return (state, abilities, source) =>
            {
                if (Subject(state, source, pronoun) is not { } self)
                    return false;

                if (self.Permanent?.Counters.Values.Any(held => held > 0) == true)
                    return true;

                // The Aura half is narrower than the Equipment half, and CR 700.9 is why: an
                // Equipment modifies whoever it is attached to, an Aura only when its controller
                // is that permanent's. An opponent's Pacifism does not modify your creature.
                var owner = Characteristics.Of(state, abilities, self).ControllerId;

                return state.Battlefield
                    .Select(state.GetObject)
                    .Any(other => other.Permanent?.AttachedTo == self.Id
                        && (other.Card.Subtypes.Contains(
                                "Equipment", StringComparer.OrdinalIgnoreCase)
                            || (other.Card.Subtypes.Contains(
                                    "Aura", StringComparer.OrdinalIgnoreCase)
                                && Characteristics.Of(state, abilities, other).ControllerId
                                    == owner)));
            };
        }

        // "As long as enchanted permanent is a creature", "as long as equipped creature is a
        // Human", "as long as enchanted creature is red". The subject is the Aura's host rather
        // than the Aura, which is the only difference from the question below - and the whole
        // difference in effect, because an Aura is never the creature it is asking about.
        var hostIs = HostTypeLine().Match(text);
        if (hostIs.Success)
        {
            var described = hostIs.Groups["what"].Value.Trim();

            // A type or a subtype is a noun and stands alone; a colour or a supertype is an
            // adjective and needs one. Tried in that order rather than guessed at, so "red"
            // becomes "red permanent" and "creature" is left as it is.
            var spec = EffectPhrase.Specs.Parse("target " + described)
                ?? EffectPhrase.Specs.Parse("target " + described + " permanent");

            if (spec is not { Kind: Abilities.TargetKind.Permanent } filter)
                return null;

            return (state, abilities, source) =>
            {
                if (source.Permanent?.AttachedTo is not { } host
                    || !state.TryGetObject(host, out var wearing))
                {
                    return false;
                }

                return filter.ObjectFilter?.Invoke(
                    state, abilities, wearing, source.ControllerId) != false;
            };
        }

        var isA = SelfTypeLine().Match(text);
        if (isA.Success)
        {
            // Read from the computed characteristics rather than the printed card, because what
            // a permanent is can be changed (CR 613 layer 4) and the cards that ask this - a
            // land that is sometimes also a creature, most of them - are exactly the ones it
            // changes for.
            if (EffectPhrase.Specs.Parse("target " + isA.Groups["what"].Value.Trim()) is not
                { Kind: Abilities.TargetKind.Permanent } spec)
            {
                return null;
            }

            var negated = isA.Groups["not"].Success;

            return (state, abilities, source) =>
            {
                if (!state.TryGetObject(source.Id, out var self) || self.Zone != Zone.Battlefield)
                    return false;

                var matches = spec.ObjectFilter?.Invoke(
                    state, abilities, self, source.ControllerId) != false;

                return matches != negated;
            };
        }

        var controls = ControlsAnyLine().Match(text);
        if (controls.Success)
        {
            var none = controls.Groups["none"].Success;

            // "Another" excludes the permanent asking (CR 109.5). Every adjective in this slot
            // already worked and this one word did not, which is the difference between a card
            // that turns itself on and one that needs a friend.
            var excludesSelf = controls.Groups["another"].Success;

            // "Defending player controls no Glimmer creatures" is the one arm of this pattern
            // whose noun arrives plural, and a six-word regex was all it had: anything with an
            // adjective in front - "no Glimmer creatures", "no untapped lands" - went to the
            // target grammar still plural, was refused, and this reader then *claimed the clause
            // and returned null*, taking it from every reader beneath as well.
            //
            // It now folds through the group grammar's own plural, which is where that
            // vocabulary lives. This file used to keep a fork of it - six words, anchored whole,
            // so it could not even reach a noun with an adjective in front - and the three
            // readers here patched around the fork in three different ways. One of the three
            // never got patched at all: the attacking count, eight hundred lines above.
            //
            // Only on the "no" arm. "A", "an" and "another" are always followed by a singular
            // noun, and singularising a word that is already singular is where a Locus loses a
            // letter - so the arm that cannot need it does not get it.
            var noun = none
                ? EffectPhrase.Specs.FoldPlural(controls.Groups["what"].Value.Trim())
                : controls.Groups["what"].Value.Trim();

            // Matched against the whole subject and not its first word: "your opponents
            // control" also begins with "you", so a prefix test on three letters read it as your
            // own board and inverted every card that says it.
            var theirBoard = !controls.Groups["who"].Value
                .StartsWith("you control", StringComparison.OrdinalIgnoreCase);

            // "A player controls" and "there are ... on the battlefield" name no side at all, so
            // the ownership test is skipped rather than answered — asking whose it is would make
            // a global question into a one-sided one, which is how a board wipe reads as a board
            // check.
            var anyone = controls.Groups["anyone"].Success;

            // "Defending player" is one particular opponent rather than any of them, and which
            // one is only knowable from combat: it is whoever the permanent asking is attacking.
            // Outside combat it names nobody, and the condition is simply false - which is the
            // right answer for an evasion ability that only matters while blockers are declared.
            var defending = controls.Groups["who"].Value
                .StartsWith("defending", StringComparison.OrdinalIgnoreCase);

            // "A Plains or a Swamp" is one condition over two nouns, and the dual lands that
            // print it would otherwise all go unread. Each side is parsed as a noun in its own
            // right, so anything one side accepts the other does too.
            var specs = new List<Abilities.TargetSpec>();

            foreach (var alternative in EitherNoun().Split(noun))
            {
                if (EffectPhrase.Specs.Parse("target " + alternative.Trim()) is not
                    { Kind: Abilities.TargetKind.Permanent } one)
                {
                    return null;
                }

                specs.Add(one);
            }

            if (specs.Count == 0)
                return null;

            var spec = specs[0];

            return (state, abilities, source) =>
            {
                Guid? defender = null;

                if (defending)
                {
                    if (!state.Combat.Attackers.TryGetValue(source.Id, out var attacking))
                        return none;

                    defender = attacking.DefendingPlayer;
                }

                var any = state.Battlefield.Any(id =>
                {
                    var obj = state.GetObject(id);
                    var controller =
                        Characteristics.Of(state, abilities, obj).ControllerId;

                    if (excludesSelf && id == source.Id)
                        return false;

                    var whoseBoard = anyone
                        || (defender is { } named
                            ? controller == named
                            : (controller == source.ControllerId) != theirBoard);

                    return whoseBoard
                        && specs.Any(one => one.ObjectFilter?.Invoke(
                            state, abilities, obj, source.ControllerId) != false);
                });

                return any != none;
            };
        }

        var opponents = OpponentsLine().Match(text);
        if (opponents.Success)
        {
            var wanted = Number(opponents.Groups["n"].Value);
            var orMore = opponents.Groups["dir"].Value.StartsWith("more", StringComparison.OrdinalIgnoreCase);

            return (state, abilities, source) =>
            {
                var count = state.TurnOrder.Count(
                    id => id != source.ControllerId && !state.GetPlayer(id).HasLost);

                return orMore ? count >= wanted : count <= wanted;
            };
        }

        return null;
    }

    /// <summary>Two conditions joined by "and" or "or", each read by everything above.</summary>
    /// <remarks>
    /// This is the only reader here that multiplies rather than adds, so it is also the only one
    /// that could quietly invent a condition. Three things stop it, and all three are refusals:
    /// <list type="bullet">
    /// <item>Both halves must parse. That single rule disposes of every false split - "you
    /// control three <em>or</em> more lands" splits into "you control three" and "more lands",
    /// neither of which is a condition, so the clause stays unread rather than becoming a
    /// question about nothing.</item>
    /// <item>If splits on <em>both</em> words are viable the clause is refused, because "A or B
    /// and C" has two readings and nothing here can tell which was printed. Same-word splits are
    /// safe to pick between: and/or are associative, so every grouping means the same thing.</item>
    /// <item>It runs only on a clause every specific reader has already refused, so the worst it
    /// can do to a card is what was already happening to it.</item>
    /// </list>
    /// </remarks>
    private static Func<GameState, IAbilitySource, GameObject, bool>? Joined(string text)
    {
        var viable = new List<(bool All, Func<GameState, IAbilitySource, GameObject, bool> Left,
            Func<GameState, IAbilitySource, GameObject, bool> Right)>();

        foreach (Match join in JoinWord().Matches(text))
        {
            var leftText = text[..join.Index];
            if (Parse(leftText) is not { } left)
                continue;

            var rightText = text[(join.Index + join.Length)..];
            if ((Parse(rightText) ?? Elided(leftText, rightText)) is not { } right)
                continue;

            viable.Add((join.Groups["and"].Success, left, right));
        }

        if (viable.Count == 0 || viable.Any(one => one.All != viable[0].All))
            return null;

        var (all, first, second) = viable[0];

        return all
            ? (state, abilities, source) =>
                first(state, abilities, source) && second(state, abilities, source)
            : (state, abilities, source) =>
                first(state, abilities, source) || second(state, abilities, source);
    }

    /// <summary>
    /// "You control an artifact and an enchantment" — the half that leaves its subject out.
    /// </summary>
    /// <remarks>
    /// English drops a repeated subject and a parser cannot, so the second half is re-read with
    /// the first half's subject put back. It is deliberately narrow in both directions: the tail
    /// must begin with a determiner, so "you control a creature and it's your turn" is never
    /// mangled into a question about controlling a turn, and the subject must be one of the
    /// phrases that can carry a bare noun after it.
    /// </remarks>
    private static Func<GameState, IAbilitySource, GameObject, bool>? Elided(
        string left, string right)
    {
        if (!ElidedTail().IsMatch(right))
            return null;

        var subject = SubjectPrefix().Match(left);

        return subject.Success ? Parse(subject.Value + right) : null;
    }

    /// <remarks>
    /// Both words are one pattern so that the split points arrive in the order they are printed
    /// and the ambiguity check above can see that two different words matched.
    /// </remarks>
    [GeneratedRegex(@" (?:(?<and>and)|or) ", RegexOptions.IgnoreCase)]
    private static partial Regex JoinWord();

    [GeneratedRegex(
        @"^(an?|no|another|\d+|one|two|three|four|five|six|seven|eight|nine|ten) ",
        RegexOptions.IgnoreCase)]
    private static partial Regex ElidedTail();

    [GeneratedRegex(
        @"^(you control|an opponent controls|a player controls|you have|an opponent has) ",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubjectPrefix();

    /// <summary>
    /// "You control two or more other lands" — a count of permanents you control (CR 109.5).
    /// </summary>
    /// <remarks>
    /// "Other" excludes the permanent asking the question, which matters exactly here: a land
    /// checking how many lands you control is not yet on the battlefield when the replacement is
    /// applied, so "other" and "any" would give the same answer today — but they will not once
    /// something else asks the same question, and reading the word is free.
    /// </remarks>
    private static Func<GameState, IAbilitySource, GameObject, bool>? Counting(Match m)
    {
        var wanted = Number(m.Groups["n"].Value);

        // "You control at least three other enchantments" is "three or more" in the words seven
        // cards happen to use, and it is the same threshold. It is an alternative in the pattern
        // rather than a rewrite over the whole clause because "at least" is also how the
        // mana-spent reader beside this one is printed - "at least three white mana was spent to
        // cast it" - and a blanket rewrite would have taken that reader's own wording away.
        var orMore = m.Groups["atleast"].Success
            || m.Groups["dir"].Value.StartsWith("more", StringComparison.OrdinalIgnoreCase);
        var excludesSelf = m.Groups["other"].Success;
        var basicOnly = m.Groups["basic"].Success;

        // "You control exactly one creature" — a window rather than a threshold, and the whole
        // point of the cards that ask it. Read as its own comparison because both thresholds are
        // wrong for it in a way that plays: "or more" leaves the bonus on with a second creature
        // out, "or fewer" leaves it on with none.
        var exactly = m.Groups["exactly"].Success;

        // "Your opponents control three or more lands" counts across all of them together, which
        // is what the plural says - unlike "an opponent controls ...", which asks about each in
        // turn. The two read alike and mean different things at more than two seats.
        var theirs = m.Groups["who"].Value.StartsWith("your opponents", StringComparison.OrdinalIgnoreCase);

        // "An opponent controls four or more lands" is any one of them holding four (CR 102.1),
        // which is the singular of the clause above and not a synonym for it: two opponents with
        // two lands each answer the pooled question and not this one. Five cards print it, and
        // every one of them is a card that punishes a *player* for their board - a land
        // destruction activation, a cost reduction, an upkeep trigger - so a pooled reading would
        // turn each of them on at a table where nobody has done anything.
        var eachInTurn = m.Groups["oneof"].Success;

        // "A player controls" names nobody in particular, which is how a question about the
        // battlefield reaches this reader: the ownership test is skipped rather than answered,
        // because asking whose permanent it is would turn a count of the board into one side's.
        var anyone = m.Groups["anyone"].Success;

        // The noun goes through the target grammar, so every filter that grammar understands —
        // types, subtypes, ownership — works here without a second vocabulary.
        // The target grammar is written around a singular noun, and a count is always plural.
        // "Tapped creatures" is an adjective and a noun, and only the noun is plural. The
        // adjective goes through untouched to the target grammar, which is where the vocabulary
        // for it already lives - so this does not need to know what "tapped" means.
        var noun = EffectPhrase.Specs.FoldPlural(m.Groups["what"].Value.Trim());
        var spec = EffectPhrase.Specs.Parse("target " + noun);
        if (spec is null || spec.Kind != Abilities.TargetKind.Permanent)
            return null;

        bool Passes(int count) =>
            exactly ? count == wanted : orMore ? count >= wanted : count <= wanted;

        return (state, abilities, source) =>
        {
            // Tallied once per controller rather than swept once per subject. The four subjects
            // this reader answers - yours, all your opponents' together, any one opponent's, and
            // nobody's in particular - are four sums over the same tally, and a sweep each would
            // be four places for the filter and the "another" exclusion to drift apart.
            var byController = new Dictionary<Guid, int>();
            var everyone = 0;

            foreach (var id in state.Battlefield)
            {
                if (excludesSelf && id == source.Id)
                    continue;

                var obj = state.GetObject(id);

                if (basicOnly
                    && !obj.Card.Supertypes.Contains("Basic", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The filter is asked without an ability source, so it sees printed
                // characteristics. That is right for a replacement applied as a permanent
                // arrives: nothing has had a chance to change it yet.
                if (spec.ObjectFilter?.Invoke(state, abilities, obj, source.ControllerId) == false)
                    continue;

                // Computed, like every other "do you control this" question — a land you have
                // taken counts towards the lands you control (CR 613.1b).
                var controller = Characteristics.Of(state, abilities, obj).ControllerId;

                byController[controller] = byController.GetValueOrDefault(controller) + 1;
                everyone++;
            }

            if (anyone)
                return Passes(everyone);

            if (eachInTurn)
            {
                return state.TurnOrder
                    .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                    .Any(id => Passes(byController.GetValueOrDefault(id)));
            }

            var mine = byController.GetValueOrDefault(source.ControllerId);

            return Passes(theirs ? everyone - mine : mine);
        };
    }

    /// <summary>
    /// A question about permanents that crossed the battlefield's edge this turn (CR 400.7).
    /// </summary>
    /// <remarks>
    /// Arriving and dying are one question over two lists, and the difference between them is
    /// which list is handed in. Written once because the four things that vary — how many, whose,
    /// which noun, and whether the asking permanent counts — are the same four either way, and a
    /// second copy of them is a second place for "another" to be forgotten.
    /// </remarks>
    /// <param name="Wanted">How many crossings the clause is satisfied by.</param>
    /// <param name="ExcludesSelf">Whether "another" took the asking permanent out (CR 109.5).</param>
    /// <param name="NonlandOnly">Whether the noun was "nonland", which no type table holds.</param>
    /// <param name="TheirBoard">
    /// Whether the clause names an opponent's side. Counted per opponent rather than across all
    /// of them, because "an opponent had two or more creatures enter" is any one of them having
    /// two (CR 102.1) and a sum would fire on two opponents with one each.
    /// </param>
    /// <param name="Types">Any of these printed card types, or <c>None</c> for any permanent.</param>
    /// <param name="Filter">
    /// The shared card-filter vocabulary's answer for a noun the type table cannot name — a
    /// creature type, or a list of them. Null when the types above are the whole question.
    /// </param>
    private sealed record Crossing(
        int Wanted,
        bool ExcludesSelf,
        bool NonlandOnly,
        bool TheirBoard,
        Domain.Enums.CardType Types,
        string? Filter)
    {
        public bool Happened(
            IEnumerable<BattlefieldCrossing> crossings, GameState state, GameObject source)
        {
            var seen = crossings.ToList();

            int For(Guid who) => seen.Count(one =>
                one.ControllerId == who
                && (!ExcludesSelf || one.Id != source.Id)
                && !(NonlandOnly && one.Card.CardTypes.HasFlag(Domain.Enums.CardType.Land))
                && (Types == Domain.Enums.CardType.None
                    || (one.Card.CardTypes & Types) != Domain.Enums.CardType.None)
                && (Filter is null || Abilities.SearchFilters.Matches(Filter, one.Card)));

            if (!TheirBoard)
                return For(source.ControllerId) >= Wanted;

            return state.TurnOrder
                .Where(id => id != source.ControllerId && !state.GetPlayer(id).HasLost)
                .Any(id => For(id) >= Wanted);
        }
    }

    /// <summary>The crossing question a matched clause asks, or null if its noun is unreadable.</summary>
    /// <remarks>
    /// The type table is tried before the card filter and not instead of it. That order is the
    /// whole fix to a reader that had been claiming clauses and then refusing them: "another
    /// Knight entered the battlefield under your control this turn" reached a table that knows
    /// only card types, which returned null and took the line away from everything below.
    /// </remarks>
    private static Crossing? Crossings(Match m)
    {
        var noun = m.Groups["what"].Value.Trim();

        var nonlandOnly = noun.StartsWith("nonland", StringComparison.OrdinalIgnoreCase);
        if (nonlandOnly)
            noun = noun["nonland".Length..].Trim();

        var types = Domain.Enums.CardType.None;
        string? filter = null;

        if (EffectPhrase.Specs.PermanentTypes(noun) is { } named)
        {
            types = named.Aggregate(
                Domain.Enums.CardType.None, (running, one) => running | one);
        }
        else
        {
            filter = EffectPhrase.CardFilterNamed(Either().Replace(noun, " or "));
            if (filter is null)
                return null;
        }

        // Which side is said twice on this family - once by the subject and once by the
        // possessive - and either alone is enough, because "an opponent had a creature enter"
        // and "a creature entered under an opponent's control" are the same sentence.
        var theirs = m.Groups["theirs"].Success
            || m.Groups["who"].Value.StartsWith("an opponent", StringComparison.OrdinalIgnoreCase);

        return new Crossing(
            m.Groups["n"].Success ? Number(m.Groups["n"].Value) : 1,
            m.Groups["another"].Success,
            nonlandOnly,
            theirs,
            types,
            filter);
    }

    private static int Number(string word) =>
        int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : word.ToLowerInvariant() switch
            {
                "one" or "a" or "an" => 1,
                "six" => 6,
                "seven" => 7,
                "eight" => 8,
                "nine" => 9,
                "ten" => 10,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                _ => 1,
            };

    /// <remarks>
    /// The noun may be several words - "no untapped lands", "no other creatures" - and a single
    /// word was all this accepted, so every adjective in that slot lost the whole condition.
    /// </remarks>
    [GeneratedRegex(
        @"^you control no (?<what>[A-Za-z]+( [A-Za-z]+)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoneLine();

    /// <summary>The same emptiness asked of the whole board rather than of one player.</summary>
    /// <remarks>
    /// Both word orders, because the corpus prints both and they are one question: the four
    /// sweeper enchantments that sacrifice themselves say "if no creatures are on the
    /// battlefield" while everything else says "if there are no creatures". A second reader for
    /// the second wording would be a second place for the answer to drift.
    /// </remarks>
    [GeneratedRegex(
        @"^(there are no (?<what>[A-Za-z]+( [A-Za-z]+)*)"
            + @"|no (?<what>[A-Za-z]+( [A-Za-z]+)*) are) on the battlefield$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoneOnBattlefieldLine();

    /// <summary>"Three or more creatures with different powers" — distinct values, not a tally.</summary>
    [GeneratedRegex(
        @"^you control (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more "
            + @"(?<what>[A-Za-z][A-Za-z0-9 ]*?) with different powers$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DistinctPowersLine();

    /// <remarks>
    /// "There's a Lesson card in your graveyard" is "there are one or more" in the words a card
    /// happens to use, so it is rewritten onto the counting reader rather than given its own.
    /// <para>
    /// The contraction is optional because five cards print it out in full — "there is a Desert
    /// card in your graveyard" — and one apostrophe was the whole of the difference. It is worth
    /// more than those five: the disjunction reader at the bottom of this file can only join two
    /// halves it can each read, so a wording missed here silently costs every clause containing
    /// it as well.
    /// </para>
    /// <para>
    /// "An artifact card is in your graveyard" is the same sentence with the pile moved to the
    /// back, which is the other half of a pair this file has been caught missing before. Both
    /// spellings share one pattern, and therefore one rewrite, rather than getting a reader each:
    /// the alternative is two places for the same answer to drift apart.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?:there(?:'s| is) an? (?<what>[A-Za-z][A-Za-z ]*?) card"
            + @"|an? (?<what>[A-Za-z][A-Za-z ]*?) card is) in your graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OneInGraveyardLine();

    /// <summary>"Seven or more cards are in your graveyard" — the count, said backwards.</summary>
    /// <remarks>
    /// "Or fewer" is deliberately not admitted. No card prints it in this word order, and the
    /// rewrite it feeds builds an "or more" clause unconditionally — so accepting the word here
    /// would silently invert every card that used it.
    /// <para>
    /// The number class is the one the reader it rewrites onto accepts, and no wider. "Twenty or
    /// more creature cards are in your graveyard" is one card and is left unread, because a
    /// rewrite that produced a clause nothing downstream matches would look like a reader and
    /// behave like a refusal — the worst of both.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more "
            + @"(?<what>[A-Za-z]+ )?cards are in your graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraveyardCountReversedLine();

    /// <remarks>
    /// "Exactly one creature" is a third comparison rather than a third reader, the way the hand
    /// count already reads it. It has to be its own arm and cannot be folded into "or fewer":
    /// the cards that ask it — the ones that pump your lone creature — are turned <em>off</em> by
    /// a second creature arriving, and "one or fewer" would leave them on with none at all.
    /// <para>
    /// "A player controls" names no side and is what the battlefield-wide rewrite above produces,
    /// the way the emptiness rewrite already produces it for the ownership reader. It is a third
    /// answer rather than the union of the two: at any number of seats, a count of everybody's
    /// permanents is not either player's count and cannot be reached by inverting one.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|your opponents|(?<oneof>an opponent)|(?<anyone>a player)) controls? "
            + @"((?<exactly>exactly) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<atleast>at least) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|fewer)) "
            + @"(?<other>other )?(?<basic>basic )?(?<what>[a-z]+( [a-z]+)*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CountLine();

    [GeneratedRegex(@"\s+or\s+an?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex EitherNoun();

    /// <summary>
    /// The permanent a self-condition is about: the host when a pronoun is used on something
    /// attached, and the source otherwise.
    /// </summary>
    /// <remarks>
    /// "Enchanted creature gets +1/+1 as long as it's attacking" is about the creature, not the
    /// Aura — an Aura is not a creature and never attacks, so a reading that asked about itself
    /// would never be true. Only the pronoun redirects: "~ is untapped" on an Equipment is about
    /// the Equipment, which can perfectly well be tapped.
    /// </remarks>
    private static GameObject? Subject(GameState state, GameObject source, bool pronoun)
    {
        var id = pronoun && source.Permanent?.AttachedTo is { } host ? host : source.Id;

        return state.TryGetObject(id, out var found) && found.Zone == Zone.Battlefield
            ? found
            : null;
    }

    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+) is|(?<it>it)('s| is)) (?<tapped>tapped|untapped)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfStateLine();

    /// <summary>"It's paired with a creature with soulbond" — the status, asked from one end.</summary>
    [GeneratedRegex(
        @"^(~ is|(?<it>it)('s| is)) paired with a(nother)? creature"
            + @"( with (?<soulbond>soulbond))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfPairedLine();

    /// <summary>"~'s power is 3 or greater" — one permanent's power, not a search for one.</summary>
    /// <remarks>
    /// The three subjects are one pattern because they differ only in where the permanent is
    /// found: the source names itself, the pronoun and the possessive both name whatever the
    /// source is attached to. Splitting them would put the comparison in three places, and the
    /// comparison is the half that plays wrong when it drifts.
    /// <para>
    /// The pronoun is spelled "its" and not "it's". A possessive pronoun takes no apostrophe,
    /// which is the whole difference between this and every other pronoun clause in this file -
    /// and matching the contraction here reads nothing, because no card prints it.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+)'s|(?<it>its)|(?<host>(enchanted|equipped) [a-z]+)'s) power is "
            + @"(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>greater|more|less|fewer)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfPowerLine();

    /// <remarks>
    /// Renowned joins the designations rather than the counters, and the distinction is the
    /// point of CR 702.112b: it is a marker that survives every counter being removed, so a
    /// reader that answered it by looking for +1/+1 counters would turn the card off the moment
    /// something shrank the creature it had rewarded.
    /// </remarks>
    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+) is|(?<it>it)('s| is)) "
            + @"(?<how>monstrous|saddled|renowned|attacking|blocking|equipped|enchanted|face down)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfConditionLine();

    /// <summary>
    /// Whether the permanent a counter clause is about is carrying enough of that counter.
    /// </summary>
    /// <remarks>
    /// Shared by the two word orders — "~ has three or more ki counters on it" and "there are
    /// three or more ki counters on ~" — because they are one question and a second copy of the
    /// answer is a second thing to get wrong. The name is normalised the way the engine stores
    /// it when a counter is put on: lowercase for a named counter, printed as-is for the two
    /// written as numbers, so a reader that lowercased "+1/+1" cannot go looking for a counter
    /// nothing ever adds.
    /// </remarks>
    private static Func<GameState, IAbilitySource, GameObject, bool> CountersOn(Match m)
    {
        var kind = m.Groups["kind"].Value is "+1/+1" or "-1/-1"
            ? m.Groups["kind"].Value
            : m.Groups["kind"].Value.ToLowerInvariant();

        var least = m.Groups["n"].Success ? Number(m.Groups["n"].Value) : 1;
        var pronoun = m.Groups["it"].Success;

        // "No counters" is the opposite comparison rather than a threshold of zero, and the two
        // are only distinguishable here: every other clause this reads asks for "at least".
        var none = m.Groups["none"].Success;

        // "~ doesn't have a +1/+1 counter on it" is the same threshold with the verb negated.
        // Applied after the comparison rather than folded into it, so the fail-closed answer
        // below stays false in both directions - a permanent that is gone must not satisfy a
        // clause about what it is not carrying either.
        var negated = m.Groups["not"].Success;

        return (state, _, source) =>
        {
            // No subject is no answer, in both directions. Reading a permanent that has left
            // the battlefield as one carrying no counters would make "there are no depletion
            // counters on ~" true of a land that is not there, which is the fail-open half of
            // this pair and the only one that plays.
            if (Subject(state, source, pronoun) is not { } self)
                return false;

            var held = self.Permanent?.Counters.GetValueOrDefault(kind) ?? 0;

            return (none ? held == 0 : held >= least) != negated;
        };
    }

    /// <remarks>
    /// The counter name is a single word or one of the two written as numbers. Admitting a phrase
    /// there would read "a +1/+1 counter on target creature" as a counter called "on target
    /// creature", which is the mistake the noun-phrase class in the target grammar exists to stop.
    /// <para>
    /// The count is here as well as on <see cref="HasCounterLine"/> above, and the two do not
    /// overlap: that pattern's name class has no digits in it, so "~ has three or more +1/+1
    /// counters on it" falls past it and arrives here. Widening its class instead would have made
    /// it claim every clause this one reads and lose the host redirect below with them.
    /// </para>
    /// <para>
    /// The two ways a card says the counter is absent are both here and neither can be dropped.
    /// "~ has no charge counters on it" is the count at zero; "~ doesn't have a +1/+1 counter on
    /// it" is the ordinary clause with the verb negated, and it carries the article rather than
    /// the word "no" - so folding it onto the <c>none</c> group would need the article to mean
    /// zero, which it does not anywhere else in this file.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+)|(?<it>it)) (has|(?<not>doesn't have)) "
            + @"(an?|(?<none>no)|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more) "
            + @"(?<kind>[+][1]/[+][1]|[-][1]/[-][1]|[a-z]+) counters? on (it|him|her)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfCounterLine();

    /// <summary>"There are three or more brick counters on ~" — the other word order.</summary>
    /// <remarks>
    /// "There are no depletion counters on ~" is the same clause with the count at zero, and
    /// eleven lines ask it — every land that pays itself out in counters and sacrifices itself
    /// when they run out. It cannot be folded into "one or more" with the count set to zero:
    /// that comparison is <em>at least</em>, which is true of every permanent on the board, so
    /// the land would sacrifice itself the moment it arrived.
    /// </remarks>
    [GeneratedRegex(
        @"^there are "
            + @"(an?|(?<none>no)|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more) "
            + @"(?<kind>[+][1]/[+][1]|[-][1]/[-][1]|[a-z]+) counters? on (~|(?<it>it))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ThereAreCountersLine();

    /// <remarks>
    /// Case-sensitive on the description, because a capital is what separates a subtype from an
    /// ordinary word here as everywhere else: "an Equipment" is a subtype and "a creature" is a
    /// card type.
    /// </remarks>
    [GeneratedRegex(
        @"^(enchanted|equipped) [a-z]+ is (an? )?(?<what>[A-Za-z][A-Za-z ]*)$",
        RegexOptions.None)]
    private static partial Regex HostTypeLine();

    /// <remarks>
    /// "It" is the source here rather than a target: these clauses hang off the permanent's own
    /// triggered ability, and the pronoun in that sentence has only one thing it can mean.
    /// </remarks>
    [GeneratedRegex(
        @"^(~|this permanent|this card|this creature|it)('s| is|s are| was)"
            + @"( (?<not>not|n't))? an? (?<what>[a-z ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfTypeLine();

    [GeneratedRegex(
        @"^(~|it|this spell) ((was|were)|(?<not>wasn't|weren't|was not)) kicked$",
        RegexOptions.IgnoreCase)]
    private static partial Regex WasKickedLine();

    /// <summary>"It was kicked twice" (CR 702.33d).</summary>
    [GeneratedRegex(
        @"^(~|it|this spell|this creature) was kicked twice$", RegexOptions.IgnoreCase)]
    private static partial Regex KickedTwiceLine();

    /// <summary>"It was kicked with its {2}{R} kicker" (CR 702.33f).</summary>
    [GeneratedRegex(
        @"^(~|it|this spell|this creature) was kicked with its "
            + @"(?<cost>(\{[^}]+\})+) kicker$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KickedWithLine();

    /// <summary>"It was cast using teamwork" (CR 702.194b).</summary>
    [GeneratedRegex(
        @"^(~|it|this spell) was cast using teamwork$", RegexOptions.IgnoreCase)]
    private static partial Regex TeamworkCastLine();

    /// <summary>"If the gift was promised" / "if the gift wasn't promised" (CR 702.174k).</summary>
    /// <remarks>
    /// Also "if its gift cost was paid", which is how CR 702.174b spells the same fact inside
    /// the trigger it defines. CR 702.174k makes the two one thing: declaring the intention to
    /// pay is what promising is.
    /// </remarks>
    [GeneratedRegex(
        @"^(the gift ((was)|(?<not>wasn't|was not)) promised"
            + @"|its gift cost ((was)|(?<not>wasn't|was not)) paid)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GiftPromisedLine();

    /// <summary>"If it was bargained" (CR 702.166b).</summary>
    [GeneratedRegex(@"^(~|it|this spell) was bargained$", RegexOptions.IgnoreCase)]
    private static partial Regex WasBargainedLine();

    [GeneratedRegex(@"^you cast (it|~|this spell)$", RegexOptions.IgnoreCase)]
    private static partial Regex WasCastLine();

    /// <remarks>
    /// "Mana from a Treasure was spent" is deliberately not admitted: where a mana came from is
    /// not on this record, and answering it from the colours would be a guess.
    /// <para>
    /// The two counted arms are written before the plain one for readability rather than for
    /// correctness - "at least three white mana" cannot reach the plain arm, whose word class
    /// stops at a space - and each has its own group, because a shared one would leave the
    /// handler unable to tell "three mana" from "three white mana" and it would answer the
    /// first question with the second's number.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((?<none>no) mana|(?<nocolour>no colored) mana"
            + @"|at least (?<n>[a-z]+|\d+) (?<colour>white|blue|black|red|green) mana"
            + @"|at least (?<same>[a-z]+|\d+) mana of the same color"
            + @"|at least (?<n>[a-z]+|\d+) mana|(?<symbols>(\{[WUBRG]\})+))"
            + @" was spent to cast (it|~|this spell)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ManaSpentLine();

    /// <summary>One of the five colours by its printed word, or null (CR 105.1).</summary>
    /// <remarks>
    /// Shared by the devotion reader and the mana-spent one because they ask the same question of
    /// the same five words. It is deliberately not the filter vocabulary's table: that one reads
    /// a colour attached to a noun, and both of these read a colour standing on its own.
    /// </remarks>
    private static Domain.Enums.ManaColor? ColourNamed(string word) => word.ToLowerInvariant() switch
    {
        "white" => Domain.Enums.ManaColor.White,
        "blue" => Domain.Enums.ManaColor.Blue,
        "black" => Domain.Enums.ManaColor.Black,
        "red" => Domain.Enums.ManaColor.Red,
        "green" => Domain.Enums.ManaColor.Green,
        _ => null,
    };

    [GeneratedRegex(@"\{(?<c>[WUBRG])\}", RegexOptions.IgnoreCase)]
    private static partial Regex ManaSymbolsIn();

    /// <remarks>
    /// "From your hand" is deliberately not admitted: where a spell was cast from is on the cast
    /// event and not on this count, and answering it from the count would be wrong for anything
    /// flashed back or cast from exile.
    /// <para>
    /// The kind is a phrase rather than the two words the tallies answer, because the commonest
    /// one printed is "an instant or sorcery spell" and neither tally can be asked it. Any phrase
    /// the shared card-filter vocabulary cannot name still leaves the clause unread.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^you(('ve| have)|(?<not> haven't| have not)) cast "
            + @"(an?|(?<n>\d+|one|two|three|four|five) or more) "
            + @"(?<kind>[A-Za-z][A-Za-z/ ]*? )?spells? this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpellsCastThisTurnLine();

    /// <summary>"You've drawn two or more cards this turn" (CR 121.1).</summary>
    [GeneratedRegex(
        @"^you('ve| have) drawn (an?|(?<n>\d+|one|two|three|four|five) or more) "
            + @"cards? this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CardsDrawnThisTurnLine();

    /// <summary>"An opponent has three or more poison counters" (CR 122.1).</summary>
    [GeneratedRegex(
        @"^(?<who>an opponent|you) (has|have) "
            + @"(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or more poison counters$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PoisonCountLine();

    /// <summary>"~ has counters on it", "~ has four or more counters on it" (CR 122.1).</summary>
    /// <remarks>
    /// Deliberately has no <em>name</em>. A card asking this is one that then moves, removes or
    /// counts <em>all</em> of them, and giving the pattern a name slot would have it claim the
    /// named clauses beside it and answer a narrower question with a wider number.
    /// <para>
    /// It does have a count, and the count replaced a literal "one or more" that had never
    /// matched anything: no corpus card says "has one or more counters on it", while four say
    /// "has <em>N</em> or more counters on it". The general form subsumes the dead one, so this
    /// is one alternative fewer rather than one more.
    /// </para>
    /// <para>
    /// The two named readers - one in front of this in the chain and one behind it - cannot
    /// reach a clause this reads and this cannot reach one of theirs: both of them require a name
    /// word between the number and "counters", and this requires the word "counters" to follow
    /// the number directly. A clause carries one or the other and never both, so the order they
    /// sit in does not decide anything.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+)|(?<it>it)) has "
            + @"(an? |(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more )?"
            + @"counters? on (it|him|her)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AnyCounterLine();

    /// <summary>"An opponent is poisoned" - one or more poison counters (CR 122.1f).</summary>
    [GeneratedRegex(
        @"^(?<who>an opponent|defending player) is poisoned$", RegexOptions.IgnoreCase)]
    private static partial Regex PoisonedLine();

    /// <summary>"~ is attached to a creature" - the attachment asking about its host.</summary>
    [GeneratedRegex(
        @"^(~|(?<it>it))(?:'s| is) attached to an? (?<what>[A-Za-z][A-Za-z ]*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedToLine();

    /// <summary>"If it's modified" (CR 700.9).</summary>
    [GeneratedRegex(
        @"^((~|(this|the) [a-z]+)|(?<it>it))(?:'s| is) modified$", RegexOptions.IgnoreCase)]
    private static partial Regex ModifiedLine();

    /// <summary>"It's attacking alone" — the only attacker in this declaration (CR 506.5).</summary>
    [GeneratedRegex(
        @"^(~|it|this creature)('s| is) attacking alone$", RegexOptions.IgnoreCase)]
    private static partial Regex AttackingAloneLine();

    /// <summary>"Three or more creatures are attacking" — a described attacker, not the source.</summary>
    /// <remarks>
    /// Sits behind the clause above rather than widening it: "attacking alone" is a fact about
    /// <em>this</em> permanent and this is a count of the declaration, and a pattern that read
    /// both would have to be trusted to know which - the near-identical pair that reads correctly
    /// and plays wrong.
    /// </remarks>
    [GeneratedRegex(
        @"^(an?|(?<exactly>exactly) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more) "
            + @"(?<what>[a-z][a-z ]*?) (is|are) attacking$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttackingNounLine();

    /// <summary>"~ is blocked" — one or more blockers were declared for it (CR 509.1h).</summary>
    [GeneratedRegex(@"^(~|it|this creature)('s| is) blocked$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfBlockedLine();


    /// <summary>"It has a depletion counter on it", and its numbered form.</summary>
    [GeneratedRegex(
        @"^(~|it|this [a-z]+) has (an?|(?<n>\d+|one|two|three|four|five) or more) "
            + @"(?<kind>[a-z+/-]+(?: [a-z+/-]+)?) counters? on (it|him|her)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HasCounterLine();

    /// <summary>Where a card is asking whether it is (CR 400.1).</summary>
    /// <remarks>
    /// The zone phrase carries its own preposition, because the preposition is part of how each
    /// zone is named: a card is <em>in</em> a graveyard and <em>on</em> the battlefield, and a
    /// pattern that read the preposition separately would accept "in the battlefield", which is
    /// not English any card prints.
    /// </remarks>
    [GeneratedRegex(
        @"^(~|this card|it)(?:'s| is)(?<not>n't| not)? "
            + @"(?<z1>in your graveyard|in your hand|in exile|exiled|on the battlefield"
            + @"|on the stack|in the command zone)"
            + @"( or (?<z2>in your graveyard|in your hand|in exile|exiled|on the battlefield"
            + @"|on the stack|in the command zone))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex InZoneLine();

    /// <summary>A printed zone phrase as the zone it names (CR 400.1).</summary>
    private static Zone? ZoneNamed(string phrase) => phrase.ToLowerInvariant() switch
    {
        "in your graveyard" => Zone.Graveyard,
        "in your hand" => Zone.Hand,
        "in exile" or "exiled" => Zone.Exile,
        "on the battlefield" => Zone.Battlefield,
        "on the stack" => Zone.Stack,
        "in the command zone" => Zone.Command,
        _ => null,
    };

    /// <summary>"No opponent controls a Wall" - the negation said from the other end.</summary>
    [GeneratedRegex(
        @"^no opponent controls an? (?<what>[A-Za-z][A-Za-z0-9 ]*)$", RegexOptions.None)]
    private static partial Regex NoOpponentControlsLine();

    /// <remarks>
    /// "Defending player controls more lands than you" is deliberately not admitted, and the
    /// measurement is why: the one card that prints it - Aerial Surveyor - asks it as an
    /// intervening-if on an attack trigger, and a trigger predicate is handed the state as it
    /// was <em>before</em> the event (CR 603.6), where no attacker has been declared and there
    /// is no defending player to count. Read here it compiled clean and the card never fired
    /// once; answering "true" instead would be wrong for the statics that share this reader.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>an opponent|you) controls? (?<dir>more|fewer) (?<what>[a-z]+( [a-z]+)*)"
            + @" than (you|they do|each opponent|any opponent)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MoreThanLine();

    [GeneratedRegex(
        @"^(?<who>you|an opponent) (?<dir>gained or lost|gained|lost|have gained|has gained)"
            + @"( (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more)?"
            + @" life this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LifeMovedThisTurnLine();

    /// <remarks>
    /// The negated arm admits only "you", and that is a refusal rather than an omission. "An
    /// opponent hasn't attacked this turn" would mean <em>some</em> opponent has not, which is
    /// not the negation of "an opponent attacked" and would need a different loop - and no card
    /// prints it, so the branch would exist to be wrong in.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<who>you|an opponent) (have |has |'ve )?attacked this turn"
            + @"|(?<who>you) (?<not>didn't|haven't) attack(ed)? (with a creature )?this turn"
            + @"|(?<nobody>no creatures attacked) this turn)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttackedThisTurnLine();

    /// <remarks>
    /// The negative is the same record read the other way round rather than a reader of its own,
    /// and it is one printed card - Titan Hunter, which punishes a turn in which nothing died.
    /// A condition with two ways to be satisfied and a pattern for only one of them is this
    /// file's own recurring shape; here the second way is the complement of the first.
    /// </remarks>
    [GeneratedRegex(
        @"^(a creature (died|was put into a graveyard from the battlefield)"
            + @"|(?<none>no creatures died)) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiedThisTurnLine();

    /// <summary>The same fact asked as a number rather than as a yes or no.</summary>
    [GeneratedRegex(
        @"^(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more creatures "
            + @"(died|were put into graveyards from the battlefield) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreaturesDiedCountLine();

    /// <summary>"~ entered this turn" - the permanent asking, not the trigger's subject.</summary>
    [GeneratedRegex(@"^(~|it) entered (the battlefield )?this turn$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfEnteredThisTurnLine();

    /// <summary>Both spellings of "a permanent of yours left the battlefield this turn".</summary>
    [GeneratedRegex(
        @"^a permanent (left the battlefield under your control|you controlled left the "
            + @"battlefield) this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LeftBattlefieldThisTurnLine();

    /// <summary>"Two or more nonland permanents entered the battlefield under your control."</summary>
    /// <remarks>
    /// Both word orders are one pattern. English moves the subject to the front and turns the
    /// verb into an infinitive - "you had another creature <em>enter</em> the battlefield under
    /// your control this turn" - and that is a rephrasing of the same fact, not a second one; a
    /// reader each would be two places for "another" to be dropped.
    /// <para>
    /// The noun class admits commas so a list of creature types reaches the shared card-filter
    /// vocabulary whole. It cannot swallow the count in front of it: every alternative there is
    /// anchored at the start of the clause and the noun is lazy, so "two or more creatures" takes
    /// the counted branch before the noun is looked at.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((?<who>you|an opponent) had )?"
            + @"(an?|(?<another>another)|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or more) (?<what>[a-z][a-z, ]*?)s? enter(ed)? the battlefield under "
            + @"(your|(?<theirs>an opponent's)|their) control this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnteredUnderYourControlLine();

    /// <summary>"Another Human died under your control this turn" (CR 700.4).</summary>
    /// <remarks>
    /// Distinct from the game-wide death count, and the word "your" is the whole distinction:
    /// these cards reward you for your own losses, and a reading that counted everybody's would
    /// be turned on by an opponent's creature dying.
    /// </remarks>
    [GeneratedRegex(
        @"^(an?|(?<another>another)|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or more) (?<what>[a-z][a-z, ]*?)s? died under your control this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiedUnderControlLine();

    /// <summary>"You descended this turn" (CR 700.11).</summary>
    [GeneratedRegex(@"^you descended this turn$", RegexOptions.IgnoreCase)]
    private static partial Regex DescendedThisTurnLine();

    [GeneratedRegex(
        @"^you have (?<n>\d+|one|two|three|four|five) or (?<dir>more|fewer) opponents$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OpponentsLine();

    /// <remarks>
    /// The noun is a phrase and not a word, because the commonest filtered pile in the corpus is
    /// "instant and/or sorcery cards" and a single-word class could not name it. Every extra word
    /// still has to be one the shared card-filter vocabulary knows, so widening the class costs
    /// nothing in safety: a phrase it cannot name leaves the condition unread exactly as before.
    /// <para>
    /// "There are no cards in your graveyard" is the same count at zero and cannot be folded into
    /// "one or fewer": the cards that ask it are turned <em>off</em> by a single card arriving,
    /// and the comparison the numbered arm builds is at-most rather than exactly.
    /// </para>
    /// <para>
    /// The noun may not contain "among", and the lookahead that says so is load-bearing. Delirium
    /// asks for "four or more card <em>types</em> among cards in your graveyard" and the mana
    /// value count asks the same shape about a different characteristic; a multi-word noun claims
    /// both of those sentences, then refuses them for a filter it cannot name — so widening this
    /// class without the guard cost six cards that had been read for months. A reader that claims
    /// a clause and returns null takes it away from the readers below it.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(there are ((?<none>no)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|fewer)) "
            + @"(?<what>(?!.*\bamong\b)[A-Za-z][A-Za-z/ ]*? )?cards in your graveyard"
            + @"|(?<who>an opponent|you) (has|have) "
            + @"(?<n2>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir2>more|fewer) "
            + @"(?<what2>(?!.*\bamong\b)[A-Za-z][A-Za-z/ ]*? )?cards in (their|your) graveyard)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraveyardCountLine();

    /// <summary>The card types a printed card can have, for counting them (CR 205.2a).</summary>
    private static readonly Domain.Enums.CardType[] CardTypesForDelirium =
    [
        Domain.Enums.CardType.Creature, Domain.Enums.CardType.Instant, Domain.Enums.CardType.Sorcery, Domain.Enums.CardType.Enchantment,
        Domain.Enums.CardType.Artifact, Domain.Enums.CardType.Land, Domain.Enums.CardType.Planeswalker, Domain.Enums.CardType.Tribal,
        Domain.Enums.CardType.Battle,
    ];

    /// <summary>The six types a permanent can have, for counting them (CR 110.4).</summary>
    /// <remarks>
    /// Battle is on this list and Tribal is not, which is the whole difference from the delirium
    /// list beside it: a kindred card is only ever a permanent by virtue of its other types, so
    /// counting it would be counting a type the card asking has excluded.
    /// </remarks>
    private static readonly Domain.Enums.CardType[] PermanentCardTypes =
    [
        Domain.Enums.CardType.Artifact, Domain.Enums.CardType.Battle,
        Domain.Enums.CardType.Creature, Domain.Enums.CardType.Enchantment,
        Domain.Enums.CardType.Land, Domain.Enums.CardType.Planeswalker,
    ];

    [GeneratedRegex(
        @"^there are (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|fewer) (?<kind>card|permanent) types among cards in your graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraveyardTypeCountLine();

    /// <summary>"Five or more mana values among cards in your graveyard" (CR 202.3).</summary>
    /// <remarks>
    /// Deliberately not shared with the pattern above. The two clauses differ by two words and
    /// mean different things, and a single pattern with the characteristic as a group would have
    /// to be trusted to keep counting the right one — the sort of near-identical pair that reads
    /// correctly and plays wrong.
    /// </remarks>
    [GeneratedRegex(
        @"^there are (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|fewer) (different )?mana values among cards in your graveyard$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraveyardManaValueCountLine();

    /// <remarks>
    /// The lead-in words are optional because the caller may have stripped them: "During your
    /// turn, ~ has first strike" is matched by a pattern that captures only the condition itself.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|an opponent|a player) ha(s|ve) "
            + @"((?<exactly>exactly) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|less|fewer)) life$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LifeLine();

    [GeneratedRegex(
        @"^(?<who>you have|an opponent has|(?<anyone>a player has)) no cards in hand$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmptyHandLine();

    /// <summary>"An opponent has more life than you" — two totals, not a threshold.</summary>
    /// <remarks>
    /// The two subjects are separate arms rather than one group with a comparison flipped
    /// afterwards, because the thing that differs between them is <em>which</em> total the
    /// quantifier is over: "an opponent has more life than you" asks each of them about my one
    /// total, and "you have more life than an opponent" asks my one total about each of them.
    /// Written as one arm, the direction and the quantifier would have to be flipped together
    /// and the pair of them is exactly what plays inverted when it drifts.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<theirs>an opponent) has (?<dir>more|less|fewer) life than you"
            + @"|you have (?<dir>more|less|fewer) life than (?<each>each opponent|an opponent))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LifeComparisonLine();

    /// <summary>"There are five or more Islands on the battlefield" — everybody's permanents.</summary>
    /// <remarks>
    /// The emptiness half of this sentence has been rewritten onto the ownership readers since
    /// they existed; the counted and singular halves had not, which is this file's own recurring
    /// shape — a condition with two ways to be satisfied and a reader for only one of them.
    /// <para>
    /// "No" is deliberately absent from the quantifier: <see cref="NoneOnBattlefieldLine"/> runs
    /// first and already reads it, and a second reader for it would be a second place for the
    /// negation to drift.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^there (?:is|are) ((?<a>an?)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more) "
            + @"(?<what>[A-Za-z][A-Za-z0-9 ]*) on the battlefield$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OnBattlefieldLine();

    /// <summary>The cards' "and/or", which the shared filter vocabulary spells "or" (CR 109.4).</summary>
    [GeneratedRegex(@"\s*\band/or\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Either();

    /// <remarks>
    /// "You control no creatures" is the same question asked backwards, so it shares the pattern
    /// rather than getting one of its own — and getting the negation wrong is the sort of thing
    /// that reads correctly and plays inverted.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you control|your opponents control|an opponent controls"
            + @"|another player controls"
            + @"|defending player controls|(?<anyone>an?y? ?player controls)) "
            + @"(an?|(?<none>no)|(?<another>another)) (?<what>[A-Za-z][A-Za-z0-9 ]*)$",
        RegexOptions.None)]
    private static partial Regex ControlsAnyLine();

    [GeneratedRegex(
        @"^~ is an? (?<what>[a-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceIsTypeLine();

    /// <remarks>
    /// The perfect tense is the same fact in different words - "an opponent <em>has been</em>
    /// dealt damage this turn" - and it is the whole of what an <c>unless</c> tail needed to
    /// become readable on one card. A tense is not a question, so it belongs in this pattern
    /// rather than in a reader of its own.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>an opponent|you) (was|were|has been|have been) dealt damage this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamagedThisTurnLine();

    [GeneratedRegex(
        @"^you control a creature with power "
            + @"(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>greater|more|less|fewer)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ControlsWithPowerLine();

    [GeneratedRegex(
        @"^((it's|it is|during) )?(?<not>not )?your turn$", RegexOptions.IgnoreCase)]
    private static partial Regex YourTurnLine();

    /// <remarks>
    /// "To a player" and not "to a creature": the two are different facts and only the first is
    /// recorded. A pattern admitting either would compile the wrong half of the corpus silently.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|an opponent) dealt combat damage to a player this turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DealtCombatDamageLine();

    [GeneratedRegex(
        @"^(?<who>you|an opponent) ha(ve|s) the city's blessing$", RegexOptions.IgnoreCase)]
    private static partial Regex CitysBlessingLine();

    /// <summary>"An opponent owns a card in exile" — a card in the shared zone, by owner.</summary>
    [GeneratedRegex(
        @"^(?<who>you|an opponent) owns? a card in exile$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnsExiledLine();

    /// <summary>"You have max speed" — speed 4 (CR 702.179e).</summary>
    /// <remarks>
    /// Only the controller's own speed is admitted, because that is the only side the corpus
    /// asks about: the cards naming somebody else's say "each player who doesn't have max speed",
    /// which is a group filter and belongs to the effect grammar rather than here.
    /// </remarks>
    [GeneratedRegex(@"^you have max speed$", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSpeedLine();

    [GeneratedRegex(
        @"^((?<who>you|an opponent) ?(are|'re|’re|is) the monarch"
            + @"|(?<nobody>there is no monarch))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonarchLine();

    /// <summary>"You have the initiative" (CR 726.1).</summary>
    [GeneratedRegex(@"^you have the initiative$", RegexOptions.IgnoreCase)]
    private static partial Regex InitiativeLine();

    /// <summary>"You've completed a dungeon" (CR 309.7).</summary>
    /// <remarks>
    /// Both spellings of the apostrophe, which this file has had to do everywhere: the corpus
    /// uses the typographic one and hand-written test fixtures use the typewriter one, and a
    /// pattern with only one of them reads half the cards that say the same thing.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:(?<opponent>an opponent) has|you('ve|’ve| have)) completed a dungeon$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CompletedDungeonLine();

    /// <summary>"You control your commander" (CR 903.3), or any commander (CR 903.3d).</summary>
    /// <remarks>
    /// The two determiners are kept apart rather than folded together. "Your commander" is the
    /// one card this player designated before the game; "a commander" is any permanent that is
    /// somebody's, which is a strictly wider question and the one the twenty-two cards printing
    /// it actually ask. A single reader answering both with the narrow question would refuse a
    /// free cast the card offers; answering both with the wide one would hand the lieutenant
    /// cycle its bonus off an opponent's commander.
    /// </remarks>
    [GeneratedRegex(
        @"^you control (your|(?<any>a)) commander$", RegexOptions.IgnoreCase)]
    private static partial Regex ControlsCommanderLine();

    /// <summary>"It's night", "it's neither day nor night" — the game's designation (CR 731.1).</summary>
    /// <remarks>
    /// "Neither" is spelled out in the pattern rather than reached by negating the other two,
    /// because it is a state of the field and not the absence of one: a game that has never had
    /// a designation is neither, and a game that has had one can never be neither again.
    /// </remarks>
    [GeneratedRegex(
        @"^it('s| is) (?<what>neither day nor night|day|night)$", RegexOptions.IgnoreCase)]
    private static partial Regex DayNightLine();

    /// <summary>"If you have a full party" (CR 700.8c).</summary>
    [GeneratedRegex(@"^you have a full party$", RegexOptions.IgnoreCase)]
    private static partial Regex FullPartyLine();

    /// <summary>The four creature types a party is made of (CR 700.8).</summary>
    private static readonly string[] PartyRoles = ["Cleric", "Rogue", "Warrior", "Wizard"];

    /// <summary>
    /// Whether this player controls a creature for each of the four party roles (CR 700.8c).
    /// </summary>
    /// <remarks>
    /// CR 700.8b is the whole of the difficulty: a creature that could fill two of the roles
    /// fills only one, and the count is taken the way that produces the highest result. So this
    /// is a matching and not a tally, and a greedy assignment is wrong exactly where it matters
    /// — a Cleric Rogue beside a plain Rogue is a full half of a party, and a reader that spent
    /// the Cleric Rogue on the Rogue slot would report neither role filled.
    /// <para>
    /// Types are computed rather than printed (CR 613 layer 4), because the cards that make a
    /// creature "a Cleric in addition to its other types" are printed alongside the ones asking
    /// this question.
    /// </para>
    /// </remarks>
    private static bool HasFullParty(GameState state, IAbilitySource abilities, Guid playerId)
    {
        var candidates = state.Battlefield
            .Select(state.GetObject)
            .Select(obj => Characteristics.Of(state, abilities, obj))
            .Where(now => now.IsCreature && now.ControllerId == playerId)
            .ToList();

        if (candidates.Count < PartyRoles.Length)
            return false;

        // Which creature each role has been given, as the search reassigns them.
        var filled = new int[PartyRoles.Length];
        Array.Fill(filled, -1);

        bool Fill(int role, bool[] tried)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (tried[i]
                    || !candidates[i].Subtypes.Contains(
                        PartyRoles[role], StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                tried[i] = true;

                // Free, or the role holding it can be re-housed somewhere else — which is the
                // step that makes this a maximum rather than a first-come assignment.
                var heldBy = Array.IndexOf(filled, i);
                if (heldBy < 0 || Fill(heldBy, tried))
                {
                    filled[role] = i;
                    return true;
                }
            }

            return false;
        }

        for (var role = 0; role < PartyRoles.Length; role++)
        {
            if (!Fill(role, new bool[candidates.Count]))
                return false;
        }

        return true;
    }

    [GeneratedRegex(
        @"^((?<none>no spells were cast)"
            + @"|a player cast (?<n>one|two|three|\d+) or more spells) last turn$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpellsLastTurnLine();

    [GeneratedRegex(
        @"^(?<what>[A-Za-z0-9'’ -]+?) have total power (?<n>\d+|one|two|three) or "
            + @"(?<dir>greater|more|less|fewer)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TotalPowerLine();

    [GeneratedRegex(
        @"^you cast (it|~|this spell) from your (?<zone>hand|graveyard|exile)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CastFromLine();

    /// <remarks>
    /// "In hand" and "in your hand" are the same phrase; the possessive is carried by the subject
    /// at the front, which is the only place that says whose hand it is.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<who>you|an opponent) (has|have) "
            + @"((?<none>no)"
            + @"|(?<exactly>exactly) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<under>fewer|more) than (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or (?<dir>more|fewer)"
            + @"|(?<a>an?)) cards? in (your |their )?hand$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HandCountLine();

    /// <summary>"Your devotion to white and black is less than seven" (CR 700.5).</summary>
    /// <remarks>
    /// Only your own devotion is read, because only your own is printed: no card in the corpus
    /// asks about an opponent's, and admitting a subject the cards never use would be a second
    /// branch nothing could ever exercise.
    /// </remarks>
    [GeneratedRegex(
        @"^your devotion to (?<c1>white|blue|black|red|green)"
            + @"( and (?<c2>white|blue|black|red|green))? is "
            + @"((?<less>less than) (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
            + @"|(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or "
            + @"(?<dir>greater|more))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DevotionLine();

    /// <summary>"If you have more cards in hand than each opponent" — a comparison, not a count.</summary>
    /// <remarks>
    /// Both directions are printed and each is five to seven corpus cards. They share a reader
    /// because they share a pair of counts; they do not share a quantifier, and the group is what
    /// keeps them apart.
    /// </remarks>
    [GeneratedRegex(
        @"^(you have more cards in hand than each opponent"
            + @"|(?<theirs>an opponent) has more cards in hand than you)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LargestHandLine();

    /// <summary>
    /// "Red is the most common color among all permanents or is tied for most common."
    /// </summary>
    /// <remarks>
    /// The "or is tied" tail is optional in the pattern and present on every card that prints
    /// the clause. It is read rather than ignored because the two readings differ on the board
    /// the cards are actually played on: a Djinn is itself a permanent of the colour it names,
    /// so the commonest way for its clause to be true is a tie.
    /// </remarks>
    [GeneratedRegex(
        @"^((?<colour>white|blue|black|red|green) is"
            + @"|(~|it|enchanted (creature|permanent)) shares a color with) "
            + @"the most common color among all permanents"
            + @"( or (is tied for most common|a color tied for most common))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MostCommonColourLine();

    /// <summary>"You control three or more lands with the same name."</summary>
    /// <remarks>
    /// The noun admits a comma so a phrase like "nonland, nontoken permanents" reaches the
    /// shared filter vocabulary whole; a noun that vocabulary cannot name leaves the clause
    /// unread rather than counting every permanent.
    /// </remarks>
    [GeneratedRegex(
        @"^you control (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten) or more "
            + @"(?<what>[a-z][a-z, ]*?) with (the same name( as one another)?"
            + @"|(?<different>different names))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SameNameLine();

    /// <summary>
    /// Which colours the most permanents on the battlefield are, or none at all (CR 105.2).
    /// </summary>
    /// <remarks>
    /// A permanent of two colours is counted towards each of them, because an object <em>is</em>
    /// every colour of its mana cost (CR 105.2) rather than being one thing that has to be
    /// picked. The colours come from the computed characteristics, since colour is layer 5
    /// (CR 613.1e) and a permanent something has turned black is black for this count.
    /// <para>
    /// A board with no coloured permanent on it has no most common colour, so the answer is
    /// empty rather than all five. That case is unreachable for the cards that ask: each of them
    /// is itself a permanent of the colour it names, and the clause only matters while it is on
    /// the battlefield.
    /// </para>
    /// </remarks>
    private static HashSet<Domain.Enums.ManaColor> MostCommonColours(
        GameState state, IAbilitySource abilities)
    {
        var tally = new Dictionary<Domain.Enums.ManaColor, int>();

        foreach (var id in state.Battlefield)
        {
            foreach (var colour in Characteristics.Of(state, abilities, state.GetObject(id)).Colors)
                tally[colour] = tally.GetValueOrDefault(colour) + 1;
        }

        if (tally.Count == 0)
            return [];

        var most = tally.Values.Max();

        return [.. tally.Where(pair => pair.Value == most).Select(pair => pair.Key)];
    }

    /// <summary>A small number written as a word, or as digits.</summary>
    private static int NumberWord(string word) => word.ToLowerInvariant() switch
    {
        "one" => 1,
        "two" => 2,
        "three" => 3,
        _ => int.TryParse(
            word,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) ? value : 1,
    };
}
