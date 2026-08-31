using System.Text.RegularExpressions;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>Temporary measurement harness for round 21-40 (comparison filters).</summary>
public sealed partial class ZzR2140Census
{
    private static readonly string Nl = ((char)10).ToString();

    private static string Out(string name)
    {
        var dir = Environment.GetEnvironmentVariable("R2140_OUT") ?? ".";
        return Path.Combine(dir, name);
    }

    /// <summary>Anything that names a comparison rather than a bare quantity.</summary>
    [GeneratedRegex(
        @"(less than or equal to|greater than or equal to|less than|greater than|equal to|\b(power|toughness|mana value|life total|devotion)\b[^.]{0,40}\b(or (less|greater|more|fewer))\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Family();

    [Fact]
    public void Census()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var complete = 0;
        var completeNames = new List<string>();
        var effects = new List<string>();
        var soleBlockers = new List<string>();
        var anyBlockers = new List<string>();
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var shapeExample = new Dictionary<string, string>(StringComparer.Ordinal);
        var populationText = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);

            if (card.OracleText is { Length: > 0 } text && Family().IsMatch(text))
                populationText++;

            if (compiled.IsComplete)
            {
                complete++;
                completeNames.Add(card.Name);
                effects.Add("## " + card.Name + " :: " + Describe(compiled));
                continue;
            }

            var inFamily = compiled.Unhandled.Where(l => Family().IsMatch(l)).ToList();
            if (inFamily.Count == 0)
                continue;

            anyBlockers.Add("@@ " + card.Name + " [" + compiled.Unhandled.Count + "] :: "
                + string.Join(" || ", inFamily.Select(l => l.Replace(Nl, " | "))));

            if (compiled.Unhandled.Count == 1)
            {
                var line = compiled.Unhandled[0].Replace(Nl, " | ");
                soleBlockers.Add("@@ " + card.Name + " :: " + line);

                var shape = Shape(line);
                shapes[shape] = shapes.TryGetValue(shape, out var n) ? n + 1 : 1;
                if (!shapeExample.ContainsKey(shape))
                    shapeExample[shape] = line;
            }
        }

        completeNames.Sort(StringComparer.Ordinal);
        effects.Sort(StringComparer.Ordinal);
        soleBlockers.Sort(StringComparer.Ordinal);
        anyBlockers.Sort(StringComparer.Ordinal);

        File.WriteAllLines(Out("complete-names.txt"), completeNames);
        File.WriteAllLines(Out("complete-effects.txt"),
            new[] { "COMPLETE: " + complete + " of " + corpus.Count }.Concat(effects));
        File.WriteAllLines(Out("sole-blockers.txt"),
            new[] { "SOLE-BLOCKER COMPARISON LINES: " + soleBlockers.Count }.Concat(soleBlockers));
        File.WriteAllLines(Out("any-blockers.txt"),
            new[] { "CARDS WITH >=1 COMPARISON LINE UNREAD: " + anyBlockers.Count }.Concat(anyBlockers));
        File.WriteAllLines(Out("shapes.txt"),
            new[] { "POPULATION (rules text mentions a comparison): " + populationText }
            .Concat(shapes.OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Value + "\t" + kv.Key + "\t\tEG: " + shapeExample[kv.Key])));
    }

    /// <summary>Numbers and capitalised names normalised out, so shapes group.</summary>
    private static string Shape(string line)
    {
        var s = Regex.Replace(line, @"\d+", "N");
        s = Regex.Replace(s, @"\{[^}]*\}", "{M}");
        s = Regex.Replace(s, @"\b[A-Z][a-z]+(?: [A-Z][a-z]+)*\b", "NAME");
        return s.Length > 160 ? s[..160] : s;
    }

    private static string Describe(CompiledCard card)
    {
        var parts = new List<string>();

        if (card.Spell is { } spell)
        {
            parts.Add("spell[" + string.Join("; ", spell.Effects.Select(e => e.ToString())) + "]");
            foreach (var mode in spell.Modes)
                parts.Add("mode[" + string.Join("; ", mode.Effects.Select(e => e.ToString())) + "]");
        }

        foreach (var t in card.Triggers)
            parts.Add("trig[" + t.Text + " => " + string.Join("; ", t.Effects.Select(e => e.ToString())) + "]");

        foreach (var a in card.Activated)
            parts.Add("act[" + string.Join("; ", a.Effects.Select(e => e.ToString())) + "]");

        foreach (var s in card.Statics)
            parts.Add("static[" + s.ToString() + "]");

        foreach (var r in card.Replacements)
            parts.Add("repl[" + r.ToString() + "]");

        return string.Join(" | ", parts);
    }
}
