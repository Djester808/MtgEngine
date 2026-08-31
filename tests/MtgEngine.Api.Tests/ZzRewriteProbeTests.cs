// One-off measurement probe for round twenty-one, branch r21-43. Reads rewrite.tsv
// (tag, oracleId, unit, replacement) from $env:FRAME_DIR and writes rewrite-out.tsv
// (tag, oracleId, name, ok). An empty replacement is a plain excision. Convert to CRLF
// before dropping it in tests/MtgEngine.Api.Tests/, and delete it before committing.
//   FRAME_DIR=<dir> dotnet test tests/MtgEngine.Api.Tests --no-build --filter ZzRewriteProbeTests
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class ZzRewriteProbeTests(ITestOutputHelper output)
{
    private static CardDefinition WithText(CardDefinition card, string text) =>
        new()
        {
            OracleId = card.OracleId,
            Name = card.Name,
            OracleText = text,
            CardTypes = card.CardTypes,
            Keywords = card.Keywords,
            ColorIdentity = card.ColorIdentity,
            Colors = card.Colors,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            Power = card.Power,
            Toughness = card.Toughness,
            Defense = card.Defense,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            StartingLoyalty = card.StartingLoyalty,
            Faces = [],
        };

    private static bool Reads(CardDefinition card, List<string> lines, string unit, string replacement)
    {
        var kept = new List<string>();
        var hit = false;
        foreach (var line in lines)
        {
            var at = line.IndexOf(unit, StringComparison.Ordinal);
            var rest = line;
            if (at >= 0)
            {
                rest = line.Remove(at, unit.Length).Insert(at, replacement);
                hit = true;
            }

            rest = rest.Trim();
            if (rest.Length > 0)
                kept.Add(rest);
        }

        return hit && CardCompiler.Compile(WithText(card, string.Join("\n", kept))).IsComplete;
    }

    [Fact]
    public void Rewrite()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var dir = Environment.GetEnvironmentVariable("FRAME_DIR") ?? Path.GetTempPath();
        var jobs = new Dictionary<string, List<(string Tag, string Unit, string Replacement)>>(StringComparer.Ordinal);
        foreach (var row in File.ReadLines(Path.Combine(dir, "rewrite.tsv")).Skip(1))
        {
            var p = row.Split('\t');
            if (p.Length < 3)
                continue;
            if (!jobs.TryGetValue(p[1], out var list))
                jobs[p[1]] = list = [];
            list.Add((p[0], p[2], p.Length > 3 ? p[3] : string.Empty));
        }

        using var w = new StreamWriter(Path.Combine(dir, "rewrite-out.tsv"));
        w.Write("tag\toracleId\tname\tok\n");

        var done = 0;
        foreach (var card in corpus!.Where(c => jobs.ContainsKey(c.OracleId))
                     .OrderBy(c => c.OracleId, StringComparer.Ordinal))
        {
            var lines = CardCompiler.Lines(card).ToList();
            foreach (var job in jobs[card.OracleId])
            {
                var ok = Reads(card, lines, job.Unit, job.Replacement);
                w.Write(job.Tag + "\t" + card.OracleId + "\t" + card.Name + "\t" + (ok ? "1" : "0") + "\n");
            }

            done++;
        }

        output.WriteLine($"rewrote {done} cards -> {dir}");
    }
}
