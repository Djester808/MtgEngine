using System.Text.RegularExpressions;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class ZzCensusR2158Tests(ITestOutputHelper output)
{
    private static readonly (string Name, string Pattern)[] Families =
    [
        ("cost-reduce-cast", @"costs? \{[^:]{0,40}\} less to cast"),
        ("cost-increase-cast", @"costs? \{[^:]{0,40}\} more to cast"),
        ("cost-reduce-activate", @"cost \{[^:]{0,40}\} (less|more) to activate"),
        ("additional-cost", @"as an additional cost to (cast|activate)"),
        ("alternative-rather-than", @"rather than pay"),
        ("without-paying", @"without paying (its|their) mana cost"),
        ("as-though-flash", @"as though it had flash"),
        ("play-from-zone", @"you may (cast|play) .{0,60}from (your|a|the|their)"),
        ("cant-cast", @"can't cast"),
        ("cant-play-lands", @"can't play lands|play an additional land"),
        ("add-mana", @"(^|: |\. )Add [^.]{0,80}\."),
        ("spend-only", @"[Ss]pend (this|only) "),
        ("dont-untap", @"don't untap"),
        ("untap-target", @"[Uu]ntap (X |up to |target|all |each )"),
        ("pay-x-life", @"pay X life"),
        ("unless-they-pay", @"unless (they|its controller|that player|you) pays?"),
    ];

    [Fact]
    public void Census()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var complete = 0;
        var sole = new List<(string Card, string Line)>();
        foreach (var card in corpus)
        {
            var c = CardCompiler.Compile(card);
            if (c.IsComplete)
            {
                complete++;
                continue;
            }

            if (c.Unhandled.Count != 1)
                continue;

            sole.Add((card.Name, c.Unhandled[0]));
        }

        output.WriteLine($"corpus {corpus.Count} complete {complete} sole-blocked {sole.Count}");
        output.WriteLine(string.Empty);

        foreach (var (name, pattern) in Families)
        {
            var hits = sole.Where(s => Regex.IsMatch(s.Line, pattern)).ToList();
            output.WriteLine($"=== {name}: {hits.Count} sole-blocked cards");
            foreach (var group in hits.GroupBy(h => Shape(h.Line)).OrderByDescending(g => g.Count()).Take(14))
                output.WriteLine($"   {group.Count(),3}  {group.Key}  ||| {string.Join(", ", group.Select(g => g.Card).Take(4))}");

            output.WriteLine(string.Empty);
        }
    }

    private static string Shape(string line)
    {
        var s = Regex.Replace(line, @"\{[^}]*\}", "{M}");
        s = Regex.Replace(s, @"\b\d+\b", "N");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 120 ? s[..120] : s;
    }
}
