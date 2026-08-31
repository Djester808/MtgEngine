using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class R2136Measure(ITestOutputHelper output)
{
    private const char NL = (char)10;
    private const RegexOptions O = RegexOptions.IgnoreCase;

    private static readonly (string Name, Regex Pattern)[] Shapes =
    [
        ("A1 until your next turn", new Regex(@"until your next turn", O)),
        ("A2 until the end of your next turn", new Regex(@"until the end of your next turn", O)),
        ("A3 until <player>'s next turn", new Regex(@"until that player's next turn|until its controller's next turn", O)),
        ("A4 until next untap step", new Regex(@"next untap step", O)),
        ("B1 for as long as", new Regex(@"for as long as", O)),
        ("B2 until ~ leaves the battlefield", new Regex(@"until .{0,30} leaves the battlefield", O)),
        ("C1 next spell you cast this turn", new Regex(@"next spell you cast this turn", O)),
        ("C2 the next time this turn", new Regex(@"the next time .{0,60}this turn", O)),
        ("C3 first spell you cast each turn", new Regex(@"first (spell|creature spell|card) you (cast|play) (each|this) turn", O)),
        ("D1 as though it had flash", new Regex(@"as though (it|they|that card|those cards|it were|they were).{0,40}flash", O)),
        ("D2 cast .. this turn as though", new Regex(@"cast .{0,80}this turn as though", O)),
        ("D3 any 'as though' + this turn", new Regex(@"as though.{0,80}\bthis turn\b|\bthis turn\b.{0,80}as though", O)),
        ("E1 once each turn", new Regex(@"once each turn|only once each turn", O)),
        ("E2 without paying its mana cost", new Regex(@"without paying its mana cost", O)),
        ("F1 attack tax with a duration", new Regex(@"(until your next turn|this turn).{0,160}can't (attack|block)|can't (attack|block).{0,160}(until your next turn)", O)),
        ("G1 any unread line naming a window", new Regex(@"until your next turn|for as long as|next untap step|the next spell you cast|until the end of your next turn", O)),
    ];

    private static readonly (Regex From, string To)[] Rewrites =
    [
        (new Regex(@"until the end of your next turn", O), "until end of turn"),
        (new Regex(@"until your next turn", O), "until end of turn"),
        (new Regex(@"until that player's next turn", O), "until end of turn"),
        (new Regex(@"until its controller's next turn", O), "until end of turn"),
        (new Regex(@"until its controller's next untap step", O), "until end of turn"),
        (new Regex(@"until your next untap step", O), "until end of turn"),
        (new Regex(@"for as long as [^.,""]+", O), "until end of turn"),
        (new Regex(@"until [A-Za-z~' ]{0,30} leaves the battlefield", O), "until end of turn"),
        (new Regex(@"the next spell you cast this turn", O), "spells you cast this turn"),
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
        var excision = new Dictionary<string, int>(StringComparer.Ordinal);
        var control = new Dictionary<string, int>(StringComparer.Ordinal);
        var substitute = new Dictionary<string, int>(StringComparer.Ordinal);
        var winners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);

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
                var sole = compiled.Unhandled.Count == hits.Count;
                if (sole)
                    soleCount[name] = soleCount.GetValueOrDefault(name) + 1;

                if (!samples.TryGetValue(name, out var list))
                    samples[name] = list = [];

                if (list.Count < 60)
                    list.Add($"{(sole ? "SOLE" : "PART")} | {card.Name} [{compiled.Unhandled.Count} short] :: {hits[0]}");

                if (Substituted(card) is { } rewritten && CardCompiler.Compile(rewritten).IsComplete)
                {
                    substitute[name] = substitute.GetValueOrDefault(name) + 1;
                    if (!winners.TryGetValue(name, out var won))
                        winners[name] = won = [];

                    won.Add(card.Name);
                }

                if (Recompiles(card, pattern))
                    excision[name] = excision.GetValueOrDefault(name) + 1;

                if (RecompilesDroppingOther(card, pattern))
                    control[name] = control.GetValueOrDefault(name) + 1;
            }
        }

        output.WriteLine($"complete={complete} of {corpus.Count}");

        foreach (var (name, _) in Shapes)
        {
            output.WriteLine(
                $"{name}: lines={lineCount.GetValueOrDefault(name)} cards={cardCount.GetValueOrDefault(name)}"
                + $" sole={soleCount.GetValueOrDefault(name)} excision={excision.GetValueOrDefault(name)}"
                + $" linecontrol={control.GetValueOrDefault(name)}"
                + $" substitute={substitute.GetValueOrDefault(name)}");
        }

        foreach (var (name, won) in winners)
            output.WriteLine($"WON {name}: {string.Join(" | ", won)}");

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

    private static bool RecompilesDroppingOther(CardDefinition card, Regex drop)
    {
        if (card.Faces.Count > 1)
            return false;

        var lines = (card.OracleText ?? string.Empty).Split(NL).ToList();
        if (lines.Count < 2)
            return false;

        var index = lines.FindIndex(l => !drop.IsMatch(l));
        if (index < 0)
            return false;

        lines.RemoveAt(index);
        return CardCompiler.Compile(Retext(card, string.Join(NL, lines))).IsComplete;
    }

    private static CardDefinition? Substituted(CardDefinition card)
    {
        if (card.Faces.Count > 1)
            return null;

        var text = card.OracleText ?? string.Empty;
        var before = text;

        foreach (var (from, to) in Rewrites)
            text = from.Replace(text, to);

        return string.Equals(text, before, StringComparison.Ordinal) ? null : Retext(card, text);
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
