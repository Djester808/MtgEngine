using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Every line shape the compiler recognises is played by at least one behaviour test.
/// </summary>
/// <remarks>
/// Coverage of the corpus says how much text the compiler can <em>read</em>. It says nothing
/// about whether what it built out of that text does the right thing when it is played, and the
/// gap between those two is where this project keeps finding its bugs: exalted pumped the wrong
/// creature on 26 cards, persist returned a creature every time it died, and both compiled
/// perfectly for as long as they existed.
/// <para>
/// The only thing that catches that is a game. So this asserts the one property that makes the
/// behaviour suite meaningful as a whole: <strong>for every line the compiler knows how to read,
/// some test hands it a card that says that line.</strong> A matcher added without a test fails
/// here rather than sitting unplayed.
/// </para>
/// <para>
/// It works by reflection over the compiler's own generated patterns rather than a list of
/// mechanics, because a list of mechanics is the thing that goes stale - three of them already
/// have in this file's history.
/// </para>
/// </remarks>
public sealed partial class MechanicCoverageTests
{
    /// <summary>
    /// Patterns matched against part of a line rather than a whole one.
    /// </summary>
    /// <remarks>
    /// A fragment is anchored like a line but is only ever handed a phrase the compiler has
    /// already cut out of one - the condition after "When", the noun inside "can't be blocked
    /// by". No card line equals it, so it cannot be found this way and is covered through the
    /// line that contains it.
    /// <para>
    /// This is a hand-kept list, which this file otherwise argues against. It is tolerable here
    /// because of how it fails: a new fragment that is not listed makes this test fail, and
    /// somebody has to either write a test or add the name. It cannot go quiet on its own.
    /// </para>
    /// </remarks>
    private static readonly ImmutableHashSet<string> Fragments =
    [
        "PowerBlockers",
        "GreaterPowerBlockers",
        // Where a card's own trigger watches from, asked of the condition after "When"
        // rather than of a line: "you cycle ~" and "you cast ~" are phrases the trigger
        // reader cut out. Played through the lines that contain them, by
        // A_when_you_cycle_trigger_fires_on_cycling_it and
        // A_cards_own_cast_trigger_fires_from_the_stack.
        "CyclesSelf",
        "CastsSelf",
        "DiscardSelfCost",

        // The noun inside a "for each ..." count, not a line: DefinedCount is handed only the
        // phrase after "the number of". Played by A_party_counts_classes_rather_than_creatures,
        // through the cost-reduction line that contains it.
        "PartyLine",

        // The group inside "gets +N/+N for each ...", not a line: the counting static hands it
        // only what it cut out between "for each" and the full stop. Played by
        // A_creature_can_count_the_auras_attached_to_it, through the line that contains it.
        "AttachedCountLine",

        // One price out of an alternative cost's "and"-joined list, not a line: the reader
        // splits "pay {3}{U} and tap an untapped artifact you control" and offers each half to
        // these in turn. Played through the lines that contain them, by
        // An_alternative_cost_can_ask_for_mana_and_a_tapped_permanent_together and
        // An_alternative_cost_can_be_paid_in_life_while_the_board_allows_it.
        "AlternativeManaPart",
        "AlternativeLifePart",

        // One price out of an additional cost printed as a choice (CR 601.2b), not a line: the
        // reader cuts "sacrifice a creature or pay {2}" at the "or" and offers each half to
        // this in turn, so no card line is ever equal to it. Played through the lines that
        // contain it, by The_mana_price_is_charged_when_nothing_is_offered and
        // A_wide_filter_beside_a_mana_price_keeps_both_halves.
        "PayManaOption",

        // One clause of a copy effect's exception list (CR 707.9), not a line: each is handed
        // only what was cut out after "except" and split on "and". Played through the line that
        // contains them - An_exception_to_the_copy_changes_the_card_that_is_copied for the
        // in-addition form, An_exception_can_give_the_copy_a_different_size for the size, and
        // An_exception_can_add_a_type_and_a_keyword_at_once for the keyword and the splitting.
        // "Isn't legendary" rides the same splitter, and drops a supertype the copy tests
        // already read through the legend rule.
        "InAdditionClause",
        "SetSizeClause",
        "NotLegendaryClause",
        "HasKeywordClause",
    ];

    [Fact]
    public void Every_line_shape_the_compiler_reads_is_played_by_a_test()
    {
        var source = BehaviourTestSource();
        if (source is null)
        {
            // Nothing to compare against rather than a silent pass.
            Assert.Fail("could not find CompiledCardBehaviourTests.cs to read card text from.");
            return;
        }

        var lines = CardLinesIn(source);
        Assert.True(lines.Count > 100, $"only {lines.Count} card lines found - the reader is wrong.");

        var unplayed = new List<string>();

        foreach (var (name, pattern) in CompilerPatterns())
        {
            // Only whole-line shapes: the others are matched against phrases and are reached
            // through the line that holds them.
            if (!pattern.ToString().StartsWith('^') || Fragments.Contains(name))
                continue;

            if (!lines.Any(pattern.IsMatch))
                unplayed.Add(name);
        }

        Assert.True(
            unplayed.Count == 0,
            "the compiler reads these line shapes and no behaviour test ever plays one:\n  "
                + string.Join("\n  ", unplayed));
    }

    /// <summary>The compiler's generated line patterns, by the name each is declared under.</summary>
    private static IEnumerable<(string Name, Regex Pattern)> CompilerPatterns()
    {
        foreach (var method in typeof(CardCompiler).GetMethods(
            BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.ReturnType != typeof(Regex) || method.GetParameters().Length > 0)
                continue;

            if (method.Invoke(null, null) is Regex pattern)
                yield return (method.Name, pattern);
        }
    }

    /// <summary>Every line of card text any behaviour test hands the compiler.</summary>
    /// <remarks>
    /// Taken from the test source rather than from the tests themselves, because what a test
    /// gives the compiler is a string literal and there is no way to ask a compiled assembly what
    /// its literals were used for. The card's own name is replaced with "~" the way the compiler
    /// does it, since every self-referring pattern is written against that.
    /// </remarks>
    private static ImmutableHashSet<string> CardLinesIn(string source)
    {
        var lines = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        // A card's text is often written as several literals joined with "+", and each piece on
        // its own is not a line of anything. Joining them first is what makes this a reader of
        // card text rather than a reader of string fragments - without it a card whose wording
        // ran to two lines of C# was invisible, which is exactly the sort of quiet miss this
        // whole test exists to prevent.
        source = Concatenation().Replace(source, string.Empty);

        foreach (Match literal in StringLiteral().Matches(source))
        {
            var text = literal.Groups["text"].Value
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\u2014", "\u2014", StringComparison.Ordinal);

            foreach (var raw in text.Split('\n'))
            {
                var line = Reminder().Replace(raw, string.Empty).Trim();
                if (line.Length == 0)
                    continue;

                // An ability word is flavour with no rules meaning (CR 207.2c), and the
                // compiler strips it before any template sees the line - so "Strive - This
                // spell costs ..." reaches the matchers as the sentence behind it, and a
                // reader that did not strip it reported that shape unplayed while a test was
                // playing it. Added as a second reading rather than replacing the first, so a
                // pattern written against either form is still found.
                foreach (var reading in new[]
                {
                    line,
                    AbilityWordPrefix().Replace(line, string.Empty),
                })
                {
                    var named = TestCardName().Replace(reading, "~");

                    lines.Add(reading);
                    lines.Add(named);
                    lines.Add(SelfWord().Replace(reading, "~"));
                    lines.Add(SelfWord().Replace(named, "~"));
                }
            }
        }

        return lines.ToImmutable();
    }

    private static string? BehaviourTestSource()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null)
        {
            var candidate = Path.Combine(
                here.FullName, "tests", "MtgEngine.Rules.Tests", "CompiledCardBehaviourTests.cs");

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            here = here.Parent;
        }

        return null;
    }

    [GeneratedRegex(@"""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"""\s*\+\s*""")]
    private static partial Regex Concatenation();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Reminder();

    [GeneratedRegex(@"\bTest [A-Z][A-Za-z]*(?: [A-Z][A-Za-z]*)*")]
    private static partial Regex TestCardName();

    /// <summary>
    /// The words a card uses for itself, taken from the compiler rather than restated.
    /// </summary>
    /// <remarks>
    /// This was a hand-written list of seven against the compiler's twenty-five, and it had
    /// drifted exactly the way this file argues lists do: "this spell" was not on it, so a
    /// line saying so reached the matchers unnormalised and the shape behind it was reported
    /// unplayed while a test was playing it. Built from
    /// <see cref="CardCompiler.SelfReferenceTypeNames"/> so the reader normalises a line the
    /// same way the thing it is checking does.
    /// </remarks>
    private static readonly Regex SelfWordRegex = new(
        @"\bthis (" + string.Join('|', CardCompiler.SelfReferenceTypeNames) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex SelfWord() => SelfWordRegex;

    /// <summary>An ability word and the em dash after it (CR 207.2c).</summary>
    /// <remarks>
    /// Not the compiler's own pattern, which is private, and deliberately narrower: it
    /// refuses the roman numerals a Saga chapter opens with, so a chapter keeps its whole
    /// line rather than being beheaded into a sentence no card prints.
    /// </remarks>
    [GeneratedRegex(@"^(?![IVX]+(?:, ?[IVX]+)* \u2014 )[A-Z][A-Za-z0-9' -]{2,24} \u2014 ")]
    private static partial Regex AbilityWordPrefix();
}
