using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class R2108Measure(ITestOutputHelper output)
{
    private const char NL = (char)10;

    private static readonly (string Name, Regex Pattern)[] Shapes =
    [
        ("A unless pays {X}", new Regex(@"unless (its controller|that player|they) pays \{X\}", RegexOptions.IgnoreCase)),
        ("B unless pays {N} for each", new Regex(@"unless (its controller|that player|they|you) pays? \{\d+\} for each", RegexOptions.IgnoreCase)),
        ("C attack tax for each", new Regex(@"can't (attack|block).*unless.*pays \{\d+\} for each", RegexOptions.IgnoreCase)),
        ("D spell costs {X} less", new Regex(@"costs \{X\} less to cast", RegexOptions.IgnoreCase)),
        ("E ability costs {X} less", new Regex(@"costs \{X\} less to activate", RegexOptions.IgnoreCase)),
        ("F coloured reduction", new Regex(@"costs \{[^}]*\}\{[^}]*\} less to (cast|activate)", RegexOptions.IgnoreCase)),
        ("G any unless-pay", new Regex(@"\bunless\b.*\bpays?\b", RegexOptions.IgnoreCase)),
        ("H any costs-less", new Regex(@"costs \{[^}]+\} less", RegexOptions.IgnoreCase)),
    ];

    [Fact]
    public void Measure()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var complete = 0;
        var lineCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var cardCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var soleCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var excision = new Dictionary<string, int>(StringComparer.Ordinal);
        var control = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                complete++;
                continue;
            }

            foreach (var (name, pattern) in Shapes)
            {
                var hits = compiled.Unhandled.Where(l => pattern.IsMatch(l)).ToList();
                if (hits.Count == 0)
                    continue;

                lineCount[name] = lineCount.GetValueOrDefault(name) + hits.Count;
                cardCount[name] = cardCount.GetValueOrDefault(name) + 1;
                if (compiled.Unhandled.Count == hits.Count)
                    soleCount[name] = soleCount.GetValueOrDefault(name) + 1;

                if (!samples.TryGetValue(name, out var list))
                    samples[name] = list = [];

                if (list.Count < 45)
                    list.Add($"{card.Name} [{compiled.Unhandled.Count} short] :: {hits[0]}");

                if (Recompiles(card, pattern))
                    excision[name] = excision.GetValueOrDefault(name) + 1;

                if (RecompilesDroppingFirst(card))
                    control[name] = control.GetValueOrDefault(name) + 1;
            }
        }

        output.WriteLine($"complete={complete} of {corpus.Count}");

        foreach (var (name, _) in Shapes)
        {
            output.WriteLine(
                $"{name}: lines={lineCount.GetValueOrDefault(name)} cards={cardCount.GetValueOrDefault(name)}"
                + $" sole={soleCount.GetValueOrDefault(name)} excision={excision.GetValueOrDefault(name)}"
                + $" control={control.GetValueOrDefault(name)}");
        }

        foreach (var (name, list) in samples)
        {
            output.WriteLine($"---- {name} ----");
            foreach (var s in list)
                output.WriteLine("  " + s);
        }
    }

    private static bool Recompiles(CardDefinition card, Regex drop)
    {
        if (card.Faces.Count > 1)
            return false;

        var kept = (card.OracleText ?? string.Empty).Split(NL).Where(l => !drop.IsMatch(l)).ToList();
        return CardCompiler.Compile(Retext(card, string.Join(NL, kept))).IsComplete;
    }

    private static bool RecompilesDroppingFirst(CardDefinition card)
    {
        if (card.Faces.Count > 1)
            return false;

        var lines = (card.OracleText ?? string.Empty).Split(NL).ToList();
        if (lines.Count < 2)
            return false;

        lines.RemoveAt(0);
        return CardCompiler.Compile(Retext(card, string.Join(NL, lines))).IsComplete;
    }

    private static CardDefinition Retext(CardDefinition card, string text) => new()
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
        Defense = card.Defense,
        Keywords = card.Keywords,
        ColorIdentity = card.ColorIdentity,
        Colors = card.Colors,
        Faces = card.Faces,
    };
}
