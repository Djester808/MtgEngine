using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Writes every line the compiler could not read to a file, so the work left can be measured by
/// what it *needs* rather than by how the sentence opens.
/// </summary>
/// <remarks>
/// A diagnostic rather than a guard: it asserts nothing about coverage, because the number it
/// would assert on is the one <see cref="CardCompilerCoverageTests"/> already owns. It exists so
/// the queue can be bucketed offline - "how many of these want a permanent chooser" is a question
/// no ranking by opening words can answer, and it is the question that decides what to build.
/// </remarks>
public sealed partial class UnreadLineDumpTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_unread_line_is_written_out_for_analysis()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("MTG_UNREAD_DUMP");
        if (string.IsNullOrWhiteSpace(destination))
        {
            output.WriteLine("MTG_UNREAD_DUMP not set — skipping.");
            return;
        }

        var lines = new List<string>(60_000);
        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
                continue;

            foreach (var unread in compiled.Unhandled)
                lines.Add($"{compiled.Unhandled.Count}\t{card.Name}\t{unread.Replace('\n', ' ')}");
        }

        File.WriteAllLines(destination, lines);
        output.WriteLine($"{lines.Count} unread lines written to {destination}");
    }

    /// <summary>
    /// Whether an unread line is unread because of its *words* or because of its *shape*.
    /// </summary>
    /// <remarks>
    /// Every unread line is cut into sentences and each sentence is compiled alone, on a card
    /// that is otherwise the same. A line whose every sentence reads on its own is not a
    /// vocabulary gap at all - the compiler knows every word in it and cannot put them together,
    /// and one change to how lines are composed would take the whole family. A line where no
    /// sentence reads is the opposite, and is worth exactly the cards that print it.
    /// <para>
    /// This is the measurement that decides whether the remaining work is thousands of small
    /// readers or a handful of structural ones, and no ranking by opening words can answer it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Unread_lines_are_split_into_composition_and_vocabulary_gaps()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var everySentenceReads = 0;
        var someSentenceReads = 0;
        var noSentenceReads = 0;
        var singleSentence = 0;
        var examples = new List<string>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];
            var sentences = SentencesIn(line);
            if (sentences.Count < 2)
            {
                singleSentence++;
                continue;
            }

            var read = 0;
            foreach (var sentence in sentences)
            {
                if (CardCompiler.Compile(ProbeOf(card, sentence)).IsComplete)
                    read++;
            }

            if (read == sentences.Count)
            {
                everySentenceReads++;
                if (examples.Count < 15)
                    examples.Add(card.Name + ": " + line.Replace('\n', ' '));
            }
            else if (read > 0)
            {
                someSentenceReads++;
            }
            else
            {
                noSentenceReads++;
            }
        }

        var multi = everySentenceReads + someSentenceReads + noSentenceReads;
        output.WriteLine($"one-line-short cards with a single-sentence line: {singleSentence}");
        output.WriteLine($"one-line-short cards with a multi-sentence line:  {multi}");
        output.WriteLine($"   every sentence reads alone (composition gap): {everySentenceReads}");
        output.WriteLine($"   some sentences read alone:                    {someSentenceReads}");
        output.WriteLine($"   no sentence reads alone (vocabulary gap):     {noSentenceReads}");
        output.WriteLine(string.Empty);
        output.WriteLine("---- lines the compiler can read entirely, one sentence at a time:");
        foreach (var example in examples)
            output.WriteLine("  " + example);
    }

    /// <summary>
    /// Every individual sentence the compiler cannot read, written out for ranking.
    /// </summary>
    /// <remarks>
    /// The line-level queue counts combinations: "draw a card" beside twelve different second
    /// sentences is twelve templates, each worth one card, and the reader that would take all
    /// twelve is invisible. Cutting to sentences factors that out, so a family shows up at its
    /// real size rather than divided by however many ways it has been paired.
    /// </remarks>
    [Fact]
    public void Every_unread_sentence_is_written_out_for_ranking()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("MTG_SENTENCE_DUMP");
        if (string.IsNullOrWhiteSpace(destination))
        {
            output.WriteLine("MTG_SENTENCE_DUMP not set — skipping.");
            return;
        }

        var rows = new List<string>(80_000);
        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
                continue;

            foreach (var line in compiled.Unhandled)
            {
                foreach (var sentence in SentencesIn(line))
                {
                    if (CardCompiler.Compile(ProbeOf(card, sentence)).IsComplete)
                        continue;

                    rows.Add($"{compiled.Unhandled.Count}\t{card.Name}\t{sentence.Replace('\n', ' ')}");
                }
            }
        }

        File.WriteAllLines(destination, rows);
        output.WriteLine($"{rows.Count} unread sentences written to {destination}");
    }

    /// <summary>
    /// The one sentence whose removal lets a line read - the sentence actually blocking it.
    /// </summary>
    /// <remarks>
    /// Compiling a sentence alone is the wrong question for a modifier. "Activate only as a
    /// sorcery." never compiles by itself - it is not an ability - so a ranking built that way
    /// puts it at the top with 172 cards behind it, when the compiler has read that clause in
    /// context all along and the real blocker on every one of those lines is somewhere else.
    /// <para>
    /// Taking the sentence *out* asks the question that was meant: if the rest of the line reads
    /// without it, it is the blocker; if the line still does not read, it was riding along.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_sentence_actually_blocking_each_line_is_written_out()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("MTG_BLOCKER_DUMP");
        if (string.IsNullOrWhiteSpace(destination))
        {
            output.WriteLine("MTG_BLOCKER_DUMP not set — skipping.");
            return;
        }

        var rows = new List<string>(20_000);
        var noSingleBlocker = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];
            var sentences = SentencesIn(line);

            if (sentences.Count < 2)
            {
                rows.Add($"single\t{card.Name}\t{line.Replace('\n', ' ')}");
                continue;
            }

            var blockers = new List<string>();
            for (var i = 0; i < sentences.Count; i++)
            {
                var without = string.Join(" ", sentences.Where((_, j) => j != i));
                if (CardCompiler.Compile(ProbeOf(card, without)).IsComplete)
                    blockers.Add(sentences[i]);
            }

            if (blockers.Count == 1)
                rows.Add($"blocker\t{card.Name}\t{blockers[0].Replace('\n', ' ')}");
            else
                noSingleBlocker++;
        }

        File.WriteAllLines(destination, rows);
        output.WriteLine($"{rows.Count} rows written to {destination}");
        output.WriteLine($"{noSingleBlocker} lines have no single blocking sentence");
    }

    /// <summary>
    /// Unread lines that a mechanical rewrite makes readable - defects, not missing features.
    /// </summary>
    /// <remarks>
    /// "Creatures with flying" was refused for years because the reader singularised the last
    /// letter of the phrase rather than its noun, and the grammar underneath had understood
    /// "creature with flying" all along. That is not a missing feature, it is a bug, and it was
    /// invisible because the symptom - a line the compiler will not read - looks identical either
    /// way.
    /// <para>
    /// Each rewrite below is meaning-preserving. If applying one makes a line compile, the
    /// vocabulary was already there and something on the way in dropped it, so the count beside a
    /// rewrite is a count of cards behind one defect rather than behind one new reader.
    /// </para>
    /// </remarks>
    [Fact]
    public void Unread_lines_that_a_meaning_preserving_rewrite_would_fix_are_counted()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var rewrites = new (string Name, Func<string, string> Apply)[]
        {
            ("curly apostrophe to straight", s => s.Replace('’', '\'')),
            ("em dash to hyphen", s => s.Replace('—', '-')),
            ("drop reminder text", s => ReminderText().Replace(s, string.Empty).Trim()),
            ("singularise noun before 'with'", SingulariseBeforeWith),
            ("'an' to 'a'", s => s.Replace(" an ", " a ", StringComparison.Ordinal)),
            ("drop a trailing full stop", s => s.TrimEnd('.')),
            ("'that player' to 'target player'",
                s => s.Replace("that player", "target player", StringComparison.OrdinalIgnoreCase)),

            // The class that keeps paying: a prefix or a separator the compiler's own
            // preprocessing cannot see. "Power-up" was unread for the hyphen in a character
            // class, and thirty-seven cards went with it.
            ("strip a leading 'Word \u2014 ' prefix", s => LeadingPrefix().Replace(s, string.Empty)),
            ("en dash to em dash", s => s.Replace('\u2013', '\u2014')),
            ("non-breaking space to space", s => s.Replace('\u00a0', ' ')),
            ("curly quotes to straight",
                s => s.Replace('\u201c', '"').Replace('\u201d', '"')),
            ("'colour' to 'color'",
                s => s.Replace("colour", "color", StringComparison.OrdinalIgnoreCase)),

            // The plural-before-a-qualifier bug, in the other places it could hide. It was found
            // once before " with " and cost a whole family of block restrictions; the same
            // mistake would look identical before any other qualifier.
            ("singularise noun before 'you control'", s => SingulariseBefore(s, " you control")),
            ("singularise noun before 'in your'", s => SingulariseBefore(s, " in your")),
            ("singularise noun before 'an opponent'", s => SingulariseBefore(s, " an opponent")),
            ("collapse repeated spaces", s => RepeatedSpace().Replace(s, " ")),
        };

        var fixedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];

            foreach (var (name, apply) in rewrites)
            {
                var rewritten = apply(line);
                if (string.Equals(rewritten, line, StringComparison.Ordinal))
                    continue;

                if (!CardCompiler.Compile(ProbeOf(card, rewritten)).IsComplete)
                    continue;

                fixedBy[name] = fixedBy.GetValueOrDefault(name) + 1;
                examples.TryAdd(name, card.Name + ": " + line.Replace('\n', ' '));
            }
        }

        output.WriteLine("cards a meaning-preserving rewrite would complete:");
        foreach (var (name, count) in fixedBy.OrderByDescending(p => p.Value))
        {
            output.WriteLine($"  {count,5}  {name}");
            output.WriteLine($"         e.g. {examples[name]}");
        }

        if (fixedBy.Count == 0)
            output.WriteLine("  (none - every unread line is unread for its content)");
    }

    /// <summary>Singularises the plural noun in front of a named qualifier.</summary>
    private static string SingulariseBefore(string phrase, string qualifier)
    {
        var at = phrase.IndexOf(qualifier, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
            return phrase;

        var head = phrase[..at];
        return head.EndsWith('s') ? head[..^1] + phrase[at..] : phrase;
    }

    [GeneratedRegex(@"  +")]
    private static partial Regex RepeatedSpace();

    /// <summary>Singularises the noun in front of a qualifier, not the last letter of a phrase.</summary>
    private static string SingulariseBeforeWith(string phrase)
    {
        var at = phrase.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
            return phrase;

        var head = phrase[..at];
        return head.EndsWith('s') ? head[..^1] + phrase[at..] : phrase;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ReminderText();

    /// <summary>
    /// The compiler's own "this creature" rewrite, so the two dumps can be compared.
    /// </summary>
    /// <remarks>
    /// The unread dump is the compiler's normalised text and this one was the printed text, so
    /// a card that refers to itself appeared in one as "~" and in the other as "this creature".
    /// Anything comparing the two then reported a difference that exists only between the dumps
    /// - which the near-miss probe was doing, on two of the shapes it ranked highest.
    /// </remarks>
    [GeneratedRegex(
        @"\bthis (creature|permanent|card|spell|land|artifact|enchantment|planeswalker"
            + @"|equipment|aura|vehicle|saga|token|battle|class|room|kindred|spacecraft"
            + @"|contraption|siege|case|attraction|planet|conspiracy|realm)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SelfReferences();

    /// <summary>A capitalised phrase before an em dash — an ability word, or a card's name.</summary>
    [GeneratedRegex(@"^[^\u2014]{2,30} \u2014 ")]
    private static partial Regex LeadingPrefix();

    /// <summary>
    /// Which "as long as" clauses the shared condition vocabulary cannot answer, ranked.
    /// </summary>
    /// <remarks>
    /// `BoardConditions` is asked by static abilities, activation restrictions and the
    /// enters-with-counters replacements alike, so a phrase it cannot read costs cards in every
    /// one of them at once - and the line-level queue cannot see that, because each of those
    /// lines fails for what looks like a different reason.
    /// <para>
    /// Pulling the clauses out of the corpus and asking the vocabulary directly turns "this line
    /// is unread" into "this *phrase* is unread, in N places". Twice today that has been a tally
    /// the state already kept with nothing able to ask for it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Condition_clauses_the_vocabulary_cannot_read_are_ranked()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var unreadable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var readable = 0;

        foreach (var card in corpus)
        {
            // Reminder text is not a rule the compiler has to read - it restates one - and it is
            // where most of these clauses live: every landwalk card prints "as long as defending
            // player controls a Swamp" in brackets. Counting those put a phantom family of 162 at
            // the top of the first run of this, for a rule the engine has implemented for ages.
            var text = ReminderText().Replace(card.OracleText, string.Empty);
            if (text.Length == 0)
                continue;

            foreach (Match clause in ConditionClause().Matches(text))
            {
                var phrase = clause.Groups["what"].Value.Trim().TrimEnd('.', ',');
                if (phrase.Length is 0 or > 90)
                    continue;

                // The compiler substitutes the card's name for the tilde before any of this runs,
                // so the probe has to put it back or every self-referring clause reads as unique.
                var normalised = phrase.Replace(card.Name, "~", StringComparison.Ordinal);

                if (BoardConditions.Parse(normalised) is not null)
                    readable++;
                else
                    unreadable[normalised] = unreadable.GetValueOrDefault(normalised) + 1;
            }
        }

        output.WriteLine($"{readable} clauses read, {unreadable.Values.Sum()} not, "
            + $"across {unreadable.Count} distinct phrases");
        output.WriteLine(string.Empty);

        foreach (var (phrase, count) in unreadable.OrderByDescending(p => p.Value).Take(25))
            output.WriteLine($"  {count,5}  {phrase}");
    }

    /// <summary>The clause after "as long as" or "only if" — what a board condition is asked.</summary>
    [GeneratedRegex(@"(as long as|only if) (?<what>[^.;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionClause();

    /// <summary>
    /// Every line the compiler *does* read, for comparison against the ones it does not.
    /// </summary>
    /// <remarks>
    /// The most productive defects this engine has are wording variants: a rule it understands,
    /// said a way it does not accept. "Activate no more than twice" against "Activate only twice";
    /// "creatures with flying" against a singulariser that took the last letter. Both were found
    /// by a person noticing, which does not scale.
    /// <para>
    /// An unread line that is nearly identical to a line the compiler reads is exactly that shape.
    /// This writes out the read side so the two can be compared offline - the comparison itself is
    /// not a test, because "nearly identical" is a judgement and a threshold in a test would go
    /// stale or go quiet.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_line_the_compiler_reads_is_written_out_for_comparison()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("MTG_READ_DUMP");
        if (string.IsNullOrWhiteSpace(destination))
        {
            output.WriteLine("MTG_READ_DUMP not set — skipping.");
            return;
        }

        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in corpus)
        {
            if (!CardCompiler.Compile(card).IsComplete)
                continue;

            foreach (var line in ReminderText().Replace(card.OracleText, string.Empty).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 3)
                {
                    lines.Add(SelfReferences().Replace(
                        trimmed.Replace(card.Name, "~", StringComparison.Ordinal), "~"));
                }
            }
        }

        File.WriteAllLines(destination, lines);
        output.WriteLine($"{lines.Count} distinct read lines written to {destination}");
    }

    /// <summary>
    /// Measures a candidate reader before it is written: a rewrite file names the phrase, the
    /// substitution that would replace it with one the compiler already reads, and a control.
    /// </summary>
    /// <remarks>
    /// Excision has over-counted the worth of a family by between 1.5x and 82x, because deleting
    /// a clause deletes whatever else was wrong with the sentence too. The honest ceiling is the
    /// <em>substitution</em>: rewrite only the phrase into one already read and leave the rest of
    /// the words alone. This runs all three cuts over the same set of cards so the three numbers
    /// can be compared - excise the phrase, substitute it, and excise a different sentence of the
    /// same line as a control that says whether any excision at all would have completed them.
    /// <para>
    /// Driven entirely by a file so that measuring a new family needs no code: each row is
    /// <c>name TAB mode TAB pattern TAB replacement</c>, mode one of <c>sub</c>, <c>excise</c> or
    /// <c>ctrl</c>. It asserts nothing - it is a ruler, and the ratchet is the gate.
    /// </para>
    /// </remarks>
    [Fact]
    public void Candidate_rewrites_named_in_a_file_are_measured_three_ways()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present - skipping.");
            return;
        }

        var source = Environment.GetEnvironmentVariable("MTG_REWRITE_FILE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            output.WriteLine("MTG_REWRITE_FILE not set - skipping.");
            return;
        }

        var probes = new List<(string Name, string Mode, Regex Pattern, string Replacement)>();
        foreach (var row in File.ReadAllLines(source))
        {
            if (row.Length == 0 || row[0] == '#')
                continue;

            var parts = row.Split('\t');
            if (parts.Length < 3)
                continue;

            probes.Add((
                parts[0],
                parts[1],
                new Regex(parts[2], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
                parts.Length > 3 ? parts[3] : string.Empty));
        }

        // Compiled once and kept, because every probe asks the same question of the same set:
        // recompiling the corpus per row turned a two-minute ruler into a forty-minute one.
        var short1 = new List<(CardDefinition Card, string Line)>(20_000);
        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete && compiled.Unhandled.Count == 1)
                short1.Add((card, compiled.Unhandled[0]));
        }

        output.WriteLine($"{short1.Count} cards are one line short.");

        foreach (var (name, mode, pattern, replacement) in probes)
        {
            var matched = 0;
            var completed = 0;
            var examples = new List<string>();

            foreach (var (card, line) in short1)
            {
                if (!pattern.IsMatch(line))
                    continue;

                matched++;

                string rewritten;
                if (string.Equals(mode, "ctrl", StringComparison.Ordinal))
                {
                    // The control: cut a sentence the candidate phrase is *not* in. A family
                    // whose control completes as many cards as the substitution was never a
                    // family - the line was one edit from reading whatever you did to it.
                    var sentences = SentencesIn(line);
                    var other = sentences.FindIndex(s => !pattern.IsMatch(s));
                    if (other < 0)
                        continue;

                    rewritten = string.Join(" ", sentences.Where((_, j) => j != other));
                }
                else
                {
                    rewritten = pattern.Replace(
                        line, string.Equals(mode, "excise", StringComparison.Ordinal)
                            ? string.Empty
                            : replacement);
                }

                rewritten = RepeatedSpace().Replace(rewritten, " ").Trim();
                if (rewritten.Length == 0 || string.Equals(rewritten, line, StringComparison.Ordinal))
                    continue;

                if (!CardCompiler.Compile(ProbeOf(card, rewritten)).IsComplete)
                    continue;

                completed++;
                if (examples.Count < 8)
                    examples.Add(card.Name + ": " + line.Replace('\n', ' '));
            }

            output.WriteLine($"{completed,5} / {matched,5} matched  [{mode}] {name}");
            foreach (var example in examples)
                output.WriteLine("         " + example);
        }
    }

    /// <summary>
    /// Compiles whatever text a file names, so a candidate wording can be tried without a corpus.
    /// </summary>
    /// <remarks>
    /// Working from shapes alone means guessing at the surrounding words, and reading a
    /// candidate reader out of the regex means guessing at what the compiler does with them.
    /// This asks it. Each row is <c>name TAB oracle text</c>, newlines written as a backslash-n pair, and
    /// the answer is the card's own <c>Unhandled</c> list.
    /// </remarks>
    [Fact]
    public void Text_named_in_a_file_is_compiled_and_its_unread_lines_printed()
    {
        var source = Environment.GetEnvironmentVariable("MTG_TEXT_PROBE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            output.WriteLine("MTG_TEXT_PROBE not set - skipping.");
            return;
        }

        foreach (var row in File.ReadAllLines(source))
        {
            if (row.Length == 0 || row[0] == '#')
                continue;

            var parts = row.Split('\t');
            if (parts.Length < 2)
                continue;

            var card = new CardDefinition
            {
                OracleId = "probe-" + parts[0],
                Name = parts[0],
                OracleText = parts[1].Replace("\\n", "\n", StringComparison.Ordinal),
                CardTypes = parts.Length > 2 && parts[2].Length > 0
                    ? Enum.Parse<MtgEngine.Domain.Enums.CardType>(parts[2], ignoreCase: true)
                    : MtgEngine.Domain.Enums.CardType.Creature,
                Power = 2,
                Toughness = 2,
            };

            var compiled = CardCompiler.Compile(card);
            output.WriteLine(compiled.IsComplete
                ? $"COMPLETE  {parts[0]}"
                : $"UNREAD    {parts[0]}  ->  " + string.Join(" | ", compiled.Unhandled));
        }
    }

    /// <summary>The same card carrying one sentence, so that sentence can be compiled alone.</summary>
    /// <remarks>
    /// Copied field by field because <see cref="CardDefinition"/> is a class rather than a
    /// record, and every field the compiler reads has to come with it: a sentence about power
    /// reads differently on a card that has none.
    /// </remarks>
    private static CardDefinition ProbeOf(CardDefinition card, string sentence) => new()
    {
        OracleId = card.OracleId,
        Name = card.Name,
        ManaCostRaw = card.ManaCostRaw,
        Cmc = card.Cmc,
        CardTypes = card.CardTypes,
        Subtypes = card.Subtypes,
        Supertypes = card.Supertypes,
        OracleText = sentence,
        Power = card.Power,
        Toughness = card.Toughness,
        StartingLoyalty = card.StartingLoyalty,
        Keywords = card.Keywords,
        ColorIdentity = card.ColorIdentity,
        Colors = card.Colors,
    };

    /// <summary>Cuts a line into sentences the way a reader would, keeping the full stop.</summary>
    private static List<string> SentencesIn(string line)
    {
        var sentences = new List<string>();
        var start = 0;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '.' || i + 1 >= line.Length || line[i + 1] != ' ')
                continue;

            sentences.Add(line[start..(i + 1)].Trim());
            start = i + 2;
        }

        if (start < line.Length)
            sentences.Add(line[start..].Trim());

        return sentences;
    }
}
