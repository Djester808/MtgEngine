using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

public sealed class ZzR2109CensusTests
{
    private static readonly string Nl = ((char)10).ToString();

    private static string Out(string name)
    {
        var dir = Environment.GetEnvironmentVariable("R2109_OUT") ?? ".";
        return Path.Combine(dir, name);
    }

    [Fact]
    public void Prevention_lines_still_unread()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var rows = new List<string>();
        var complete = 0;
        var effects = new List<string>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                complete++;
                effects.Add("## " + card.Name + " :: " + Describe(compiled));
                continue;
            }

            if (compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];
            if (line.Contains("prevent", StringComparison.OrdinalIgnoreCase)
                && line.Contains("damage", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add("@@ " + card.Name + " :: " + line.Replace(Nl, " | "));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        effects.Sort(StringComparer.Ordinal);

        File.WriteAllLines(
            Out("prevention-sole-blockers.txt"),
            new[] { "SOLE-BLOCKER PREVENTION LINES: " + rows.Count }.Concat(rows));

        File.WriteAllLines(
            Out("complete-effects.txt"),
            new[] { "COMPLETE: " + complete + " of " + corpus.Count }.Concat(effects));
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

    [Fact]
    public void Probe_named_cards()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var wanted = (Environment.GetEnvironmentVariable("R2109_CARDS") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>();

        foreach (var card in corpus.Where(c => wanted.Contains(c.Name)))
        {
            var compiled = CardCompiler.Compile(card);
            lines.Add("=== " + card.Name + "  complete=" + compiled.IsComplete);
            lines.Add("    text: " + (card.OracleText ?? string.Empty).Replace(Nl, " | "));
            foreach (var u in compiled.Unhandled)
                lines.Add("    UNREAD: " + u.Replace(Nl, " | "));
            lines.Add("    EFFECTS: " + Describe(compiled));
        }

        File.WriteAllLines(Out("probe-cards.txt"), lines);
    }
}
