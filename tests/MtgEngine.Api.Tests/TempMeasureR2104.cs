using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

public sealed class TempMeasureR2104(ITestOutputHelper output)
{
    private static readonly Regex ExileSelf = new(
        @"exile (~|it)\b", RegexOptions.IgnoreCase);

    private static readonly Regex AnyExile = new(
        @"\bexile\b", RegexOptions.IgnoreCase);

    private static string CostOf(string line)
    {
        var colon = line.IndexOf(':', System.StringComparison.Ordinal);
        return colon < 0 ? string.Empty : line[..colon];
    }

    private static bool IsExileSelfCost(string line)
    {
        var cost = CostOf(line);
        return cost.Length > 0 && ExileSelf.IsMatch(cost);
    }

    private static bool IsExileOtherCost(string line)
    {
        var cost = CostOf(line);
        return cost.Length > 0 && AnyExile.IsMatch(cost) && !ExileSelf.IsMatch(cost);
    }

    private static string Bucket(string line)
    {
        var whole = CostOf(line);
        var m = ExileSelf.Match(whole);
        var after = whole[(m.Index + m.Length)..].TrimStart();
        var alone = whole.Trim().Trim(',').Trim();

        string zone;
        if (after.StartsWith("from your graveyard", System.StringComparison.OrdinalIgnoreCase))
        {
            zone = "graveyard";
        }
        else if (after.StartsWith("from your hand", System.StringComparison.OrdinalIgnoreCase))
        {
            zone = "hand";
        }
        else if (after.StartsWith("from", System.StringComparison.OrdinalIgnoreCase))
        {
            zone = "other-zone";
        }
        else
        {
            zone = "battlefield";
        }

        var soleItem = alone.Equals("Exile ~", System.StringComparison.OrdinalIgnoreCase)
            || alone.Equals("Exile it", System.StringComparison.OrdinalIgnoreCase)
            || alone.Equals("Exile ~ from your graveyard", System.StringComparison.OrdinalIgnoreCase);

        return zone + (soleItem ? " (whole cost)" : " (part of a larger cost)");
    }

    [Fact]
    public void Measure()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("NO CORPUS");
            return;
        }

        var baseComplete = 0;
        var total = 0;
        var lineTotal = 0;
        var lineRead = 0;
        var sole = new List<CardDefinition>();
        var partial = new List<CardDefinition>();
        var touching = 0;

        foreach (var card in corpus)
        {
            total++;
            var compiled = CardCompiler.Compile(card);
            var lines = CardCompiler.Lines(card).ToList();
            lineTotal += lines.Count;
            lineRead += lines.Count - compiled.Unhandled.Count;
            if (compiled.IsComplete)
            {
                baseComplete++;
            }

            if (!lines.Any(IsExileSelfCost))
            {
                continue;
            }

            touching++;

            if (compiled.IsComplete)
            {
                continue;
            }

            var unread = compiled.Unhandled.ToList();
            if (unread.Count > 0 && unread.All(IsExileSelfCost))
            {
                sole.Add(card);
            }
            else if (unread.Any(IsExileSelfCost))
            {
                partial.Add(card);
            }
        }

        output.WriteLine($"TOTAL={total} BASELINE_COMPLETE={baseComplete}");
        output.WriteLine($"LINES total={lineTotal} read={lineRead}");
        output.WriteLine($"CARDS_TOUCHING_AN_EXILE_SELF_COST={touching}");
        output.WriteLine($"SOLE_BLOCKER = {sole.Count}");
        output.WriteLine($"PARTIAL (blocked also by something else) = {partial.Count}");

        output.WriteLine(string.Empty);
        output.WriteLine("---- decomposition of the SOLE set ----");
        foreach (var g in sole
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileSelfCost).Select(l => (c, l)))
            .GroupBy(x => Bucket(x.l), System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"[{g.Key}] {g.Count()} lines / {g.Select(x => x.c.Name).Distinct().Count()} cards");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- SOLE cards: name | unread line ----");
        foreach (var c in sole.OrderBy(c => c.Name, System.StringComparer.Ordinal))
        {
            foreach (var l in CardCompiler.Compile(c).Unhandled)
            {
                output.WriteLine($"SOLE | {c.Name} | {l}");
            }
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- PARTIAL cards ----");
        foreach (var c in partial.OrderBy(c => c.Name, System.StringComparer.Ordinal))
        {
            output.WriteLine($"PARTIAL | {c.Name} | " + string.Join(" ## ", CardCompiler.Compile(c).Unhandled));
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- ALL unread lines with an exile-self cost, ranked ----");
        foreach (var g in corpus
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileSelfCost).Select(l => (c, l)))
            .GroupBy(x => x.l, System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(70))
        {
            output.WriteLine($"{g.Count(),4}  {g.Key}   [{string.Join(", ", g.Select(x => x.c.Name).Take(3))}]");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- costs that exile SOMETHING ELSE, unread, ranked by cost ----");
        foreach (var g in corpus
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileOtherCost).Select(l => (c, l)))
            .GroupBy(x => CostOf(x.l), System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(30))
        {
            output.WriteLine($"{g.Count(),4}  COST[{g.Key}]   [{string.Join(", ", g.Select(x => x.c.Name).Take(3))}]");
        }
    }

    /// <summary>Excision with a control: drop the matching lines, recount.</summary>
    [Fact]
    public void Excise()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("NO CORPUS");
            return;
        }

        var baseline = 0;
        var battlefield = 0;
        var graveyardPart = 0;
        var elseExile = 0;
        var anySelf = 0;
        var control = 0;

        foreach (var card in corpus)
        {
            var unread = CardCompiler.Compile(card).Unhandled;

            if (unread.IsEmpty)
            {
                baseline++;
                battlefield++;
                graveyardPart++;
                elseExile++;
                anySelf++;
                control++;
                continue;
            }

            if (unread.All(l => IsExileSelfCost(l)
                && Bucket(l).StartsWith("battlefield", System.StringComparison.Ordinal)))
            {
                battlefield++;
            }

            if (unread.All(l => IsExileSelfCost(l)
                && Bucket(l).StartsWith("graveyard", System.StringComparison.Ordinal)))
            {
                graveyardPart++;
            }

            if (unread.All(IsExileSelfCost))
            {
                anySelf++;
            }

            if (unread.All(IsExileOtherCost))
            {
                elseExile++;
            }

            // Control: an activation cost containing "sacrifice ~", which already reads.
            // Nothing should move.
            if (unread.All(l => CostOf(l).Contains("sacrifice ~", System.StringComparison.OrdinalIgnoreCase)))
            {
                control++;
            }
        }

        output.WriteLine($"BASELINE={baseline}");
        output.WriteLine($"EXCISE exile-self-from-BATTLEFIELD = {battlefield}  DELTA={battlefield - baseline}");
        output.WriteLine($"EXCISE exile-self-from-GRAVEYARD   = {graveyardPart}  DELTA={graveyardPart - baseline}");
        output.WriteLine($"EXCISE exile-self ANY zone         = {anySelf}  DELTA={anySelf - baseline}");
        output.WriteLine($"EXCISE exile-SOMETHING-ELSE        = {elseExile}  DELTA={elseExile - baseline}");
        output.WriteLine($"CONTROL(sacrifice ~ cost, reads)   = {control}  DELTA={control - baseline}");
    }
}
