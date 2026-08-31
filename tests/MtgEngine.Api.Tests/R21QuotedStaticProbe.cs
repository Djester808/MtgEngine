using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class R21QuotedStaticProbe(ITestOutputHelper output)
{
    private const string Readable = "{T}: Add {G}.";

    private static CompiledCard CompileText(CardDefinition card, string text) =>
        CardCompiler.Compile(new CardDefinition
        {
            OracleId = card.OracleId,
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            OracleText = text,
            Power = card.Power,
            Toughness = card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Keywords = card.Keywords,
            Colors = card.Colors,
            ColorIdentity = card.ColorIdentity,
        });

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
        var staticOnly = 0;
        var frameReads = 0;
        var frameReadsSelf = 0;
        var frameBlocked = 0;
        var winFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        var winners = new List<string>();
        var selfWinners = new List<string>();
        var lostFrames = new Dictionary<string, int>(StringComparer.Ordinal);

        // The two families round nineteen listed, measured as sole blockers.
        var crews = 0;
        var spirits = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete || compiled.Unhandled.Count != 1)
                continue;

            var line = compiled.Unhandled[0];

            if (line.Contains("as though its power were", StringComparison.Ordinal))
                crews++;

            if (line.Contains("or be blocked by non-", StringComparison.Ordinal))
                spirits++;

            var quotes = quoteSpan.Matches(line);
            if (quotes.Count == 0)
                continue;

            oneShortQuoted++;

            var allStatic = true;
            var allSelf = true;
            foreach (Match q in quotes)
            {
                var inner = q.Groups[1].Value.Trim();
                if (!StaticOnly(CompileQuoted(inner)))
                {
                    allStatic = false;
                    break;
                }

                if (!inner.StartsWith("~", StringComparison.Ordinal))
                    allSelf = false;
            }

            if (!allStatic)
                continue;

            staticOnly++;

            // Swap a known-readable ability into every quotation on the card. If the card then
            // completes, the frame reads and the quotation is the whole blocker.
            var swapped = quoteSpan.Replace(card.OracleText, "\u0022" + Readable + "\u0022");
            var frame = quoteSpan.Replace(line, "\u0022Q\u0022");

            if (CompileText(card, swapped).IsComplete)
            {
                frameReads++;
                winFrames[frame] = winFrames.GetValueOrDefault(frame) + 1;
                winners.Add(card.Name + "  ||  " + line);
                if (allSelf)
                {
                    frameReadsSelf++;
                    selfWinners.Add(card.Name);
                }
            }
            else
            {
                frameBlocked++;
                lostFrames[frame] = lostFrames.GetValueOrDefault(frame) + 1;
            }
        }

        output.WriteLine($"one-line-short cards whose blocker carries a quotation: {oneShortQuoted}");
        output.WriteLine($"  every quoted span compiles to statics alone:          {staticOnly}");
        output.WriteLine($"    ... and the frame reads with a readable ability in: {frameReads}");
        output.WriteLine($"        ... of which every span's subject is '~':       {frameReadsSelf}");
        output.WriteLine($"    ... frame blocked too:                              {frameBlocked}");
        output.WriteLine("");
        output.WriteLine($"sole-blocker lines saying 'as though its power were': {crews}");
        output.WriteLine($"sole-blocker lines saying \"or be blocked by non-\":   {spirits}");

        output.WriteLine("");
        output.WriteLine("frames the quotation alone blocks:");
        foreach (var (frame, n) in winFrames.OrderByDescending(p => p.Value))
            output.WriteLine($"  {n,4}  {frame}");

        output.WriteLine("");
        output.WriteLine("cards the quotation alone blocks:");
        foreach (var w in winners)
            output.WriteLine("  " + w);

        output.WriteLine("");
        output.WriteLine("of those, self-subject ones: " + string.Join(", ", selfWinners));

        output.WriteLine("");
        output.WriteLine("frames blocked as well:");
        foreach (var (frame, n) in lostFrames.OrderByDescending(p => p.Value).Take(20))
            output.WriteLine($"  {n,4}  {frame}");
    }
}
