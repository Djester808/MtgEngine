using System.Text.RegularExpressions;

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
public sealed class RuleCitationTests
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
}
