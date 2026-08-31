using System.Globalization;
using System.Text;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class R21Dump(ITestOutputHelper output)
{
    private static string Fingerprint(CompiledCard c)
    {
        var sb = new StringBuilder();

        if (c.Spell is not null)
        {
            sb.Append("spell[");
            foreach (var e in c.Spell.Effects)
                sb.Append(e).Append('|');
            sb.Append("] ");
        }

        foreach (var a in c.Activated)
            sb.Append("act:").Append(a.Id).Append('=').Append(a.Text).Append(' ');

        foreach (var t in c.Triggers)
            sb.Append("trg:").Append(t.Id).Append(' ');

        foreach (var s in c.Statics)
            sb.Append("sta:").Append(s.Id).Append('@').Append(s.Layer).Append(' ');

        foreach (var r in c.Replacements)
            sb.Append("rep:").Append(r.Id).Append(' ');

        foreach (var q in c.PlayerQualities)
            sb.Append("pq:").Append(q.Id).Append(' ');

        foreach (var u in c.Unhandled)
            sb.Append("un:").Append(u).Append(' ');

        return sb.ToString().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    [Fact]
    public void Dump()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var target = Environment.GetEnvironmentVariable("R21_DUMP")
            ?? Path.Combine(Path.GetTempPath(), "r21-dump.tsv");

        var complete = 0;
        var lines = 0;
        var readLines = 0;

        using var writer = new StreamWriter(target, false, new UTF8Encoding(false));
        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
                complete++;

            var total = CardCompiler.Lines(card).Count();
            lines += total;
            readLines += total - compiled.Unhandled.Count;

            writer.Write(card.OracleId);
            writer.Write('\t');
            writer.Write(card.Name.Replace('\t', ' '));
            writer.Write('\t');
            writer.Write(compiled.IsComplete ? "1" : "0");
            writer.Write('\t');
            writer.Write(Fingerprint(compiled));
            writer.Write('\n');
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"cards {corpus.Count} complete {complete} lines {lines} read {readLines}"));
        output.WriteLine("wrote " + target);
    }
}
