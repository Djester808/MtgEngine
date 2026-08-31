using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Isolating a duration blocker from a verb blocker, one rewrite at a time.</summary>
public sealed class R2136Measure2(ITestOutputHelper output)
{
    private const char NL = (char)10;
    private const RegexOptions O = RegexOptions.IgnoreCase;

    private static readonly (string Name, Regex From, string To)[] Rewrites =
    [
        ("R0 control: rewrite nothing", new Regex(@"\bzzzznevermatches\b", O), "x"),
        ("R1 'until ~ leaves the battlefield' -> a held tail that reads",
            new Regex(@"until (~|this [a-z]+) leaves the battlefield", O),
            "for as long as ~ remains on the battlefield"),
        ("R2 drop the conjunction in a held tail",
            new Regex(@"for as long as you control (~|this [a-z]+) and (~|this [a-z]+) remains tapped", O),
            "for as long as ~ remains tapped"),
        ("R3 'until your next turn' -> 'until end of turn'",
            new Regex(@"until your next turn", O), "until end of turn"),
        ("R4 'until the end of your next turn' -> 'until end of turn'",
            new Regex(@"until the end of your next turn", O), "until end of turn"),
        ("R5 'until its controller's next untap step' -> 'until end of turn'",
            new Regex(@"until (its|their) controller's next untap step", O), "until end of turn"),
        ("R6 ANY held tail -> a held tail that reads",
            new Regex(@"for as long as [^.,""]+", O), "for as long as ~ remains on the battlefield"),
        ("R7 ANY held tail -> 'until end of turn'",
            new Regex(@"for as long as [^.,""]+", O), "until end of turn"),
        ("R8 held tail dropped entirely (excision of the clause only)",
            new Regex(@" for as long as [^.,""]+", O), string.Empty),
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
        var won = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var touched = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                complete++;
                continue;
            }

            if (card.Faces.Count > 1)
                continue;

            var text = card.OracleText ?? string.Empty;

            foreach (var (name, from, to) in Rewrites)
            {
                var rewritten = from.Replace(text, to);
                if (string.Equals(rewritten, text, StringComparison.Ordinal))
                    continue;

                touched[name] = touched.GetValueOrDefault(name) + 1;

                if (!CardCompiler.Compile(Retext(card, rewritten)).IsComplete)
                    continue;

                if (!won.TryGetValue(name, out var list))
                    won[name] = list = [];

                list.Add(card.Name);
            }

            // The line control: drop one line the rewrites never touch.
            var lines = text.Split(NL).ToList();
            if (lines.Count >= 2)
            {
                var index = lines.FindIndex(l =>
                    !l.Contains("for as long as", StringComparison.OrdinalIgnoreCase)
                    && !l.Contains("next turn", StringComparison.OrdinalIgnoreCase)
                    && !l.Contains("leaves the battlefield", StringComparison.OrdinalIgnoreCase));

                if (index >= 0)
                {
                    var kept = lines.ToList();
                    kept.RemoveAt(index);
                    if (CardCompiler.Compile(Retext(card, string.Join(NL, kept))).IsComplete)
                    {
                        if (!won.TryGetValue("LC line control", out var list))
                            won["LC line control"] = list = [];

                        list.Add(card.Name);
                    }

                    touched["LC line control"] = touched.GetValueOrDefault("LC line control") + 1;
                }
            }
        }

        output.WriteLine($"complete={complete} of {corpus.Count}");

        foreach (var name in Rewrites.Select(r => r.Name).Append("LC line control"))
        {
            var list = won.GetValueOrDefault(name) ?? [];
            output.WriteLine(
                $"{name}: touched={touched.GetValueOrDefault(name)} completed={list.Count}");
            if (list.Count > 0)
                output.WriteLine("    " + string.Join(" | ", list));
        }
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
