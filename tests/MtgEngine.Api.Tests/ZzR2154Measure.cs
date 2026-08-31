using System.Globalization;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Scratch measurement for round twenty-one's Attraction/specialize remainders. Asserts nothing.</summary>
public sealed class ZzR2154Measure(ITestOutputHelper output)
{
    private static CardDefinition Rewritten(CardDefinition card, string text) => new()
    {
        OracleId = card.OracleId + "#c" + text.GetHashCode(StringComparison.Ordinal)
            .ToString(CultureInfo.InvariantCulture),
        Name = card.Name,
        OracleText = text,
        ManaCostRaw = card.ManaCostRaw,
        Cmc = card.Cmc,
        CardTypes = card.CardTypes,
        Subtypes = card.Subtypes,
        Supertypes = card.Supertypes,
        Keywords = card.Keywords,
        Colors = card.Colors,
        ColorIdentity = card.ColorIdentity,
        Power = card.Power,
        Toughness = card.Toughness,
        Defense = card.Defense,
        AttractionLights = card.AttractionLights,
        Faces = card.Faces,
        Specializations = card.Specializations,
    };

    /// <summary>The same card with each version's text replaced by the matching entry.</summary>
    private static CardDefinition WithVersions(CardDefinition card, IReadOnlyList<string> texts)
    {
        var swapped = new List<CardFace>(card.Specializations.Count);
        for (var i = 0; i < card.Specializations.Count; i++)
        {
            swapped.Add(i == 0 || i >= texts.Count
                ? card.Specializations[i]
                : card.Specializations[i] with { OracleText = texts[i] });
        }

        return new CardDefinition
        {
            OracleId = card.OracleId + "#v" + string.Concat(texts).GetHashCode(StringComparison.Ordinal)
                .ToString(CultureInfo.InvariantCulture),
            Name = card.Name,
            OracleText = card.OracleText,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            Keywords = card.Keywords,
            Colors = card.Colors,
            ColorIdentity = card.ColorIdentity,
            Power = card.Power,
            Toughness = card.Toughness,
            Defense = card.Defense,
            AttractionLights = card.AttractionLights,
            Faces = card.Faces,
            Specializations = swapped,
        };
    }

    private static bool AttractionFamily(CardDefinition card) =>
        card.Subtypes.Contains("Attraction", StringComparer.OrdinalIgnoreCase)
        || (card.OracleText ?? string.Empty).Contains("Attraction", StringComparison.OrdinalIgnoreCase);

    private static bool VisitedTally(string line) =>
        line.Contains("visited", StringComparison.OrdinalIgnoreCase);

    private static bool Prize(string line) =>
        line.Contains("prize", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Attraction_remainders()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        { output.WriteLine("no corpus"); return; }

        var compiled = corpus.Select(c => (Card: c, Result: CardCompiler.Compile(c))).ToList();
        output.WriteLine($"CONTROL corpus {corpus.Count} complete {compiled.Count(c => c.Result.IsComplete)}");

        var incomplete = compiled.Where(c => AttractionFamily(c.Card) && !c.Result.IsComplete).ToList();
        output.WriteLine($"attraction family incomplete: {incomplete.Count}");

        foreach (var (name, pick) in new (string, Func<string, bool>)[]
        {
            ("visited-this-turn", VisitedTally),
            ("prize", Prize),
        })
        {
            int carriers = 0, excised = 0, substituted = 0, lineCtl = 0, lineCtlEligible = 0;
            var names = new List<string>();

            foreach (var (card, result) in incomplete)
            {
                var whole = CardCompiler.Lines(card).ToList();
                var mine = result.Unhandled.Where(l => pick(l)).ToList();
                var others = result.Unhandled.Where(l => !pick(l)).ToList();

                if (mine.Count == 0)
                    continue;

                carriers++;
                names.Add($"{card.Name} [{mine.Count} mine / {others.Count} other]");

                var cut = string.Join("\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
                if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                    excised++;

                var swap = string.Join("\n", whole.Select(l =>
                    mine.Contains(l, StringComparer.Ordinal) ? "Draw a card." : l));
                if (CardCompiler.Compile(Rewritten(card, swap)).IsComplete)
                    substituted++;

                if (others.Count > 0)
                {
                    lineCtlEligible++;
                    var dropped = string.Join("\n",
                        whole.Where(l => !string.Equals(l, others[0], StringComparison.Ordinal)));
                    if (CardCompiler.Compile(Rewritten(card, dropped)).IsComplete)
                        lineCtl++;
                }
            }

            output.WriteLine($"--- {name}: carriers {carriers}");
            foreach (var n in names)
                output.WriteLine("     " + n);
            output.WriteLine($"    EXCISION {excised}  SUBSTITUTION {substituted}  LINE-CONTROL {lineCtl}/{lineCtlEligible}");
        }

        output.WriteLine("--- every still-short attraction card:");
        foreach (var (card, result) in incomplete.OrderBy(c => c.Card.Name, StringComparer.Ordinal))
            output.WriteLine($"  {card.Name} :: {string.Join(" | ", result.Unhandled)}");
    }

    [Fact]
    public void Specialize_remainder()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        { output.WriteLine("no corpus"); return; }

        var family = corpus.Where(c => c.Specializations.Count == 6).ToList();
        var compiled = family.Select(c => (Card: c, Result: CardCompiler.Compile(c))).ToList();
        output.WriteLine($"specialize cards {family.Count} complete {compiled.Count(c => c.Result.IsComplete)}");

        var totalVersionLines = 0;
        var totalBaseLines = 0;

        foreach (var (card, result) in compiled.OrderBy(c => c.Card.Name, StringComparer.Ordinal))
        {
            var baseUnread = CardCompiler.Compile(CardFaces.Specialized(card, 0)).Unhandled;
            totalBaseLines += baseUnread.Count;
            output.WriteLine($"== {card.Name}  unread {result.Unhandled.Count} (base {baseUnread.Count})");
            foreach (var l in baseUnread)
                output.WriteLine("    BASE  " + l);
            for (var v = 1; v < 6; v++)
            {
                var version = CardFaces.Specialized(card, v);
                var vr = CardCompiler.Compile(version);
                totalVersionLines += vr.Unhandled.Count;
                foreach (var l in vr.Unhandled)
                    output.WriteLine($"    V{v} [{version.Name}]  {l}");
            }
        }

        output.WriteLine($"TOTAL base-unread {totalBaseLines}  version-unread {totalVersionLines}");

        var ifVersionsRead = 0;
        foreach (var (card, _) in compiled)
        {
            var texts = new List<string> { string.Empty };
            for (var v = 1; v < 6; v++)
            {
                var version = CardFaces.Specialized(card, v);
                var unread = CardCompiler.Compile(version).Unhandled;
                texts.Add(string.Join("\n", CardCompiler.Lines(version)
                    .Where(l => !unread.Contains(l, StringComparer.Ordinal))));
            }

            if (CardCompiler.Compile(WithVersions(card, texts)).IsComplete)
                ifVersionsRead++;
        }

        output.WriteLine($"EXCISION if every version read: {ifVersionsRead} of {family.Count}");

        var ifVersionsDraw = 0;
        foreach (var (card, _) in compiled)
        {
            var texts = new List<string> { string.Empty };
            for (var v = 1; v < 6; v++)
            {
                var version = CardFaces.Specialized(card, v);
                var unread = CardCompiler.Compile(version).Unhandled;
                texts.Add(string.Join("\n", CardCompiler.Lines(version)
                    .Select(l => unread.Contains(l, StringComparer.Ordinal) ? "Draw a card." : l)));
            }

            if (CardCompiler.Compile(WithVersions(card, texts)).IsComplete)
                ifVersionsDraw++;
        }

        output.WriteLine($"SUBSTITUTION if every version line became a draw: {ifVersionsDraw} of {family.Count}");

        var freebies = 0;
        foreach (var (card, _) in compiled)
        {
            var texts = new List<string> { string.Empty };
            var dropped = false;
            for (var v = 1; v < 6; v++)
            {
                var version = CardFaces.Specialized(card, v);
                var unread = CardCompiler.Compile(version).Unhandled;
                var lines = CardCompiler.Lines(version).ToList();
                var readable = lines.Where(l => !unread.Contains(l, StringComparer.Ordinal)).ToList();
                if (!dropped && readable.Count > 0)
                {
                    lines.Remove(readable[0]);
                    dropped = true;
                }

                texts.Add(string.Join("\n", lines));
            }

            if (CardCompiler.Compile(WithVersions(card, texts)).IsComplete)
                freebies++;
        }

        output.WriteLine($"LINE CONTROL (one readable version line dropped): {freebies} of {family.Count}");
    }

    [Fact]
    public void Specialize_shapes()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        { output.WriteLine("no corpus"); return; }

        var family = corpus.Where(c => c.Specializations.Count == 6).ToList();

        (string Name, Func<string, bool> Pick)[] shapes =
        [
            ("skanos-gains-and-pumps", l => l.Contains("+X/+0 until end of turn, where X is ~'s power", StringComparison.OrdinalIgnoreCase)),
            ("perpetually", l => l.Contains("perpetual", StringComparison.OrdinalIgnoreCase)),
            ("one-time boon", l => l.Contains("one-time boon", StringComparison.OrdinalIgnoreCase)),
            ("seek", l => l.Contains("seek", StringComparison.OrdinalIgnoreCase)),
            ("conjure", l => l.Contains("conjure", StringComparison.OrdinalIgnoreCase)),
            ("specializes/unspecializes", l => l.Contains("specialize", StringComparison.OrdinalIgnoreCase)),
        ];

        foreach (var (name, pick) in shapes)
        {
            var lines = 0;
            var cards = new List<string>();
            var completes = new List<string>();

            foreach (var card in family)
            {
                var hit = false;
                var texts = new List<string> { string.Empty };
                for (var v = 1; v < 6; v++)
                {
                    var version = CardFaces.Specialized(card, v);
                    var unread = CardCompiler.Compile(version).Unhandled.Where(pick).ToList();
                    lines += unread.Count;
                    if (unread.Count > 0)
                        hit = true;
                    texts.Add(string.Join("\n", CardCompiler.Lines(version)
                        .Where(l => !unread.Contains(l, StringComparer.Ordinal))));
                }

                if (!hit)
                    continue;
                cards.Add(card.Name);
                if (CardCompiler.Compile(WithVersions(card, texts)).IsComplete)
                    completes.Add(card.Name);
            }

            output.WriteLine($"{name}: {lines} lines on {cards.Count} cards; excising them alone completes {completes.Count} [{string.Join(", ", completes)}]");
        }

        output.WriteLine("--- residual per card after stripping all version lines:");
        foreach (var card in family.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var texts = new List<string> { string.Empty };
            for (var v = 1; v < 6; v++)
            {
                var version = CardFaces.Specialized(card, v);
                var unread = CardCompiler.Compile(version).Unhandled;
                texts.Add(string.Join("\n", CardCompiler.Lines(version)
                    .Where(l => !unread.Contains(l, StringComparer.Ordinal))));
            }

            var r = CardCompiler.Compile(WithVersions(card, texts));
            output.WriteLine($"  {card.Name}: complete={r.IsComplete} residual={string.Join(" | ", r.Unhandled)}");
        }
    }
}
