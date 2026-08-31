// Round twenty's corrected excision census. Drop into tests/MtgEngine.Api.Tests/ (convert to
// CRLF first - the analysers fail the build on LF), build, then:
//   FRAME_DIR=<dir> dotnet test tests/MtgEngine.Api.Tests --no-build --filter ZzFrameCensusTests
// Writes frame-cards.tsv, frame-units.tsv, frame-residual.tsv. Delete the file before committing.
//
// The correction it embodies: excise from CardCompiler.Lines(card) - the compiler's own
// normalised text, which is where CompiledCard.Unhandled comes from - and NOT from the raw
// OracleText. Cutting from raw text is a silent no-op on every self-referential card.
using System.Text;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class ZzFrameCensusTests(ITestOutputHelper output)
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

    /// <summary>Sentences of a line, not cutting inside a quoted granted ability.</summary>
    private static List<string> Sentences(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
                quoted = !quoted;

            sb.Append(c);

            if (!quoted && c == '.' && (i + 1 >= line.Length || line[i + 1] == ' '))
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }

                outp.Add(sb.ToString().Trim());
                sb.Clear();
                i++;
            }
        }

        var tail = sb.ToString().Trim();
        if (tail.Length > 0)
            outp.Add(tail);

        return outp.Where(s => s.Length > 1).ToList();
    }

    [Fact]
    public void Census()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var dir = Environment.GetEnvironmentVariable("FRAME_DIR") ?? Path.GetTempPath();
        using var cards = new StreamWriter(Path.Combine(dir, "frame-cards.tsv"));
        using var units = new StreamWriter(Path.Combine(dir, "frame-units.tsv"));
        using var resid = new StreamWriter(Path.Combine(dir, "frame-residual.tsv"));

        cards.Write("oracleId\tname\tfaces\tlines\tunread\tunits\tcut\tallCut\tsole\ttypes\n");
        units.Write("oracleId\tname\tsole\tinLine\tunit\n");
        resid.Write("oracleId\tname\tfaces\tcutText\tresidual\tbefore\n");

        var complete = 0;
        var incomplete = 0;
        var frame = 0;
        var sentence = 0;
        var noCut = 0;

        foreach (var card in corpus!.OrderBy(c => c.OracleId, StringComparer.Ordinal))
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                complete++;
                continue;
            }

            incomplete++;
            var lines = CardCompiler.Lines(card).ToList();
            var unread = compiled.Unhandled.ToList();

            var cardUnits = new List<string>();
            foreach (var u in unread)
            {
                foreach (var s in Sentences(u))
                {
                    if (!cardUnits.Contains(s, StringComparer.Ordinal))
                        cardUnits.Add(s);
                }
            }

            var kept = new List<string>();
            var cutAnything = false;
            foreach (var line in lines)
            {
                var rest = line;
                foreach (var u in cardUnits)
                {
                    var at = rest.IndexOf(u, StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        rest = rest.Remove(at, u.Length);
                        cutAnything = true;
                        at = rest.IndexOf(u, StringComparison.Ordinal);
                    }
                }

                rest = rest.Trim();
                if (rest.Length > 0)
                    kept.Add(rest);
            }

            var cutCompiled = CardCompiler.Compile(WithText(card, string.Join("\n", kept)));
            var allCut = cutCompiled.IsComplete;

            if (!cutAnything)
                noCut++;

            if (allCut)
                sentence++;
            else
                frame++;

            var sole = 0;
            foreach (var u in cardUnits)
            {
                var one = new List<string>();
                var found = false;
                foreach (var line in lines)
                {
                    var rest = line;
                    var at = rest.IndexOf(u, StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        rest = rest.Remove(at, u.Length);
                        found = true;
                        at = rest.IndexOf(u, StringComparison.Ordinal);
                    }

                    rest = rest.Trim();
                    if (rest.Length > 0)
                        one.Add(rest);
                }

                var isSole = found && CardCompiler.Compile(WithText(card, string.Join("\n", one))).IsComplete;
                if (isSole)
                    sole++;

                units.Write(card.OracleId + "\t" + card.Name + "\t" + (isSole ? 1 : 0) + "\t"
                    + (found ? 1 : 0) + "\t" + u.Replace('\t', ' ') + "\n");
            }

            cards.Write(card.OracleId + "\t" + card.Name + "\t" + card.Faces.Count + "\t" + lines.Count
                + "\t" + unread.Count + "\t" + cardUnits.Count + "\t" + (cutAnything ? 1 : 0)
                + "\t" + (allCut ? 1 : 0) + "\t" + sole + "\t" + card.CardTypes + "\n");

            if (!allCut)
            {
                resid.Write(card.OracleId + "\t" + card.Name + "\t" + card.Faces.Count + "\t"
                    + string.Join(" ~/~ ", kept).Replace('\t', ' ') + "\t"
                    + string.Join(" ~/~ ", cutCompiled.Unhandled).Replace('\t', ' ') + "\t"
                    + string.Join(" ~/~ ", unread).Replace('\t', ' ') + "\n");
            }
        }

        output.WriteLine($"corpus {corpus.Count} complete {complete} incomplete {incomplete}");
        output.WriteLine($"sentence-blocked {sentence}; frame-blocked {frame}; nothing cut {noCut}");
    }
}
