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
        var substitute = new Dictionary<string, int>(StringComparer.Ordinal);
        var winners = new Dictionary<string, List<string>>(StringComparer.Ordinal);

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

                if (Substituted(card) is { } rewritten && CardCompiler.Compile(rewritten).IsComplete)
                {
                    substitute[name] = substitute.GetValueOrDefault(name) + 1;
                    if (!winners.TryGetValue(name, out var won))
                        winners[name] = won = [];

                    won.Add(card.Name);
                }

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
                + $" control={control.GetValueOrDefault(name)}"
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
    private static readonly (Regex From, string To)[] Rewrites =
    [
        (new Regex(@"pays? \{1\} for each [^.""]+", RegexOptions.IgnoreCase), "pays {1}"),
        (new Regex(@"pays? \{[2-9]\} for each [^.""]+", RegexOptions.IgnoreCase), "pays {1}"),
        (new Regex(@"pays? \{X\}, where X is [^.]+", RegexOptions.IgnoreCase), "pays {1}"),
        (new Regex(@"pays? \{X\}", RegexOptions.IgnoreCase), "pays {1}"),
        (new Regex(@"costs \{X\} less to cast this way, where X is [^.]+", RegexOptions.IgnoreCase), "costs {1} less to cast for each creature you control"),
        (new Regex(@"costs \{X\} less to cast, where X is [^.]+", RegexOptions.IgnoreCase), "costs {1} less to cast for each creature you control"),
        (new Regex(@"costs \{X\} less to cast", RegexOptions.IgnoreCase), "costs {1} less to cast for each creature you control"),
        (new Regex(@"costs \{X\} less to activate, where X is [^.]+", RegexOptions.IgnoreCase), "costs {1} less to activate for each creature you control"),
    ];

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
    [Fact]
    public void Dump()
    {
        var into = Environment.GetEnvironmentVariable("R2108_DUMP");
        if (into is null)
            return;

        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var lines = new List<string>(corpus.Count);

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            var parts = new List<string> { compiled.IsComplete ? "COMPLETE" : "SHORT" };

            foreach (var spell in new[] { compiled.Spell, compiled.Adventure, compiled.PreparedSpell })
            {
                if (spell is null)
                    continue;

                foreach (var e in spell.Effects)
                    parts.Add("spell:" + e);

                foreach (var mode in spell.Modes)
                {
                    foreach (var e in mode.Effects)
                        parts.Add("mode:" + e);
                }
            }

            foreach (var t in compiled.Triggers)
            {
                parts.Add("trigger:" + t.Text);
                foreach (var e in t.Effects)
                    parts.Add("  " + e);
            }

            foreach (var a in compiled.Activated)
            {
                parts.Add("activated:" + a.Text);
                foreach (var e in a.Effects)
                    parts.Add("  " + e);
            }

            foreach (var st in compiled.Statics)
                parts.Add("static:" + st.Id);

            foreach (var u in compiled.Unhandled)
                parts.Add("unread:" + u);

            var text = string.Join(" ~~ ", parts).Replace((char)10, ' ').Replace((char)13, ' ');
            lines.Add(card.Name + "|" + text);
        }

        lines.Sort(StringComparer.Ordinal);
        File.WriteAllLines(into, lines);
    }
}
