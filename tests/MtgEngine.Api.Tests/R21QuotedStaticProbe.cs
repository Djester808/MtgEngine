using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class R21QuotedStaticProbe(ITestOutputHelper output)
{
    private static CompiledCard CompileQuoted(string text) => CardCompiler.Compile(new CardDefinition
    {
        OracleId = "probe",
        Name = "~",
        OracleText = text,
        CardTypes = MtgEngine.Domain.Enums.CardType.Enchantment,
    });

    private static bool StaticOnly(CompiledCard c) =>
        c.Unhandled.IsEmpty
        && !c.Statics.IsEmpty
        && c.Activated.IsEmpty
        && c.Triggers.IsEmpty
        && c.Spell is null
        && c.Replacements.IsEmpty;

    [Fact]
    public void Probe()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var quoteSpan = new Regex("\u0022([^\u0022]+)\u0022", RegexOptions.None, TimeSpan.FromMilliseconds(200));

        var oneShortQuoted = 0;
        var innerStaticOnly = 0;
        var innerStaticSelf = 0;
        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        var selfFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        var innerShapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<string>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];
            var quotes = quoteSpan.Matches(line);
            if (quotes.Count == 0)
                continue;

            oneShortQuoted++;

            var allStatic = quotes.Count > 0;
            var allSelf = true;
            foreach (Match q in quotes)
            {
                var inner = q.Groups[1].Value.Trim();
                var c = CompileQuoted(inner);
                if (!StaticOnly(c))
                {
                    allStatic = false;
                    break;
                }

                if (!inner.StartsWith("~", StringComparison.Ordinal))
                    allSelf = false;
            }

            if (!allStatic)
                continue;

            innerStaticOnly++;

            // The frame with the quotations blanked out.
            var frame = quoteSpan.Replace(line, "\u0022Q\u0022");
            frames[frame] = frames.GetValueOrDefault(frame) + 1;

            foreach (Match q in quotes)
            {
                var inner = q.Groups[1].Value.Trim();
                innerShapes[inner] = innerShapes.GetValueOrDefault(inner) + 1;
            }

            if (allSelf)
            {
                innerStaticSelf++;
                selfFrames[frame] = selfFrames.GetValueOrDefault(frame) + 1;
                if (examples.Count < 40)
                    examples.Add(card.Name + "  ||  " + line);
            }
        }

        output.WriteLine($"one-line-short cards whose blocker carries a quotation: {oneShortQuoted}");
        output.WriteLine($"  ... every quoted span compiles to statics alone: {innerStaticOnly}");
        output.WriteLine($"  ... and every span's subject is '~': {innerStaticSelf}");

        output.WriteLine("");
        output.WriteLine("frames (static-only inner), by cards:");
        foreach (var (frame, n) in frames.OrderByDescending(p => p.Value).Take(30))
            output.WriteLine($"  {n,5}  {frame}");

        output.WriteLine("");
        output.WriteLine("frames (self '~' inner only), by cards:");
        foreach (var (frame, n) in selfFrames.OrderByDescending(p => p.Value).Take(30))
            output.WriteLine($"  {n,5}  {frame}");

        output.WriteLine("");
        output.WriteLine("inner static texts, by cards:");
        foreach (var (inner, n) in innerShapes.OrderByDescending(p => p.Value).Take(40))
            output.WriteLine($"  {n,5}  {inner}");

        output.WriteLine("");
        output.WriteLine("examples:");
        foreach (var e in examples)
            output.WriteLine("  " + e);
    }
}
