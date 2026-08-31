using System.Text;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Scratch measurement harness for r21-42. Not part of the suite; deleted before commit.</summary>
public sealed class ScratchR2142Tests(ITestOutputHelper output)
{
    private static readonly Regex Family = new(
        @"equal to (its|that creature's|the sacrificed creature's|that permanent's|their) (power|toughness)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static CardDefinition WithText(CardDefinition c, string text) => new()
    {
        OracleId = c.OracleId,
        Name = c.Name,
        ManaCost = c.ManaCost,
        ManaCostRaw = c.ManaCostRaw,
        Cmc = c.Cmc,
        CardTypes = c.CardTypes,
        Subtypes = c.Subtypes,
        Supertypes = c.Supertypes,
        OracleText = text,
        Power = c.Power,
        Toughness = c.Toughness,
        StartingLoyalty = c.StartingLoyalty,
        Defense = c.Defense,
        Keywords = c.Keywords,
        Faces = c.Faces,
        Colors = c.Colors,
        ColorIdentity = c.ColorIdentity,
        Legalities = c.Legalities,
    };

    [Fact]
    public void Measure()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var mode = Environment.GetEnvironmentVariable("R2142_MODE") ?? "base";
        var outPath = Environment.GetEnvironmentVariable("R2142_OUT");

        var complete = new List<CardDefinition>();
        var familyCards = new List<(CardDefinition Card, CompiledCard Compiled)>();

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
                complete.Add(card);
            else if (compiled.Unhandled.Any(l => Family.IsMatch(l)))
                familyCards.Add((card, compiled));
        }

        output.WriteLine($"corpus={corpus.Count} complete={complete.Count}");
        output.WriteLine($"incomplete cards with a family line unread: {familyCards.Count}");
        output.WriteLine("  of which every unread line is a family line: "
            + familyCards.Count(f => f.Compiled.Unhandled.All(l => Family.IsMatch(l))));
        output.WriteLine("  of which exactly one unread line, family:   "
            + familyCards.Count(f => f.Compiled.Unhandled.Count == 1));

        if (mode == "set" && outPath is not null)
        {
            File.WriteAllLines(
                outPath,
                complete.Select(c => $"{c.OracleId}\t{c.Name}").Order(StringComparer.Ordinal));
            return;
        }

        if (mode == "report")
        {
            var sb = new StringBuilder();
            foreach (var g in familyCards
                .SelectMany(f => f.Compiled.Unhandled
                    .Where(l => Family.IsMatch(l))
                    .Select(l => (Line: Normalise(l, f.Card.Name), Sole: f.Compiled.Unhandled.Count == 1)))
                .GroupBy(x => x.Line, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"{g.Count()}\tsole={g.Count(x => x.Sole)}\t{g.Key}");
            }

            File.WriteAllText(outPath ?? "family-lines.txt", sb.ToString());

            var names = new StringBuilder();
            foreach (var f in familyCards
                .Where(f => f.Compiled.Unhandled.Count == 1)
                .OrderBy(f => f.Card.Name, StringComparer.Ordinal))
            {
                names.AppendLine($"{f.Card.OracleId}\t{f.Card.Name}\t{f.Compiled.Unhandled[0]}");
            }

            File.WriteAllText((outPath ?? "family-lines.txt") + ".sole.txt", names.ToString());
            output.WriteLine("wrote report");
            return;
        }

        if (mode is "excise" or "linectl" or "subst")
        {
            var gained = new List<string>();

            foreach (var (card, compiled) in familyCards)
            {
                // A two-faced card keeps its text on the faces, which this rewrite cannot reach.
                if (card.Faces.Count > 1)
                    continue;

                var text = mode switch
                {
                    "excise" => WithoutLines(card, l => Family.IsMatch(l)),
                    "linectl" => WithoutOneOtherUnreadLine(card),
                    _ => Family.Replace(card.OracleText, "equal to the number of Mountains you control"),
                };

                if (text is null || text == card.OracleText)
                    continue;

                if (CardCompiler.Compile(WithText(card, text)).IsComplete)
                    gained.Add($"{card.OracleId}\t{card.Name}");
            }

            output.WriteLine($"mode={mode} newly complete = {gained.Count}");
            if (outPath is not null)
                File.WriteAllLines(outPath, gained.Order(StringComparer.Ordinal));
        }
    }

    private static string Normalise(string line, string name)
    {
        var t = line.Replace(name, "~", StringComparison.Ordinal);
        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
            t = t.Replace(name[..comma], "~", StringComparison.Ordinal);
        return Regex.Replace(t, @"\d+", "N");
    }

    private static string? WithoutLines(CardDefinition card, Func<string, bool> drop)
    {
        var kept = card.OracleText.Split('\n').Where(l => !drop(l)).ToList();
        return kept.Count == 0 ? null : string.Join('\n', kept);
    }

    /// <summary>
    /// The line control: one printed line that is unread and is <em>not</em> a family line,
    /// taken away instead. "Unread" is measured — removing it lowers the unhandled count.
    /// </summary>
    private static string? WithoutOneOtherUnreadLine(CardDefinition card)
    {
        var lines = card.OracleText.Split('\n');
        var baseline = CardCompiler.Compile(card).Unhandled.Count;

        for (var i = 0; i < lines.Length; i++)
        {
            if (Family.IsMatch(lines[i]))
                continue;

            var without = string.Join('\n', lines.Where((_, j) => j != i));
            if (without.Length == 0)
                continue;

            if (CardCompiler.Compile(WithText(card, without)).Unhandled.Count < baseline)
                return without;
        }

        return null;
    }
}
