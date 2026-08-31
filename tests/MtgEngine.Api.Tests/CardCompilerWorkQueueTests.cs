using System.Text.RegularExpressions;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The work queue, split by <em>which part</em> of a line the compiler could not read.
/// </summary>
/// <remarks>
/// <see cref="CardCompilerCoverageTests"/> ranks whole unread lines, which was the right list
/// while whole templates were missing and is the wrong one now: the head has gone flat — no
/// single line shape blocks more than about thirty cards — while seventeen thousand cards sit one
/// line away from complete. A flat head does not mean the work is finished, it means the leverage
/// moved somewhere a whole-line ranking cannot see.
/// <para>
/// Where it moved is the shared vocabulary. "When ~ enters, draw a card" and "{T}: Draw a card"
/// are two unread lines and one missing sentence, because <see cref="EffectPhrase"/> is what both
/// bottom out in. So this splits each unread line into the parts the compiler reads separately —
/// trigger condition, activation cost, effect sentence — and ranks those instead. A sentence at
/// the top of that list is worth its count across every kind of ability at once.
/// </para>
/// <para>
/// It asserts nothing. A diagnostic that fails is a diagnostic people delete; the ratchet in the
/// coverage test is what holds the line, and this is what says where to push.
/// </para>
/// </remarks>
public sealed partial class CardCompilerWorkQueueTests(ITestOutputHelper output)
{
    /// <summary>
    /// The cards the coverage number is wrong about: read in full, and inert.
    /// </summary>
    /// <remarks>
    /// Coverage asks whether every line was recognised. This asks the question it does not: did
    /// recognising them produce anything a game could run? A card whose text compiles to no
    /// spell, no trigger, no static and no activated ability is counted as covered and does
    /// nothing on the board - which is a worse failure than an unread line, because an unread
    /// line is visible.
    /// <para>
    /// Reported rather than asserted. Some of these are correct: a card whose only text is
    /// "Flying" compiles to nothing because the keyword was already read off the printed card,
    /// and reminder text and ability words are meant to produce nothing. The number is a place
    /// to look, not a bug count, which is why the report is grouped by what the text actually
    /// says.
    /// </para>
    /// </remarks>
    [Fact]
    public void Cards_that_were_read_in_full_and_still_do_nothing()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var inert = new Counter();
        var emptySpells = new Counter();
        var emptyAbilities = new Counter();
        var total = 0;

        foreach (var card in corpus)
        {
            if (string.IsNullOrWhiteSpace(card.OracleText))
                continue;

            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            total++;

            if (!compiled.HasAbilities && SaysSomething(card.OracleText))
            {
                inert.Add(FirstLine(card.OracleText), $"{card.Name}: {card.OracleText}");
                continue;
            }

            // An ability that goes on the stack, resolves, and does nothing - the same defect as
            // an empty spell, one level down, and the one an "unhandled line" count cannot see
            // because every line was read.
            foreach (var trigger in compiled.Triggers.Where(t => t.Effects.IsEmpty))
                emptyAbilities.Add($"trigger: {trigger.Text}", card.Name);

            foreach (var mode in compiled.Spell?.Modes ?? [])
            {
                if (mode.Effects.IsEmpty)
                    emptyAbilities.Add($"mode: {mode.Text}", card.Name);
            }

            // A spell that resolves and does nothing. Modes count as doing something, since the
            // effects live on them rather than on the spell.
            if (compiled.Spell is { } spell && spell.Effects.IsEmpty && spell.Modes.IsEmpty
                && card.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Instant)
                    | card.CardTypes.HasFlag(MtgEngine.Domain.Enums.CardType.Sorcery))
            {
                emptySpells.Add(FirstLine(card.OracleText), $"{card.Name}: {card.OracleText}");
            }
        }

        output.WriteLine($"{total} cards read in full, of which:");
        Report("read in full and compiled to no ability at all", inert);
        Report("instants and sorceries that resolve and do nothing", emptySpells);
        Report("abilities and modes that resolve and do nothing", emptyAbilities);
    }

    /// <summary>
    /// Whether the text says anything beyond the keywords already printed on the card.
    /// </summary>
    /// <remarks>
    /// Without this the report is 826 cards and 800 of them are "Flying": a printed keyword is
    /// read off the card's own keyword field, so the compiler emitting nothing for it is right.
    /// Reminder text goes the same way - it is in brackets precisely because it adds nothing.
    /// </remarks>
    private static bool SaysSomething(string text)
    {
        foreach (var line in Reminder().Replace(text, string.Empty).Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd('.');
            if (trimmed.Length == 0)
                continue;

            // A keyword line is a short comma-separated list of single words - or of "protection
            // from red", which is a keyword with an argument and the commonest thing in this
            // report that did not belong in it. Anything with a verb in it is longer.
            if (trimmed.Split(',').All(word =>
                word.Trim().Split(' ').Length <= 2
                || word.Trim().StartsWith("protection from", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Reminder();

    /// <summary>The card's first line, capped, so cards of one family group together.</summary>
    private static string FirstLine(string text)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length > 90 ? first[..90] : first;
    }

    /// <summary>
    /// The Alchemy and Un-set mechanics, each measured three ways rather than named once.
    /// </summary>
    /// <remarks>
    /// This exists because the figures behind five separate declines went four rounds without
    /// being re-measured and three of them turned out to be wrong. "62 spellbook cards with no
    /// data field", "19 specialize cards absent from the corpus", "46 Attractions missing
    /// <c>attraction_lights</c>", "48 sticker sheets excluded as not-cards": of those, the
    /// specialize cards are in the corpus, every Attraction in the dump carries its lights, and
    /// only the spellbook and the sticker numbers held. A decline is a measurement and a
    /// measurement nobody repeats is a rumour.
    /// <para>
    /// <strong>The excision column is an upper bound and the substitution column is the real
    /// one.</strong> Excision deletes the family's lines and asks how many cards would then be
    /// complete - which is what everybody quotes, and it answers a question nobody asked:
    /// what if those cards did not say this. Substitution takes only the <em>mechanic word</em>
    /// out and leaves the sentence, which is what modelling the mechanic would actually buy. On
    /// perpetual the two are 193 and 4. Ranking this family by its excision number recommends
    /// fifty times the work it can pay for.
    /// </para>
    /// <para>
    /// Reported rather than asserted, like everything else in this file.
    /// </para>
    /// </remarks>
    [Fact]
    public void What_the_alchemy_and_un_set_families_would_actually_be_worth()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        // Compiled once and shared: a pass over the corpus per family is eight passes, and the
        // answer is the same one every time.
        var compiled = corpus
            .Select(card => (Card: card, Result: CardCompiler.Compile(card)))
            .ToList();

        output.WriteLine($"corpus {corpus.Count}, complete {compiled.Count(c => c.Result.IsComplete)}");
        output.WriteLine(string.Empty);
        output.WriteLine("family          cards  lines  distinct  excised  substituted");

        foreach (var (family, word, substitute) in Families)
        {
            var carriers = compiled
                .Where(c => !c.Result.IsComplete
                    && c.Result.Unhandled.Any(l => l.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var lines = 0;
            var shapes = new HashSet<string>(StringComparer.Ordinal);
            var excised = 0;
            var substituted = 0;

            foreach (var (card, result) in carriers)
            {
                var mine = result.Unhandled
                    .Where(l => l.Contains(word, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                lines += mine.Count;
                foreach (var line in mine)
                    shapes.Add(line);

                var whole = CardCompiler.Lines(card).ToList();

                var cut = string.Join("\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
                if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                    excised++;

                var swapped = string.Join(
                    "\n",
                    whole.Select(l => mine.Contains(l, StringComparer.Ordinal) ? substitute(l) : l));
                if (CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                    substituted++;
            }

            output.WriteLine(
                $"{family,-14} {carriers.Count,5}  {lines,5}  {shapes.Count,8}  {excised,7}  {substituted,11}");
        }
    }

    /// <summary>
    /// Each family, and how to say its sentence without its mechanic word.
    /// </summary>
    /// <remarks>
    /// The substitutions are deliberately the plainest printed wording that means nearly the
    /// same thing, because a clever one would be measuring the substitution rather than the
    /// grammar. Where a family has no plainer wording at all - a sticker, an Attraction - the
    /// line is left as it stands and the column reads zero, which is itself the answer: there is
    /// no ordinary sentence underneath.
    /// </remarks>
    private static readonly (string Family, string Word, Func<string, string> Without)[] Families =
    [
        // The nearest printed wording for "conjure a card named X into your hand" is a tutor,
        // which is the fairest substitute available: it asks whether the rest of the sentence -
        // the trigger, the condition, the clause after the comma - is already read.
        ("conjure", "conjure", l => Regex.Replace(
            l,
            "[Cc]onjures? (a|an|two|three|four|X) cards? named [A-Z][^,.]*",
            "search your library for a card")),
        ("perpetual", "perpetual", WithoutPerpetually),

        // The Alchemy card-list mechanics have no plainer wording, so the substitute is the
        // plainest *instruction* - a draw - left inside the sentence that carried them. That is
        // the distinction the whole table exists for: if the trigger, the cost and the clause
        // after the comma are already read, the card is waiting on a reader; if they are not,
        // the mechanic was never what blocked it.
        ("spellbook", "spellbook", l => Regex.Replace(
            l,
            "([Dd]raft|[Cc]onjure)s? (a |an |each )?(random |of your choice )?cards? "
                + "(of your choice )?from ([^,.]*?)[Ss]pellbook",
            "draw a card")),

        // A keyword ability with a cost, rewritten to an activated ability with the same cost -
        // so what is measured is whether the rest of the printed line ("Activate only if you
        // control six or more lands") reads, which is all that could be left.
        ("specialize", "specialize", l => Regex.Replace(
            l, @"Specialize ((?:\{[^}]+\})+)", "$1: Draw a card")),

        ("attraction", "attraction", l => l
            .Replace("Open an Attraction", "Draw a card", StringComparison.Ordinal)
            .Replace("open an Attraction", "draw a card", StringComparison.Ordinal)
            .Replace("Open two Attractions", "Draw two cards", StringComparison.Ordinal)
            .Replace("open two Attractions", "draw two cards", StringComparison.Ordinal)
            .Replace("Roll to visit your Attractions", "Draw a card", StringComparison.Ordinal)
            .Replace("roll to visit your Attractions", "draw a card", StringComparison.Ordinal)),

        ("sticker", "sticker", l => Regex.Replace(
            l,
            "([Yy]ou may )?put (a|an|up to two|up to one) (name )?stickers? on [^,.]*",
            m => (m.Groups[1].Success ? "you may draw a card" : "draw a card"))),

        ("seek", "seek ", l => Regex.Replace(
            l, "[Ss]eeks? (a|an|two|three) ", "search your library for $1 ")),
        // A keyword line rewritten to a keyword that reads, for the same reason specialize is.
        ("double team", "double team", l => l
            .Replace("Double team", "Flying", StringComparison.Ordinal)
            .Replace("double team", "flying", StringComparison.Ordinal)),
    ];

    /// <summary>"Perpetually X" is "X" with a duration Alchemy invented (CR 614 is not it).</summary>
    private static string WithoutPerpetually(string line)
    {
        var without = line;
        foreach (var verb in PerpetualVerbs)
            without = without.Replace("perpetually " + verb, verb, StringComparison.Ordinal);

        return without
            .Replace("Perpetually ", string.Empty, StringComparison.Ordinal)
            .Replace("perpetually ", string.Empty, StringComparison.Ordinal);
    }

    private static readonly string[] PerpetualVerbs =
    [
        "gets", "get", "gains", "gain", "becomes", "become",
        "has", "have", "loses", "lose",
    ];

    /// <summary>
    /// An effect that changes a card in a hand, a library or a graveyard, measured three ways.
    /// </summary>
    /// <remarks>
    /// The perpetual row was ranked at 193 by excision and 4 by substitution, and the reading
    /// offered for the gap was that its 241 remaining lines want a grammar for <em>cards in
    /// non-battlefield zones</em>. That reading is a claim about a shape rather than a word, so
    /// it is measured as a shape: every unread line whose subject is "&lt;filter&gt; card(s) in
    /// &lt;somebody's&gt; hand/library/graveyard" and whose verb is a continuous one — gets,
    /// gains, has, have, becomes, costs.
    /// <para>
    /// <b>Excision</b> cuts those lines, as every family measurement here does. <b>Substitution</b>
    /// is the one that answers the question: it moves the same sentence's subject onto the
    /// battlefield ("creature cards in your graveyard get +1/+1" → "creatures you control get
    /// +1/+1") and leaves every other word standing, so what it counts is the cards for which the
    /// zone was the <em>only</em> thing missing. The <b>line control</b> drops a different unread
    /// line on a carrier that has one, which is the control a naive "drop any line anywhere" is
    /// not: it asks whether these particular cards are one line short of anything at all.
    /// </para>
    /// <para>
    /// Reported rather than asserted, like everything else in this file.
    /// </para>
    /// </remarks>
    [Fact]
    public void What_an_effect_on_a_card_outside_the_battlefield_would_be_worth()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var compiled = corpus
            .Select(card => (Card: card, Result: CardCompiler.Compile(card)))
            .ToList();

        var carriers = compiled
            .Where(c => !c.Result.IsComplete && c.Result.Unhandled.Any(IsZoneScoped))
            .ToList();

        var lines = carriers.Sum(c => c.Result.Unhandled.Count(IsZoneScoped));
        var printed = compiled.Count(c => CardCompiler.Lines(c.Card).Any(IsZoneScoped));
        var alreadyComplete = compiled.Count(
            c => c.Result.IsComplete && CardCompiler.Lines(c.Card).Any(IsZoneScoped));

        var excised = 0;
        var substituted = new List<string>();
        var controlPool = 0;
        var controlWon = 0;

        foreach (var (card, result) in carriers)
        {
            var mine = result.Unhandled.Where(IsZoneScoped).ToList();
            var whole = CardCompiler.Lines(card).ToList();

            var cut = string.Join("\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
            if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                excised++;

            var swapped = string.Join(
                "\n",
                whole.Select(l => mine.Contains(l, StringComparer.Ordinal) ? OntoTheBattlefield(l) : l));
            if (CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                substituted.Add(card.Name);

            // The line control has to drop a *different* unread line on a card in this same
            // family. Dropping any line from any card completes 1,340 corpus-wide and measures
            // nothing about this shape.
            var others = result.Unhandled
                .Where(l => !mine.Contains(l, StringComparer.Ordinal))
                .ToList();

            if (others.Count == 0)
                continue;

            controlPool++;
            foreach (var other in others)
            {
                var dropped = string.Join(
                    "\n", whole.Where(l => !string.Equals(l, other, StringComparison.Ordinal)));

                if (CardCompiler.Compile(Rewritten(card, dropped)).IsComplete)
                {
                    controlWon++;
                    break;
                }
            }
        }

        output.WriteLine($"corpus {corpus.Count}, complete {compiled.Count(c => c.Result.IsComplete)}");
        output.WriteLine(
            $"the shape is printed on {printed} cards, of which {alreadyComplete} already compile "
                + $"whole; {carriers.Count} carriers, {lines} unread lines");
        output.WriteLine($"EXCISED      {excised}");
        output.WriteLine($"SUBSTITUTED  {substituted.Count}  [{string.Join(", ", substituted)}]");
        output.WriteLine($"LINE CONTROL {controlWon} of {controlPool} carriers with another unread line");
    }

    /// <summary>The subject of the sentence is a card in a hand, a library or a graveyard.</summary>
    [GeneratedRegex(
        @"(?<lead>[A-Za-z' ]{0,24}?)(?<subject>\b(?:[A-Za-z'\-]+ ){0,6}cards?)"
        + @"(?: (?:in|from) (?:your|their|a|each|all|an opponent's|any|its owner's"
        + @"|that player's|each player's)(?: [A-Za-z']+){0,2}? "
        + @"(?<zone>hand|graveyard|library)s?)"
        + @"(?<rest> (?:perpetually )?[A-Za-z']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ZoneScopedSubject();

    /// <summary>...and the verb after it changes that card rather than counting it.</summary>
    /// <remarks>
    /// Without this the match is 780 lines and most of them are "the number of cards in your
    /// graveyard" — a count, which the compiler reads perfectly well and which has nothing to do
    /// with an effect reaching into a zone.
    /// </remarks>
    [GeneratedRegex(
        @"^ (?:perpetually )?(?:gets?|gains?|have|has|lose|loses|becomes?|are|is|costs?|can't|may)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContinuousVerb();

    /// <summary>A question about the zone rather than an effect on it — "if a card ... has".</summary>
    [GeneratedRegex(
        @"(number|amount) of $|\bif $|\bif an? $|\bif the $|\bwhenever $|\bwhenever an? $",
        RegexOptions.IgnoreCase)]
    private static partial Regex AskingRatherThanGranting();

    private static bool IsZoneScoped(string line) => ZoneScopedSubject().Matches(line).Any(Grants);

    private static bool Grants(Match m) =>
        ContinuousVerb().IsMatch(m.Groups["rest"].Value)
        && !AskingRatherThanGranting().IsMatch(m.Groups["lead"].Value);

    /// <summary>The same sentence with its subject standing on the battlefield instead.</summary>
    /// <remarks>
    /// Mechanical on purpose: strip the word "card", pluralise if the subject was plural, and add
    /// "you control". A cleverer rewrite would be measuring the rewrite. Where it produces a
    /// phrase no card prints — "target instant or sorcery you control" — the substitution fails
    /// for a reason that is not the zone, and those lines are named in the report so the number
    /// can be read with that in mind.
    /// </remarks>
    private static string OntoTheBattlefield(string line) =>
        ZoneScopedSubject().Replace(line, m =>
        {
            if (!Grants(m))
                return m.Value;

            var subject = m.Groups["subject"].Value;
            var plural = subject.EndsWith("cards", StringComparison.OrdinalIgnoreCase);
            var head = subject[..^(plural ? 6 : 5)].TrimEnd();

            if (head.Length == 0)
                head = "permanent";

            if (head.EndsWith("nonland", StringComparison.OrdinalIgnoreCase))
                head += " permanent";

            if (plural)
            {
                var words = head.Split(' ');
                var last = words[^1];
                words[^1] = last.EndsWith('s') ? last
                    : last.EndsWith('y') ? last[..^1] + "ies"
                    : last + "s";
                head = string.Join(' ', words);
            }

            return m.Groups["lead"].Value + head + " you control" + m.Groups["rest"].Value;
        });

    /// <summary>The same card with different words on it, for a control run.</summary>
    private static Domain.Models.CardDefinition Rewritten(
        Domain.Models.CardDefinition card, string text) => new()
        {
            // A control card is a card of its own, or the compiled pool would be asked to serve
            // one card's behaviour under another's id.
            OracleId = card.OracleId + "#control-" + text.GetHashCode(StringComparison.Ordinal),
            Name = card.Name,
            OracleText = text,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            Keywords = card.Keywords,
            Colors = card.Colors,
            ColorIdentity = card.ColorIdentity,
            Power = card.Power,
            Toughness = card.Toughness,
            Defense = card.Defense,

            // A control card is the same card with different words, so it keeps every printed
            // characteristic that is not words - the lights included. Dropping them would have
            // measured the attraction family against a compiler that refuses a visit ability
            // whose lights are missing, which is a refusal about the control and not about the
            // card.
            AttractionLights = card.AttractionLights,
            Faces = card.Faces,
        };

    [Fact]
    public void The_work_queue_split_by_the_part_that_could_not_be_read()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var effectSentences = new Counter();
        var triggerConditions = new Counter();
        var activationCosts = new Counter();
        var keywordish = new Counter();
        var otherLines = new Counter();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);

            // Only the cards one line short. A card with five unread lines is not going to be
            // completed by any one template, so counting its lines here inflates every shape it
            // happens to contain and buries the ones that would actually finish a card.
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            Classify(
                compiled.Unhandled[0],
                effectSentences,
                triggerConditions,
                activationCosts,
                keywordish,
                otherLines);
        }

        ReportOneLineShort(corpus);
        ReportPairs(corpus);

        Report("effect sentences — each counts across spells, activated and triggered alike", effectSentences);
        Report("trigger conditions — the 'When ...' half", triggerConditions);
        Report("activation costs — the 'X:' half", activationCosts);
        Report("keyword-shaped lines — a word, or a word and a cost", keywordish);
        Report("everything else — statics, and sentences of no recognised shape", otherLines);
    }

    /// <summary>
    /// The whole line blocking each card that is exactly one line short.
    /// </summary>
    /// <remarks>
    /// The ranking to act on, and the only one here that means literally what it says: fix this
    /// shape and that many cards finish. The per-sentence rankings below are a diagnostic for
    /// <em>why</em> a line fails, and they systematically overstate — they feed each sentence to
    /// the parser alone, so a sentence that is only legal in company reports as a blocker. "Then
    /// shuffle" is the clearest case: legal after a search, meaningless before one, and it sat at
    /// the head of the sentence ranking on 234 cards while blocking none of them on its own.
    /// </remarks>
    private void ReportOneLineShort(
        IReadOnlyList<MtgEngine.Domain.Models.CardDefinition> corpus)
    {
        var lines = new Counter();
        var families = new Counter();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];
            lines.Add(Shape(line));

            // A keyword prints as its name and a number or a cost, so the cost is what makes
            // "Morph {2}{U}" and "Morph {4}" look like two problems when they are one. Grouping
            // by the name is what turns a ranking of shapes into a ranking of work.
            if (KeywordShape().IsMatch(line))
                families.Add(KeywordName(line));
        }

        var ranked = lines.Ranked().ToList();
        output.WriteLine("---- the single line blocking a card one line short — fix it and the card finishes");
        output.WriteLine($"     {ranked.Sum(p => p.Value)} cards, across {ranked.Count} distinct lines");
        foreach (var (shape, count) in ranked.Take(30))
            output.WriteLine($"  {count,6}  {shape}");
        output.WriteLine(string.Empty);

        // The same lines clustered by how they open. The ranking above went flat once the big
        // families were read — everything left is six or seven cards — and a flat list of
        // sentences hides that thirty of them begin the same way and would fall to one matcher.
        // This is the view that says where a family still is.
        var openings = new Counter();
        foreach (var (shape, count) in ranked)
        {
            var words = shape.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4)
                openings.Add(string.Join(' ', words.Take(4)), count);
        }

        var byOpening = openings.Ranked().ToList();
        output.WriteLine("---- those same lines, clustered by their first four words");
        foreach (var (shape, count) in byOpening.Take(25))
            output.WriteLine($"  {count,6}  {shape}");
        output.WriteLine(string.Empty);

        // And inside the biggest clusters, the lines themselves. A cluster is a place to look,
        // not a thing to fix; this is the step between the two, and doing it by hand each time
        // was the slowest part of using this list.
        output.WriteLine("---- inside the biggest clusters");
        foreach (var (opening, _) in byOpening.Take(8))
        {
            output.WriteLine($"  {opening}...");

            var members = ranked
                .Where(p => p.Key.StartsWith(opening, StringComparison.Ordinal))
                .Take(8);

            foreach (var (shape, count) in members)
                output.WriteLine($"     {count,4}  {shape}");
        }

        output.WriteLine(string.Empty);

        var sentences = new Counter();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.Unhandled.Count != 1)
                continue;

            // The sentences of the one line standing between this card and being playable. This
            // is the only ranking that predicts card gains: the corpus-wide sentence counts say
            // how often a phrase appears, and a phrase on a card blocked three other ways is
            // worth nothing. Several families measured 150+ across the corpus and moved the card
            // count by one, which is what this exists to stop.
            // Only the lines with exactly one unreadable sentence. A line with two of them is
            // not fixed by fixing either, so counting both puts work at the head of the list that
            // would finish nothing — which is what every earlier version of this ranking did.
            // Polymorph led it with "It can't be regenerated" while actually being blocked by the
            // sentence after that one.
            var failing = EffectPhrase.SentencesOf(compiled.Unhandled[0])
                .Select(Normalise)
                .Where(one => one.Length > 0 && !NotABlocker().IsMatch(one))
                .Where(one => !EffectPhrase.TryParse(one, out _))
                .ToList();

            if (failing.Count == 1)
                sentences.Add(Shape(failing[0]), $"{card.Name}: {compiled.Unhandled[0]}");
        }

        var blocking = sentences.Ranked().ToList();
        output.WriteLine("---- sentences inside the line blocking a card one line short");
        output.WriteLine($"     {blocking.Sum(p => p.Value)} sentences, across {blocking.Count} shapes");
        foreach (var (shape, count) in blocking.Take(20))
        {
            output.WriteLine($"  {count,6}  {shape}");

            if (sentences.ExampleOf(shape) is { } example)
                output.WriteLine($"          e.g. {example[..Math.Min(150, example.Length)]}");
        }
        output.WriteLine(string.Empty);

        var byFamily = families.Ranked().ToList();
        output.WriteLine("---- of those, the keyword abilities, by name rather than by printed cost");
        output.WriteLine($"     {byFamily.Sum(p => p.Value)} cards, across {byFamily.Count} keywords");
        foreach (var (name, count) in byFamily.Take(40))
            output.WriteLine($"  {count,6}  {name}");
        output.WriteLine(string.Empty);
    }

    /// <summary>
    /// The commonest <em>pairs</em> of lines blocking a card, for the cards two lines short.
    /// </summary>
    /// <remarks>
    /// The one-line-short ranking answers "what would finish a card on its own". This answers a
    /// question that turns out to matter more: which two shapes keep turning up together. Several
    /// families this session moved the line count and not the card count — kicker, Equipment —
    /// because the cards using them were blocked by something else as well, and a ranking of
    /// single lines cannot show that. A pair that recurs is two fixes that pay together and
    /// neither of which pays alone.
    /// </remarks>
    private void ReportPairs(IReadOnlyList<MtgEngine.Domain.Models.CardDefinition> corpus)
    {
        var pairs = new Counter();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.Unhandled.Count != 2)
                continue;

            // Sorted, so the same two shapes count together whichever order the card prints them.
            var shapes = compiled.Unhandled.Select(Shape).OrderBy(x => x, StringComparer.Ordinal);
            pairs.Add(string.Join("  +  ", shapes));
        }

        var ranked = pairs.Ranked().ToList();
        output.WriteLine("---- pairs blocking a card two lines short — two fixes that pay together");
        output.WriteLine($"     {ranked.Sum(p => p.Value)} cards, across {ranked.Count} distinct pairs");
        foreach (var (shape, count) in ranked.Take(15))
            output.WriteLine($"  {count,6}  {shape}");
        output.WriteLine(string.Empty);
    }

    /// <summary>
    /// Splits one unread line into the piece that actually defeated the compiler.
    /// </summary>
    /// <remarks>
    /// Deliberately a rough second opinion rather than a call into the compiler's own matchers.
    /// Those are private and should stay that way — a diagnostic that needs the parser opened up
    /// starts dictating the parser's shape. Approximating the split here costs nothing, because
    /// being wrong about a line only mis-files it in a ranking.
    /// </remarks>
    private static void Classify(
        string line,
        Counter effects,
        Counter conditions,
        Counter costs,
        Counter keywords,
        Counter other)
    {
        var trigger = TriggerShape().Match(line);
        if (trigger.Success)
        {
            var condition = Shape(trigger.Groups["when"].Value);
            var effect = trigger.Groups["effect"].Value;

            // Whichever half the compiler cannot read is the one worth counting. When both fail,
            // the condition is the blocker — an effect behind an unreadable trigger is unreachable
            // however well it parses.
            if (TriggerConditions.Parse(Normalise(trigger.Groups["when"].Value)) is null)
                conditions.Add(condition);
            else
                CountSentences(effect, effects, line);

            return;
        }

        var activated = ActivatedShape().Match(line);
        if (activated.Success)
        {
            // The compiler lifts "Sacrifice ~" out of a cost before checking the rest, so a cost
            // containing it is not a blocker. Mirroring that here matters: without it, "{1},
            // Sacrifice ~" sat at the top of the cost queue on 148 cards whose actual blocker was
            // the effect — and a ranking that points at the wrong half is worse than none.
            var cost = SacrificeSelf().Replace(activated.Groups["cost"].Value, string.Empty)
                .Trim().Trim(',').Trim();

            if (!PayableCost().IsMatch(cost))
                costs.Add(Shape(cost));
            else
                CountSentences(activated.Groups["effect"].Value, effects, line);

            return;
        }

        if (KeywordShape().IsMatch(line))
        {
            keywords.Add(Shape(line));
            return;
        }

        other.Add(Shape(line));
    }

    /// <summary>
    /// Counts the sentences of an effect that the phrase parser cannot read, not the whole effect.
    /// </summary>
    /// <remarks>
    /// "Draw a card, then discard a card" fails as a unit while "draw a card" is already
    /// supported. Counting the unit would put a sentence the compiler handles at the top of the
    /// queue; counting the failing halves puts the missing one there.
    /// </remarks>
    /// <summary>
    /// The effect split into the smallest pieces that mean anything on their own.
    /// </summary>
    /// <remarks>
    /// Sentences, except that an offer is spelled across two of them: "You may pay {E}{E}. If you
    /// do, put a +1/+1 counter on it." Split at the period, neither half parses — the first has
    /// no consequence and the second has no offer — so the ranking reported "you may pay {M}{M}"
    /// as the blocker of 39 cards while the real obstacle sat in the half that was thrown away.
    /// A clause that opens with one of the branch markers is glued back onto the sentence it
    /// belongs to before anything is tested.
    /// </remarks>
    private static IEnumerable<string> Units(string effect)
    {
        var units = new List<string>();

        foreach (var sentence in EffectPhrase.SentencesOf(effect))
        {
            if (units.Count > 0 && BranchMarker().IsMatch(sentence))
                units[^1] = units[^1].TrimEnd('.') + ". " + sentence;
            else
                units.Add(sentence);
        }

        return units;
    }

    [GeneratedRegex(@"^\s*(If|When) you (do|don't)", RegexOptions.IgnoreCase)]
    private static partial Regex BranchMarker();

    private static void CountSentences(string effect, Counter into, string? from = null)
    {
        foreach (var sentence in Units(effect))
        {
            var one = Normalise(sentence);

            // The compiler lifts activation restrictions off an effect before reading it, so they
            // are not blockers. Mirroring that matters: "Activate only as a sorcery" sat second in
            // this ranking on 103 cards after it had already been implemented, because the phrase
            // parser has never been the thing that reads it.
            if (one.Length == 0 || ActivationRestriction().IsMatch(one))
                continue;

            // Some sentences are only ever riders: "It's still a land" says nothing on its own
            // and is accepted after the animation it qualifies. Tested alone they always fail, so
            // they sat near the top of this ranking as blockers of 41 cards while the compiler
            // had read them all along - and the card's actual blocker was the sentence before.
            // Asking again with a prefix tells most of them apart.
            if (EffectPhrase.TryParse(one, out _))
                continue;

            if (EffectPhrase.TryParse("You gain 1 life. " + one, out _))
                continue;

            // Kept with the line the sentence came out of, because the shape alone is not
            // enough to act on: "you may pay {M}" is a fragment of some longer instruction, and
            // which one decides whether the fix is a new template or a widened anchor.
            into.Add(Shape(one), from);

            // Only the first failure. The compiler stops at the sentence it cannot read, so
            // every sentence after it is untested rather than unreadable - and counting those
            // put riders like "It can't be regenerated" near the top of this ranking as the
            // blocker of twelve cards, when it had been implemented for a long time and the
            // real obstacle was the destruction it qualifies.
            return;
        }
    }

    private void Report(string title, Counter counter)
    {
        var ranked = counter.Ranked().ToList();
        var total = ranked.Sum(p => p.Value);

        output.WriteLine($"---- {title}");
        output.WriteLine($"     {total} cards blocked, across {ranked.Count} distinct shapes");
        foreach (var (shape, count) in ranked.Take(30))
        {
            output.WriteLine($"  {count,6}  {shape}");
            if (counter.ExampleOf(shape) is { } sample)
                output.WriteLine($"          e.g. {sample}");
        }
        output.WriteLine(string.Empty);
    }

    private static string Normalise(string s) => s.Trim().TrimEnd('.', ' ');

    /// <summary>The keyword's name, with the cost or number it was printed with taken off.</summary>
    private static string KeywordName(string line)
    {
        var name = KeywordCost().Replace(Normalise(line), string.Empty).Trim().TrimEnd('—', '-', ' ');
        return name.Length == 0 ? Normalise(line) : name;
    }

    [GeneratedRegex(@"(\s+\d+|[\s—-]*(\{[^}]+\})+)\.?$")]
    private static partial Regex KeywordCost();

    /// <summary>A line with its numbers and mana symbols blanked, so shapes group together.</summary>
    private static string Shape(string line)
    {
        var shaped = NumberRun().Replace(Normalise(line), "N");
        shaped = ManaSymbol().Replace(shaped, "{M}");

        // A run of symbols is one cost, and blanking them one at a time filed the same keyword
        // under as many rows as it has printed costs: "Disturb {1}{W}" and "Disturb {3}{U}{U}"
        // became "{M}{M}" and "{M}{M}{M}". Collapsing the run merges 59 families and moves four
        // rows into the top ten - which is to say the queue was recommending the wrong work, not
        // by a little.
        shaped = SymbolRun().Replace(shaped, "{M}");

        // The same word said two ways is one shape; the digit form was already folded to N.
        shaped = NumberWord().Replace(shaped, "N");
        // Long enough that two different lines are not filed as one. At 96 every Act of Treason
        // variant collapsed into a single row of 11 cards whose trailing clauses — scry, add two
        // mana, discard then draw — were all different problems, and the plain wording the row
        // appeared to name had been supported for some time.
        return shaped.Length > 200 ? shaped[..200] : shaped;
    }

    /// <summary>
    /// Counts shapes, and keeps one whole line for each as evidence.
    /// </summary>
    /// <remarks>
    /// The example is the point. A shape is a sentence with its numbers blanked, and a sentence
    /// is not what the compiler reads — a line is. Working from shapes alone means guessing at
    /// the surrounding words, and that guess has been wrong repeatedly: a family gets widened,
    /// the ranking does not move, and the reason turns out to be a clause nobody looked at.
    /// </remarks>
    private sealed class Counter
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _examples = new(StringComparer.Ordinal);

        public void Add(string key, string? example = null)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + 1;

            if (example is not null && !_examples.ContainsKey(key))
                _examples[key] = example;
        }

        /// <summary>Adds a key that already stands for several cards.</summary>
        public void Add(string key, int howMany) =>
            _counts[key] = _counts.GetValueOrDefault(key) + howMany;

        public string? ExampleOf(string key) => _examples.GetValueOrDefault(key);

        public IEnumerable<KeyValuePair<string, int>> Ranked() =>
            _counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^(?:When|Whenever|At) (?<when>[^,]+), (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerShape();

    [GeneratedRegex(@"^(?<cost>[^:]{1,60}): (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ActivatedShape();

    [GeneratedRegex(@"^(\{[^}]+\}|,|\s|T)+$", RegexOptions.IgnoreCase)]
    private static partial Regex PayableCost();

    [GeneratedRegex(@",?\s*sacrifice ~\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeSelf();

    [GeneratedRegex(@"^Activate only ", RegexOptions.IgnoreCase)]
    private static partial Regex ActivationRestriction();

    /// <summary>
    /// Sentences that fail on their own and are not what blocked the line.
    /// </summary>
    /// <remarks>
    /// Two kinds, and both have put fiction at the head of this ranking. A restriction —
    /// "Activate only as a sorcery", "This ability triggers only once each turn" — is lifted off
    /// the text before the parser ever sees it, so it can never be the blocker; it appeared here
    /// on 68 cards purely because this report asked the parser a question the compiler does not.
    /// A rider — "then shuffle" — is legal in company and meaningless alone, and led the list at
    /// 487 while blocking nothing by itself.
    /// <para>
    /// Filtering them is not hiding work. It is the difference between a ranking that names what
    /// to fix and one that names what happens to fail when asked out of context.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(Activate only |This ability triggers only once each turn|"
            + @"Any player may activate this ability|"
            + @"(If you search(ed)? your library this way, )?shuffle( your library)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotABlocker();

    /// <summary>A word or three, optionally with a cost — the shape a keyword ability prints as.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z' -]{2,24}(\s+\d+|[\s—-]*(\{[^}]+\})+)?\.?$")]
    private static partial Regex KeywordShape();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex NumberRun();

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex ManaSymbol();

    /// <summary>A run of blanked mana symbols, which is one cost however many symbols it had.</summary>
    [GeneratedRegex(@"(\{M\})+")]
    private static partial Regex SymbolRun();

    /// <summary>A number written as a word, folded to the same N the digits already fold to.</summary>
    [GeneratedRegex(
        @"\b(one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NumberWord();
}
