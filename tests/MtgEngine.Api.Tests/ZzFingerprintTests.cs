// A fingerprint of every card's compiled abilities, for a set diff AND an effect diff.
// Complete/incomplete alone hides the cards that gained or *changed* an ability without
// completing, which is where a silently wrong reading shows up.
//   FP_OUT=<file> dotnet test tests/MtgEngine.Api.Tests --no-build --filter ZzFingerprintTests
// Run once before the change and once after, then diff on oracleId. Convert to CRLF before
// putting it in tests/MtgEngine.Api.Tests/, and delete it before committing.
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class ZzFingerprintTests(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var to = Environment.GetEnvironmentVariable("FP_OUT")
            ?? Path.Combine(Path.GetTempPath(), "fp.tsv");

        var complete = 0;
        using var writer = new StreamWriter(to);

        foreach (var card in corpus!.OrderBy(c => c.OracleId, StringComparer.Ordinal))
        {
            var c = CardCompiler.Compile(card);
            if (c.IsComplete)
                complete++;

            var parts = new List<string>();
            if (c.Spell is { } spell)
                parts.Add("spell[" + string.Join(",", spell.Effects.Select(e => e.GetType().Name)) + "]");

            foreach (var t in c.Triggers.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                parts.Add("trg(" + t.Id + (t.OncePerTurn ? "|1x" : string.Empty)
                    + (t.SubjectIsSource ? "|self" : string.Empty)
                    + (t.PerDeclaredCreature ? "|per" : string.Empty)
                    + ")[" + string.Join(",", t.Effects.Select(e => e.GetType().Name)) + "]");
            }

            foreach (var a in c.Activated.OrderBy(a => a.Id, StringComparer.Ordinal))
                parts.Add("act(" + a.Id + ")[" + string.Join(",", a.Effects.Select(e => e.GetType().Name)) + "]");

            foreach (var s in c.Statics.OrderBy(s => s.Id, StringComparer.Ordinal))
                parts.Add("st(" + s.Id + ")");

            foreach (var r in c.Replacements.OrderBy(r => r.Id, StringComparer.Ordinal))
                parts.Add("rp(" + r.Id + ")");

            foreach (var q in c.PlayerQualities.OrderBy(q => q.Id, StringComparer.Ordinal))
                parts.Add("pq(" + q.Id + ")");

            writer.Write(card.OracleId + "\t" + card.Name + "\t" + (c.IsComplete ? "1" : "0")
                + "\t" + string.Join(" ~~ ", parts) + "\n");
        }

        output.WriteLine($"corpus {corpus.Count}, complete {complete} -> {to}");
    }
}
