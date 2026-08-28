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
        "CyclingTrigger",
        "DiscardSelfCost",

        // The noun inside a "for each ..." count, not a line: DefinedCount is handed only the
        // phrase after "the number of". Played by A_party_counts_classes_rather_than_creatures,
        // through the cost-reduction line that contains it.
        "PartyLine",

        // The group inside "gets +N/+N for each ...", not a line: the counting static hands it
        // only what it cut out between "for each" and the full stop. Played by
        // A_creature_can_count_the_auras_attached_to_it, through the line that contains it.
        "AttachedCountLine",
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

                lines.Add(line);
                lines.Add(TestCardName().Replace(line, "~"));
                lines.Add(SelfWord().Replace(line, "~"));
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

    [GeneratedRegex(@"\bthis (creature|permanent|Saga|Class|Case|Room|Spacecraft)\b")]
    private static partial Regex SelfWord();
}
