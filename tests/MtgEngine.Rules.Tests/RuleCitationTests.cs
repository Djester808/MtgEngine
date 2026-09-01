using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Every Comprehensive Rules number the engine cites has to exist in the Comprehensive Rules.
/// </summary>
/// <remarks>
/// This gate exists because the citations were wrong. Shuffle was cited as CR 701.20, which is
/// Reveal; tapping as CR 701.21a, which is Sacrifice. Both were written from memory of roughly
/// where those rules live, both read as authoritative, and neither would ever have been noticed
/// — a wrong citation is worse than none, because it invites the next reader to trust it.
/// <para>
/// The rules text is a live asset in this repo, so the claim is checkable. The engine may not
/// reference the Api project, so the document is read from disk rather than through
/// <c>ComprehensiveRules</c>; if it moves, this fails loudly rather than skipping, because a
/// gate that quietly passes when it cannot find its evidence is not a gate.
/// </para>
/// </remarks>
public sealed partial class RuleCitationTests
{
    /// <summary>A rule number as the document writes it: 117, 117.3, or 117.3d.</summary>
    private static readonly Regex Citation = new(
        @"\b(\d{3}(?:\.\d+[a-z]?)?)\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>A line of the document that defines a rule: "704.5a If a player..."</summary>
    private static readonly Regex Definition = new(
        @"^(\d{3}(?:\.\d+[a-z]?)?)\.?\s", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    [Fact]
    public void Every_rule_the_engine_cites_exists_in_the_rules_document()
    {
        var root = RepositoryRoot();
        var known = KnownRules(Path.Combine(root, "MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"));

        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "MtgEngine.Rules"), "*.cs", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;

                // Only lines that are making a citation. Any other three-digit number in the
                // source — a guard count, a life total — is not a claim about the rules.
                if (!line.Contains("CR ", StringComparison.Ordinal)
                    && !line.Contains("Rule =>", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Citation.Matches(line))
                {
                    if (!known.Contains(match.Value))
                        bad.Add($"{Path.GetFileName(file)}:{lineNumber} cites CR {match.Value}");
                }
            }
        }

        Assert.True(bad.Count == 0, "Citations with no such rule:\n  " + string.Join("\n  ", bad));
    }

    /// <summary>
    /// The keyword actions the engine implements, and the rule each one is (CR 701).
    /// </summary>
    /// <remarks>
    /// Existence was never a strong enough check. Every citation this table was written to fix
    /// pointed at a rule that <em>does</em> exist: scry cited CR 701.18, which is Play;
    /// regeneration cited 701.15, which is Goad; destroy cited 701.7, which is Create. The
    /// existence gate passed all of them, and a reader following the reference would land on an
    /// unrelated paragraph and have no way to tell it was not the one meant.
    /// <para>
    /// It also does the thing an existence check cannot: CR 701's numbering shifts when Wizards
    /// inserts a keyword action, and swapping in a new rules release is meant to be a file swap.
    /// This turns "every 701 citation in the engine silently moved by one" into a failing test.
    /// </para>
    /// </remarks>
    private static readonly (string Rule, string Action)[] KeywordActions =
    [
        ("701.3", "Attach"),
        ("701.6", "Counter"),
        ("701.7", "Create"),
        ("701.8", "Destroy"),
        ("701.9", "Discard"),
        ("701.13", "Exile"),
        ("701.17", "Mill"),
        ("701.19", "Regenerate"),
        ("701.21", "Sacrifice"),
        ("701.22", "Scry"),
        ("701.24", "Shuffle"),
        ("701.25", "Surveil"),
        ("701.26", "Tap and Untap"),
    ];

    [Fact]
    public void Each_keyword_action_the_engine_cites_is_the_rule_it_says_it_is()
    {
        var path = Path.Combine(
            RepositoryRoot(), "MtgEngine.Api", "Knowledge", "comprehensive-rules.txt");

        var headings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            // A section heading is the rule number, a full stop, and the name — and nothing else.
            var heading = Regex.Match(
                line, @"^(?<rule>701\.\d+)\.\s+(?<name>[A-Z][A-Za-z ]+)$", RegexOptions.None,
                TimeSpan.FromMilliseconds(200));

            if (heading.Success)
                headings[heading.Groups["rule"].Value] = heading.Groups["name"].Value.Trim();
        }

        Assert.NotEmpty(headings);

        var wrong = KeywordActions
            .Where(pair => !headings.TryGetValue(pair.Rule, out var name)
                || !string.Equals(name, pair.Action, StringComparison.Ordinal))
            .Select(pair =>
                $"CR {pair.Rule} should be {pair.Action}, document says "
                    + $"{headings.GetValueOrDefault(pair.Rule) ?? "nothing"}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join("\n  ", wrong));
    }

    [Fact]
    public void The_checker_would_notice_a_rule_that_does_not_exist()
    {
        // The negative control. Without it, a checker that found nothing to read, or built an
        // empty set of citations, would report success just as loudly.
        var known = KnownRules(
            Path.Combine(RepositoryRoot(), "MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"));

        Assert.Contains("704.5a", known);
        Assert.Contains("117.4", known);
        Assert.Contains("400", known);
        Assert.DoesNotContain("999.9z", known);
    }

    private static HashSet<string> KnownRules(string documentPath)
    {
        Assert.True(File.Exists(documentPath), $"The rules document is not at {documentPath}.");

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(documentPath))
        {
            var match = Definition.Match(line);
            if (match.Success)
                known.Add(match.Groups[1].Value);
        }

        Assert.True(known.Count > 2000, $"Only parsed {known.Count} rules; the document looks wrong.");
        return known;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MtgEngine.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
    /// <summary>
    /// A keyword whose rule carries a condition compiles to an ability that says so.
    /// </summary>
    /// <remarks>
    /// The rulebook writes most keywords out in full: <c>"Persist" means "When this permanent is
    /// put into a graveyard from the battlefield, <strong>if it had no -1/-1 counters on it</strong>,
    /// return it..."</c>. That "if" is not decoration - for persist, undying and renown it is the
    /// whole difference between a mechanic and one that repeats forever, and in each case the
    /// first firing looks identical either way.
    /// <para>
    /// Two of those three were wrong when this was written, and renown's compiled text did not
    /// even claim otherwise: it described an ability with no condition, which is exactly what it
    /// built. So this compares the compiler's own words against the rulebook's - a keyword whose
    /// rule says "if" or "unless" must produce an ability whose text says it too.
    /// </para>
    /// <para>
    /// It cannot prove the condition is <em>implemented</em> - only a game can do that, and there
    /// are behaviour tests for these. What it catches is the cheaper mistake underneath: writing
    /// down an ability that is not the one the rules describe, which is where all three of these
    /// bugs started.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_conditional_keyword_compiles_to_an_ability_that_states_its_condition()
    {
        var rules = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"));

        var unstated = new List<string>();
        var checkedKeywords = 0;

        foreach (Match definition in KeywordDefinition().Matches(rules))
        {
            var keyword = definition.Groups["keyword"].Value;
            var meaning = definition.Groups["meaning"].Value;

            if (!Conditional().IsMatch(meaning) || Rewritten.ContainsKey(keyword))
                continue;

            // The line a card would print. Parameterised keywords take a number; the rest stand
            // alone. Anything the compiler does not recognise is not this test's business.
            foreach (var line in new[] { keyword, keyword + " 1" })
            {
                var card = new CardDefinition
                {
                    OracleId = "oracle-" + line,
                    Name = "Rule Probe",
                    OracleText = line,
                    CardTypes = CardType.Creature,
                    Power = 2,
                    Toughness = 2,
                };

                var compiled = CardCompiler.Compile(card);
                if (!compiled.IsComplete)
                    continue;

                var texts = compiled.Triggers.Select(t => t.Text)
                    .Concat(compiled.Activated.Select(a => a.Text))
                    .ToList();

                if (texts.Count == 0)
                    continue;

                checkedKeywords++;

                if (!texts.Any(t => Conditional().IsMatch(t)))
                    unstated.Add($"{keyword}: rules say \"{Excerpt(meaning)}\", engine says \"{Excerpt(texts[0])}\"");

                break;
            }
        }

        Assert.True(
            checkedKeywords > 0,
            "no conditional keyword compiled at all, so nothing was compared.");

        Assert.True(
            unstated.Count == 0,
            "these keywords have a condition in the rules and none in the ability the compiler "
                + $"builds:\n  " + string.Join("\n  ", unstated));
    }

    /// <summary>
    /// Keywords whose rule contains "if" without that "if" gating the ability.
    /// </summary>
    /// <remarks>
    /// Each of these was read against the rulebook and judged, and the reason is written down so
    /// the next person does not have to judge it again. They are not exemptions from being
    /// correct - every one has behaviour tests - only from this particular check, which looks for
    /// one word and cannot tell what that word is doing in the sentence.
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> Rewritten =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Storm"] =
                "the condition is a permission about the copies' targets, not a gate on the "
                    + "ability: storm copies the spell either way.",
            ["Cascade"] =
                "the condition restates what the exile loop already guarantees - it stopped at a "
                    + "card of lower mana value, so the card it stopped at has one.",
            ["Myriad"] =
                "\"if one or more tokens are created this way\" guards the clause that exiles "
                    + "them, which the compiled text states outright.",
            ["Fabricate"] =
                "\"you may put counters on it; if you don't, make tokens\" is a choice between "
                    + "two outcomes, and the engine writes it as the choice it is.",
            ["Riot"] = "the same either/or as fabricate, written the same way.",
        }.ToImmutableDictionary();

    private static string Excerpt(string text) =>
        text.Length <= 90 ? text : text[..90] + "...";

    /// <summary>The rulebook's own "X means ..." definition of a keyword.</summary>
    [GeneratedRegex(
        "\u201c(?<keyword>[A-Za-z][A-Za-z -]*?)(?: N| \\[[^\\]]+\\])?\u201d means \u201c(?<meaning>[^\u201d]+)\u201d")]
    private static partial Regex KeywordDefinition();

    [GeneratedRegex(@"\b(if|unless)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Conditional();
}
