using System.Globalization;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Scratch measurement for round twenty-one's attraction pass. Reports, asserts nothing.</summary>
public sealed class AttractionMeasureR2144Tests(ITestOutputHelper output)
{
    private static CardDefinition Rewritten(CardDefinition card, string text) => new()
    {
        OracleId = card.OracleId + "#ctl-" + text.GetHashCode(StringComparison.Ordinal)
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
    };

    private static bool InFamily(CardDefinition card) =>
        card.Subtypes.Contains("Attraction", StringComparer.OrdinalIgnoreCase)
        || (card.OracleText ?? string.Empty).Contains("Attraction", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Attraction_family_measured()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var compiled = corpus.Select(c => (Card: c, Result: CardCompiler.Compile(c))).ToList();
        output.WriteLine($"corpus {corpus.Count} complete {compiled.Count(c => c.Result.IsComplete)}");

        var family = compiled.Where(c => InFamily(c.Card)).ToList();
        output.WriteLine($"family cards {family.Count}, complete {family.Count(c => c.Result.IsComplete)}");

        var incomplete = family.Where(c => !c.Result.IsComplete).ToList();
        output.WriteLine($"incomplete family cards {incomplete.Count}");

        // Excision: drop every unread line mentioning an Attraction or a visit ability.
        var excised = 0;
        var substituted = 0;
        var lineControl = 0;
        var lineControlEligible = 0;
        var lineControlNames = new List<string>();

        foreach (var (card, result) in incomplete)
        {
            var whole = CardCompiler.Lines(card).ToList();
            var mine = result.Unhandled
                .Where(l => l.Contains("attraction", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("visit", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("Prize —", StringComparison.Ordinal))
                .ToList();

            if (mine.Count > 0)
            {
                var cut = string.Join("\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
                if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                    excised++;

                var swapped = string.Join(
                    "\n",
                    whole.Select(l => mine.Contains(l, StringComparer.Ordinal)
                        ? "Draw a card."
                        : l));

                if (CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                    substituted++;
            }

            // The line control: drop a *different* unread line on a card in the same family.
            var others = result.Unhandled.Where(l => !mine.Contains(l, StringComparer.Ordinal)).ToList();
            if (others.Count == 0)
                continue;

            lineControlEligible++;
            var dropped = string.Join(
                "\n", whole.Where(l => !string.Equals(l, others[0], StringComparison.Ordinal)));

            if (CardCompiler.Compile(Rewritten(card, dropped)).IsComplete)
            {
                lineControl++;
                lineControlNames.Add(card.Name + " << " + others[0]);
            }
        }

        output.WriteLine($"excision (family lines dropped): {excised}");
        output.WriteLine($"substitution (family lines become a draw): {substituted}");
        foreach (var name in lineControlNames)
            output.WriteLine("  LINE CONTROL " + name);
        output.WriteLine(
            $"line control (one other unread line dropped, {lineControlEligible} eligible): {lineControl}");

        foreach (var (card, result) in incomplete.OrderBy(c => c.Card.Name, StringComparer.Ordinal))
        {
            output.WriteLine(
                $"  STILL SHORT {card.Name} :: {string.Join(" | ", result.Unhandled)}");
        }

        foreach (var (card, _) in family.Where(c => c.Result.IsComplete)
            .OrderBy(c => c.Card.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"  COMPLETE {card.Name}");
        }
    }

    [Fact]
    public void Sticker_decline_rechecked()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var carriers = corpus
            .Select(c => (Card: c, Result: CardCompiler.Compile(c)))
            .Where(c => !c.Result.IsComplete
                && c.Result.Unhandled.Any(l =>
                    l.Contains("sticker", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var lines = 0;
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        var excised = 0;
        var substituted = 0;

        foreach (var (card, result) in carriers)
        {
            var mine = result.Unhandled
                .Where(l => l.Contains("sticker", StringComparison.OrdinalIgnoreCase))
                .ToList();

            lines += mine.Count;
            foreach (var line in mine)
                shapes.Add(line);

            var whole = CardCompiler.Lines(card).ToList();

            var cut = string.Join("\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
            if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                excised++;

            var swapped = string.Join(
                "\n",
                whole.Select(l => mine.Contains(l, StringComparer.Ordinal)
                    ? System.Text.RegularExpressions.Regex.Replace(
                        l,
                        "([Yy]ou may )?put (a|an|up to two|up to one) (name )?stickers? on [^,.]*",
                        m => m.Groups[1].Success ? "you may draw a card" : "draw a card")
                    : l));

            if (CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                substituted++;
        }

        output.WriteLine(
            $"sticker: cards {carriers.Count} lines {lines} distinct {shapes.Count} "
                + $"excised {excised} substituted {substituted}");
    }
}
