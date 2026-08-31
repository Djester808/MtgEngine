using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// The printed phrases that point back at what this resolution has already done (CR 608.2).
/// </summary>
/// <remarks>
/// "This way" is one mechanism written five ways — a count, an amount, a condition, a set, and a
/// member of that set — and the first three are the same question in three grammars: which of the
/// things this resolution just did answer to a noun and a participle. So the phrase is read once,
/// into a <see cref="TouchFilter"/>, and each grammar asks for it where it stands. A parser per
/// grammar would be the vocabulary restated three times, which is the mistake this codebase has
/// paid for four times over.
/// <para>
/// The set and the member are the same phrase again, in a position where the cards themselves are
/// the object of a verb rather than a number: "put a permanent card from among the cards milled
/// this way into your hand". They read through <see cref="Set"/>, which folds the "from among"
/// wording away and hands the rest to the same reader — the noun and the participle are printed
/// in two places on those cards and mean one thing.
/// </para>
/// <para>
/// <b>The verb list is the fail-closed guard, and it is short on purpose.</b> The engine defers
/// every question a player has to answer until after the resolution is over, so the events behind
/// "cards revealed this way", "cards you discarded this way" and "creatures sacrificed this way"
/// have not happened when a later effect of the same resolution runs. A reader that accepted them
/// would compile a card whose second half asks an empty record, answers nought, and does nothing —
/// for ever, silently, on a card that coverage counts as complete. They are refused here instead,
/// and the measurement of what that costs is in GAME_ENGINE_FEATURE.md.
/// </para>
/// <para>
/// <b>The same guard bites the set as an object, one step further on.</b> Which card to take is
/// itself a question, so the take happens at the settle after the resolution — which means a
/// sentence asking about <em>the take</em> is asking about an event that has not happened. Cache
/// Grab prints exactly that ("if you ... returned a Squirrel card to your hand this way") and is
/// left unread for it, while Sparring Dummy's second sentence asks about the mill instead and is
/// answerable. The difference is one word, and nothing downstream could tell the two apart.
/// </para>
/// </remarks>
internal static partial class ThisWay
{
    /// <summary>
    /// Which permanents an excess-damage clause was asked about (CR 120.4a).
    /// </summary>
    /// <remarks>
    /// Two arms and not one, because the corpus prints both and they are different questions: a
    /// planeswalker whose loyalty is overshot has been dealt excess damage and is not a creature.
    /// Bottle-Cap Blast and Vikya both aim at any target and differ in exactly this word.
    /// </remarks>
    internal enum ExcessScope
    {
        /// <summary>"Excess damage was dealt this way", "... to a permanent this way".</summary>
        AnyPermanent,

        /// <summary>"Excess damage was dealt to a creature this way".</summary>
        Creature,
    }

    /// <summary>
    /// "Excess damage was dealt to a creature this way" - the clause, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Read here beside the record's own condition rather than in the caller, because both are
    /// "this way" clauses and a caller that reached one would otherwise have to know which of the
    /// two vocabularies a phrase belongs to before asking either.
    /// <para>
    /// <b>A pronoun is refused.</b> "If excess damage was dealt to <em>that creature</em> this
    /// way" names one particular permanent, and the number this reader can answer with is about
    /// every permanent the effect hit. On a spell with one target the two agree and on a fight
    /// they do not, and the sentence gives nothing to tell those apart - so it is left unread,
    /// the same refusal the recorded-set condition makes one file along and for the same reason.
    /// </para>
    /// </remarks>
    internal static ExcessScope? Excess(string clause)
    {
        if (clause is null)
            return null;

        var m = ExcessClause().Match(Normalise(clause).Trim());
        if (!m.Success)
            return null;

        return m.Groups["what"].Value.Equals("a creature", StringComparison.OrdinalIgnoreCase)
            ? ExcessScope.Creature
            : ExcessScope.AnyPermanent;
    }

    [GeneratedRegex(
        @"^excess damage (was|is) dealt( to (?<what>a creature|a permanent))? this way$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExcessClause();

    /// <summary>Whether a phrase is one of these at all, before anything tries to read it.</summary>
    internal static bool Mentions(string phrase) =>
        phrase is not null
        && phrase.Contains("this way", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A counted group — "creature card exiled this way" — or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Null for anything at all it cannot read whole, including a phrase that plainly is one of
    /// these and names a verb the record does not carry. That is the point: the caller leaves the
    /// sentence unread, which is what a coverage number can see.
    /// </remarks>
    internal static TouchFilter? Counted(string phrase)
    {
        if (phrase is null)
            return null;

        var text = Normalise(phrase);
        if (!text.EndsWith(" this way", StringComparison.OrdinalIgnoreCase))
            return null;

        text = text[..^" this way".Length].Trim();

        // "Each"/"all"/"the" belong to the grammar around the phrase and not to what is counted;
        // the callers disagree about whether they strip them, exactly as they disagree for the
        // board-counting vocabulary next door.
        foreach (var article in new[] { "each ", "all ", "the ", "every " })
        {
            if (text.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                text = text[article.Length..].Trim();
        }

        return Read(text);
    }

    /// <summary>
    /// The same phrase where the cards are the object of a verb — "a permanent card from among
    /// the cards milled this way".
    /// </summary>
    /// <remarks>
    /// The noun and the participle are printed either side of an interposed phrase on half of
    /// this family and next to each other on the other half — "a land card milled this way"
    /// against "a permanent card from among the cards milled this way" — and they mean the same
    /// thing. So the interposition is folded away and the result goes to the reader every other
    /// grammar uses, rather than a second reader learning the same nouns.
    /// <para>
    /// Only "from among the cards &lt;participle&gt; this way" is folded. "From among them" names
    /// a set some earlier sentence looked at rather than one this resolution recorded, and is left
    /// alone so that the caller refuses the sentence.
    /// </para>
    /// </remarks>
    internal static TouchFilter? Set(string phrase) =>
        phrase is null ? null : Counted(FromAmong().Replace(Normalise(phrase), " "));

    /// <summary>
    /// A condition — "a creature card is exiled this way" — with how many it takes to satisfy it.
    /// </summary>
    /// <remarks>
    /// The threshold is part of the phrase rather than a fixed one, because "at least one card
    /// was milled" and "two or more cards were milled" are the same clause with a different
    /// number, and a reader that took only the first would file the second as understood while
    /// answering the wrong question.
    /// </remarks>
    internal static (TouchFilter Filter, int AtLeast)? Condition(string clause)
    {
        if (clause is null)
            return null;

        var text = Normalise(clause);
        if (!text.EndsWith(" this way", StringComparison.OrdinalIgnoreCase))
            return null;

        text = text[..^" this way".Length].Trim();

        // A clause about a relation between the things is not a clause about how many there are.
        // "Two cards that share a color were milled this way" needs the cards compared with each
        // other, which no filter expresses; reading it as "two cards were milled" would make the
        // card fire on a pair that shares nothing.
        if (RelationClause().IsMatch(text))
            return null;

        // "That spell", "that creature", "it" - a pronoun naming one particular object, which
        // this reader cannot tell from the set. Refused for the reason the "instead" rider
        // refuses one: a clause that is nearly the same question is a card that plays wrongly in
        // silence, and there is no test downstream that can see it.
        if (PronounSubject().IsMatch(text))
            return null;

        var passive = PassiveClause().Match(text);
        if (passive.Success)
        {
            var read = Read(passive.Groups["noun"].Value + " " + passive.Groups["verb"].Value);

            return read is null
                ? null
                : (read, Threshold(passive.Groups["many"].Value));
        }

        var active = ActiveClause().Match(text);
        if (!active.Success)
            return null;

        // "You exiled a land card this way" is the same fact with the actor in front. The past
        // tense is the participle for every verb the record carries except one, and "drew" is
        // spelled back into "drawn" rather than given an entry of its own in the verb table -
        // one table, asked twice, cannot drift from itself.
        var verb = active.Groups["verb"].Value;
        verb = verb.Equals("drew", StringComparison.OrdinalIgnoreCase) ? "drawn" : verb;

        var found = Read(active.Groups["noun"].Value + " " + verb);

        return found is null ? null : (found, Threshold(active.Groups["many"].Value));
    }

    /// <summary>How many of the thing the clause's opening words demand.</summary>
    private static int Threshold(string many) => many.Trim().ToLowerInvariant() switch
    {
        "two or more" => 2,
        "three or more" => 3,
        _ => 1,
    };

    /// <summary>Reads "&lt;noun&gt; &lt;participle&gt;" into a filter, or null.</summary>
    private static TouchFilter? Read(string phrase)
    {
        var text = Normalise(phrase);

        // "Exiled from their hand" - the zone the object came out of, printed between the
        // participle and "this way". Taken off before the participle is read, because the
        // participle table matches at the end of the phrase and the rider is standing there:
        // without this, "card exiled from their hand" reaches the table as a phrase ending in
        // "hand", no verb comes off it, and the whole line goes unread.
        Zone? from = null;
        if (FromZoneRider().Match(text) is { Success: true } origin
            && origin.Index + origin.Length == text.Length)
        {
            if (ZoneNamed(origin.Groups["zone"].Value) is not { } named)
                return null;

            from = named;
            text = text[..origin.Index].TrimEnd();
        }

        if (Verb(ref text) is not { } verb)
            return null;

        var yours = false;

        // "Creature you controlled that was destroyed", "permanent that was returned" - the
        // relative pronoun is grammar joining the noun to the participle, and the possessive in
        // front of it is the one word that narrows what is counted.
        foreach (var joiner in new[] { " that was", " that were", " that" })
        {
            if (text.EndsWith(joiner, StringComparison.OrdinalIgnoreCase))
                text = text[..^joiner.Length].TrimEnd();
        }

        if (text.EndsWith(" you controlled", StringComparison.OrdinalIgnoreCase))
        {
            yours = true;
            text = text[..^" you controlled".Length].TrimEnd();
        }

        // A possessive naming anybody else is a per-player count and this is not one: "creatures
        // they controlled that were destroyed this way" is a different number for each player
        // being asked, and answering it with one total would be wrong for all of them.
        if (OtherPossessive().IsMatch(text))
            return null;

        return Noun(text, verb, yours) is { } read ? read with { FromZone = from } : null;
    }

    /// <summary>
    /// Takes the participle off the end of a phrase and says which verb it was, or null.
    /// </summary>
    /// <remarks>
    /// Ordered longest first, because "returned" is the tail of "returned to its owner's hand"
    /// and the shorter entry would leave the destination behind as part of the noun.
    /// </remarks>
    private static TouchVerb? Verb(ref string phrase)
    {
        foreach (var (participle, verb) in Participles)
        {
            if (!phrase.EndsWith(participle, StringComparison.OrdinalIgnoreCase))
                continue;

            var head = phrase[..^participle.Length].TrimEnd();

            // A bare participle with no noun in front of it names nothing to count - "the damage
            // prevented this way" reaches here as "prevented" and would leave an empty filter,
            // which admits everything. Every participle in the table opens with a space, so a
            // phrase that is only the verb has nothing left once it comes off.
            if (head.Length == 0)
                return null;

            phrase = head;
            return verb;
        }

        return null;
    }

    /// <summary>
    /// The participles the record can answer, longest first (CR 608.2).
    /// </summary>
    /// <remarks>
    /// Deliberately absent, each because the events behind it do not exist when a later effect of
    /// the same resolution runs: revealed, discarded, sacrificed, chosen, tapped, cast, and every
    /// participle of prevention. See the class remarks.
    /// </remarks>
    private static readonly (string Participle, TouchVerb Verb)[] Participles =
    [
        (" put into a graveyard", TouchVerb.PutIntoGraveyard),
        (" put into your graveyard", TouchVerb.PutIntoGraveyard),
        (" put into their graveyard", TouchVerb.PutIntoGraveyard),
        (" put into its owner's graveyard", TouchVerb.PutIntoGraveyard),
        (" put into graveyards", TouchVerb.PutIntoGraveyard),
        (" put into their graveyards", TouchVerb.PutIntoGraveyard),
        (" put onto the battlefield", TouchVerb.PutOntoBattlefield),
        (" returned to its owner's hand", TouchVerb.ReturnedToHand),
        (" returned to their owner's hand", TouchVerb.ReturnedToHand),
        (" returned to their owners' hands", TouchVerb.ReturnedToHand),
        (" returned to your hand", TouchVerb.ReturnedToHand),
        (" returned", TouchVerb.ReturnedToHand),
        (" exiled", TouchVerb.Exiled),
        (" milled", TouchVerb.Milled),
        (" destroyed", TouchVerb.Destroyed),
        (" drawn", TouchVerb.Drawn),
        (" died", TouchVerb.Died),
    ];

    /// <summary>Reads the noun in front of the participle into the filter's nouns, or null.</summary>
    /// <remarks>
    /// The type words come from <c>EffectPhrase.TypesOfCardNoun</c>, which is the same two-table
    /// lookup the pile counter uses — a permanent table for the compounds and a graveyard table
    /// for the types no permanent can have. A noun it does not know leaves the phrase unread
    /// rather than counting everything: "for each Zombie card exiled this way" is not "for each
    /// card exiled this way", and a card that quietly counted the larger set would be strictly
    /// better than the one printed.
    /// </remarks>
    private static TouchFilter? Noun(string noun, TouchVerb verb, bool yours)
    {
        var filter = new TouchFilter(verb) { YoursOnly = yours };

        var text = noun.Trim();

        // "Nontoken creature", "nonland card", "noncreature card" - one negated type in front of
        // the noun, which the filter excludes rather than requires (CR 111.1, 205.2a).
        var excluded = ImmutableList.CreateBuilder<CardType>();

        while (NegatedType().Match(text) is { Success: true } negated)
        {
            if (TypeWord(negated.Groups["type"].Value) is not { } barred)
                return null;

            excluded.Add(barred);
            text = text[negated.Length..].TrimStart();
        }

        // The noun may be plural in every position that reaches here, and singular in every table
        // it is looked up in.
        text = Singular(text);

        // "Card" on its own names any card at all, and it is also the word every type noun is
        // written in front of - "creature card" is a creature. Stripping it once leaves the type
        // word, or leaves nothing, and nothing means anything.
        if (text.Equals("card", StringComparison.OrdinalIgnoreCase))
            text = string.Empty;
        else if (text.EndsWith(" card", StringComparison.OrdinalIgnoreCase))
            text = text[..^" card".Length].TrimEnd();

        if (text.Length == 0)
            return filter with { Excluded = excluded.ToImmutable() };

        // "A permanent card from among the cards milled this way". CR 110.4a lists six permanent
        // card types and the shared type table spells the word as an *empty* list of demands -
        // which admits every card there is, instants included, because nothing is being asked.
        // Read as the exclusion it actually is, through the negation the filter already has,
        // rather than as a seventh mask restated here beside the two the engine already keeps.
        // Only as the whole noun: inside an alternation the exclusion would silently narrow the
        // other alternatives too, so there it falls through and the phrase goes unread.
        if (text.Equals("permanent", StringComparison.OrdinalIgnoreCase))
        {
            excluded.Add(CardType.Instant);
            excluded.Add(CardType.Sorcery);
            return filter with { Excluded = excluded.ToImmutable() };
        }

        var alternatives = ImmutableList.CreateBuilder<TouchNoun>();

        // "Artifact or land card" is either, which is how the pile counter reads the same words.
        // Juxtaposition is the opposite - "artifact creature card" is both - and the type table
        // answers those compounds itself.
        foreach (var part in OrJoin().Split(text))
        {
            if (Alternative(part) is not { } alternative)
                return null;

            alternatives.Add(alternative);
        }

        return filter with
        {
            Nouns = alternatives.ToImmutable(),
            Excluded = excluded.ToImmutable(),
        };
    }

    /// <summary>
    /// One alternative of the noun — its card types and at most one subtype (CR 205.3).
    /// </summary>
    /// <remarks>
    /// A capitalised word is a subtype, which is how every other reader in this compiler tells
    /// one from an adjective. The type table is asked <em>first</em>, because a sentence may
    /// begin with the noun and put a capital on a word that is a card type: "Creature cards
    /// milled this way" read the other way round would demand the Creature subtype, which no card
    /// in the game has, and the card would quietly do nothing.
    /// <para>
    /// Which card type the subtype belongs to comes from the shared table rather than being
    /// assumed to be creature, for the reason written up beside that table: "Equipment" asked as
    /// a creature type matches nothing and reports itself understood.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Internal rather than private because it is not really about "this way" at all: it is the
    /// compiler's one reader for "what kind of card does this noun name", and the pile counter in
    /// <see cref="EffectPhrase"/> asks the same question about a graveyard. That counter had its
    /// own answer, built on the card-type table alone, and so read "creature cards in your
    /// graveyard" while refusing "Elf cards in your graveyard" — a gap that looked like a missing
    /// vocabulary and was a missing <em>distinction</em>, the same one written up above. Two
    /// readers for one question is what this codebase has paid for four times over, so there is
    /// one, here, and the count asks it.
    /// </remarks>
    internal static TouchNoun? Alternative(string part)
    {
        var word = Singular(part.Trim());

        if (word.EndsWith(" card", StringComparison.OrdinalIgnoreCase))
            word = word[..^" card".Length].TrimEnd();

        if (word.Length == 0)
            return null;

        string? subtype = null;
        var rest = new List<string>();

        foreach (var piece in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece.Length > 1
                && char.IsUpper(piece[0])
                && EffectPhrase.TypesOfCardNoun(Singular(piece).ToLowerInvariant()) is null)
            {
                // Two capitalised words is a noun this cannot read - "Zombie Wizard card" wants
                // both subtypes and one slot holds one. Refused rather than taking the first.
                if (subtype is not null)
                    return null;

                subtype = piece;
                continue;
            }

            rest.Add(piece.ToLowerInvariant());
        }

        if (subtype is null)
        {
            // The whole noun in one lookup, because the table answers compounds - "artifact
            // creature" is one entry and not two words to be intersected here.
            return EffectPhrase.TypesOfCardNoun(word.ToLowerInvariant()) is { Count: > 0 } types
                ? new TouchNoun { Types = [.. types] }
                : null;
        }

        if (rest.Count == 0)
            return new TouchNoun
            {
                Types = [EffectPhrase.SubtypeSetOf(subtype)],
                Subtype = subtype,
            };

        return EffectPhrase.TypesOfCardNoun(string.Join(' ', rest)) is { Count: > 0 } narrowed
            ? new TouchNoun { Types = [.. narrowed], Subtype = subtype }
            : null;
    }

    /// <summary>One card-type word, for the negated form in front of a noun.</summary>
    private static CardType? TypeWord(string word) =>
        EffectPhrase.TypesOfCardNoun(Singular(word.Trim()).ToLowerInvariant()) is [var only]
            ? only
            : null;

    /// <summary>The singular of a noun this reader might be handed.</summary>
    /// <remarks>
    /// Only the "-s" strip, because every noun that reaches here is a card type, a subtype or the
    /// word "card": there is no irregular-plural vocabulary in this phrase to need the table the
    /// target grammar keeps.
    /// </remarks>
    private static string Singular(string word) =>
        word.EndsWith('s') && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
            ? word[..^1]
            : word;

    /// <summary>
    /// The printed spacing collapsed, so every table reads alike.
    /// </summary>
    /// <remarks>
    /// Case is deliberately <em>kept</em>. It is the only thing on the page that separates a
    /// subtype from an adjective — "Desert" from "land", "Lesson" from "instant" — and the
    /// comparisons below all ask for it to be ignored where it does not matter. Lower-casing here
    /// was what made the subtypes unreadable, and it made them unreadable in a way that looked
    /// like a missing vocabulary rather than a missing distinction.
    /// </remarks>
    private static string Normalise(string phrase) =>
        RepeatedSpace().Replace(phrase.Replace('\u2019', '\'').Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedSpace();

    /// <summary>"... from among the cards milled this way" — the noun and its set, interposed.</summary>
    [GeneratedRegex(
        @"\s+from among (the |those )?cards?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromAmong();

    [GeneratedRegex(
        @"^non-?(?<type>[A-Za-z]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedType();

    [GeneratedRegex(@"\s+or\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrJoin();

    /// <summary>A possessive naming somebody other than the resolution's own controller.</summary>
    [GeneratedRegex(
        @"\b(they|that player|an opponent|each player|its owner|their owner) (controlled|owned)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherPossessive();

    /// <summary>A clause comparing the things with each other rather than counting them.</summary>
    [GeneratedRegex(
        @"\b(that share|sharing|with the (greatest|least|highest|lowest)|of the chosen"
            + @"|among (them|the cards)|named)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelationClause();

    /// <summary>A clause whose subject is one particular object the sentence already named.</summary>
    [GeneratedRegex(
        @"^(it|they|that|those|this|these)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PronounSubject();

    /// <summary>"A creature card is exiled", "at least one land card was milled".</summary>
    [GeneratedRegex(
        @"^(?<many>an?|at least one|one or more|two or more|three or more|another) "
            + @"(?<noun>[A-Za-z' -]+?) (is|are|was|were) (?<verb>[A-Za-z' ]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassiveClause();

    /// <summary>"You exiled a land card", the same fact with the actor in front.</summary>
    [GeneratedRegex(
        @"^you (?<verb>exiled|milled|destroyed|returned|drew) "
            + @"(?<many>an?|at least one|one or more|two or more|three or more) "
            + @"(?<noun>[A-Za-z' -]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveClause();

    /// <summary>
    /// The zone a phrase says the objects came out of - "exiled from their hand this way".
    /// </summary>
    /// <remarks>
    /// Nine cards in the corpus print one, all of them extractions, and all but two of them
    /// spell it "from their hand"; the other two say "from your hand". The possessive is matched
    /// and dropped rather than read, because it is not a second question: the effect that did
    /// the moving reached one player's zones, so the zone alone already names the pile. A
    /// possessive read as a filter would need a per-player count, which is the thing
    /// <see cref="OtherPossessive"/> refuses one grammar along.
    /// <para>
    /// The three zones a search reaches (CR 701.23a) and no others. A rider naming somewhere the
    /// record cannot have taken anything from would be a phrase this could answer only with
    /// nought, which is the answer this whole file exists to refuse.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"\s+from (their|your|that player's|its owner's) (?<zone>hand|graveyard|library)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromZoneRider();

    /// <summary>
    /// Whether a whole sentence counts something out of a zone rather than out of everything the
    /// effect touched.
    /// </summary>
    /// <remarks>
    /// The same words as the rider and the same zone table, asked of the sentence rather than of
    /// the phrase inside it, because the caller that has to know is the one holding the sentence:
    /// a count scoped to one zone is the only one a deferred search can answer, and the compiler
    /// decides whether to defer before the phrase has been picked out of the line. One regex and
    /// one table asked from two positions, so a word either of them learns is learned by both.
    /// </remarks>
    internal static bool NamesASourceZone(string sentence)
    {
        if (sentence is null || !Mentions(sentence))
            return false;

        var m = FromZoneRider().Match(Normalise(sentence));

        return m.Success && ZoneNamed(m.Groups["zone"].Value) is not null;
    }

    /// <summary>The zone one of those words names, or null.</summary>
    private static Zone? ZoneNamed(string word) => word.ToLowerInvariant() switch
    {
        "hand" => Zone.Hand,
        "graveyard" => Zone.Graveyard,
        "library" => Zone.Library,
        _ => null,
    };
}
