using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Round twenty-one, agent 04: exiling the source as an activation cost.</summary>
public sealed class TempMeasureR2104(ITestOutputHelper output)
{
    /// <summary>
    /// "Exile ~" / "exile it" in a cost. The trailing guard is a negative lookahead rather than
    /// a word boundary: "~" is not a word character, so <c>\b</c> after it asserts that the next
    /// character *is* one, and both "Exile ~:" and "Exile ~ from your graveyard" fail it. That
    /// mistake measured this whole family at three cards.
    /// </summary>
    private static readonly Regex ExileSelf = new(
        @"\bexile (~|it)(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

    private static readonly Regex AnyExile = new(@"\bexile\b", RegexOptions.IgnoreCase);

    /// <summary>The same token with the zone phrase attached, so a swap takes both.</summary>
    private static readonly Regex ZonedExileSelf = new(
        @"\bexile (~|it) from your (graveyard|hand)", RegexOptions.IgnoreCase);

    private const string ProbeName = "Zzprobe";

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

    /// <summary>Which zone the cost names, and whether the exile is the whole cost.</summary>
    private static string Bucket(string line)
    {
        var whole = CostOf(line);
        var m = ExileSelf.Match(whole);
        var after = whole[(m.Index + m.Length)..].TrimStart();

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

        var rest = ExileSelf.Replace(whole, string.Empty);
        if (zone != "battlefield")
        {
            var at = rest.IndexOf("from", System.StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var end = rest.IndexOf(',', at);
                rest = end < 0 ? rest[..at] : rest[..at] + rest[(end + 1)..];
            }
        }

        var alone = rest.Trim().Trim(',').Trim().Length == 0;
        return zone + (alone ? " (whole cost)" : " (with other cost items)");
    }

    /// <summary>The same card with different text, keeping every characteristic that decides
    /// which matchers are even offered the line.</summary>
    private static CardDefinition Retext(CardDefinition card, string name, string text) =>
        new()
        {
            OracleId = card.OracleId,
            Name = name,
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
            Colors = card.Colors,
            ColorIdentity = card.ColorIdentity,
        };

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
        var touchingComplete = 0;

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
                touchingComplete++;
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
        output.WriteLine($"CARDS_WITH_AN_EXILE_SELF_COST={touching} (already complete: {touchingComplete})");
        output.WriteLine($"SOLE_BLOCKER = {sole.Count}");
        output.WriteLine($"PARTIAL (blocked also by something else) = {partial.Count}");

        output.WriteLine(string.Empty);
        output.WriteLine("---- SOLE set, decomposed by zone / shape ----");
        foreach (var g in sole
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileSelfCost).Select(l => (c, l)))
            .GroupBy(x => Bucket(x.l), System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"[{g.Key}] {g.Count()} lines / {g.Select(x => x.c.Name).Distinct().Count()} cards");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- EVERY exile-self-cost line in the corpus, by zone / shape ----");
        foreach (var g in corpus
            .SelectMany(c => CardCompiler.Lines(c).Where(IsExileSelfCost).Select(l => (c, l)))
            .GroupBy(x => Bucket(x.l), System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            var unreadHere = g.Count(x => CardCompiler.Compile(x.c).Unhandled.Contains(x.l));
            output.WriteLine($"[{g.Key}] {g.Count()} lines, {unreadHere} unread / {g.Select(x => x.c.Name).Distinct().Count()} cards");
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
        output.WriteLine("---- PARTIAL cards (first 60) ----");
        foreach (var c in partial.OrderBy(c => c.Name, System.StringComparer.Ordinal).Take(60))
        {
            output.WriteLine($"PARTIAL | {c.Name} | " + string.Join(" ## ", CardCompiler.Compile(c).Unhandled));
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- unread exile-self-cost lines, ranked ----");
        foreach (var g in corpus
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileSelfCost).Select(l => (c, l)))
            .GroupBy(x => x.l, System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(70))
        {
            output.WriteLine($"{g.Count(),4}  {g.Key}   [{string.Join(", ", g.Select(x => x.c.Name).Take(2))}]");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("---- unread costs that exile SOMETHING ELSE, ranked by the cost ----");
        foreach (var g in corpus
            .SelectMany(c => CardCompiler.Compile(c).Unhandled.Where(IsExileOtherCost).Select(l => (c, l)))
            .GroupBy(x => CostOf(x.l), System.StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(30))
        {
            output.WriteLine($"{g.Count(),4}  COST[{g.Key}]   [{string.Join(", ", g.Select(x => x.c.Name).Take(2))}]");
        }
    }

    /// <summary>
    /// Excision with two controls. The candidate lines are cut and the completions recounted;
    /// then the same lines are reassembled with nothing cut (they must still fail), and again
    /// with the exile token swapped for one that already reads (they must then complete, or the
    /// blocker is the effect sentence and this family is worth less than it looks).
    /// </summary>
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
        var graveyard = 0;
        var anySelf = 0;
        var elseExile = 0;

        var probed = 0;
        var uncutStillFails = 0;
        var uncutWrong = new List<string>();
        var swapCompletes = 0;
        var swapStillFails = new List<string>();
        var swapWins = new List<string>();
        var swapByBucket = new Dictionary<string, int>(System.StringComparer.Ordinal);
        var skippedFaces = 0;

        foreach (var card in corpus)
        {
            var unread = CardCompiler.Compile(card).Unhandled;

            if (unread.IsEmpty)
            {
                baseline++;
                battlefield++;
                graveyard++;
                anySelf++;
                elseExile++;
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
                graveyard++;
            }

            if (unread.All(IsExileOtherCost))
            {
                elseExile++;
            }

            if (!unread.All(IsExileSelfCost))
            {
                continue;
            }

            anySelf++;

            if (card.Faces.Count > 1)
            {
                skippedFaces++;
                continue;
            }

            probed++;

            // Control one: the same lines reassembled with nothing cut. Must still fail.
            var text = string.Join("\n", CardCompiler.Lines(card)).Replace("~", ProbeName, System.StringComparison.Ordinal);
            var uncut = Retext(card, ProbeName, text);
            if (!CardCompiler.Compile(uncut).IsComplete)
            {
                uncutStillFails++;
            }
            else
            {
                uncutWrong.Add(card.Name);
            }

            // Control two: swap the exile token for "Sacrifice", which ReadCost already
            // charges. The zone phrase goes with it - "Sacrifice ~ from your graveyard" is not
            // a cost ReadCost charges either, and leaving it behind would blame the effect for
            // what the cost did.
            var joined = string.Join("\n", CardCompiler.Lines(card));
            var swappedText = ZonedExileSelf.Replace(joined, "Sacrifice $1");
            swappedText = ExileSelf.Replace(swappedText, "Sacrifice $1")
                .Replace("~", ProbeName, System.StringComparison.Ordinal);
            var swapped = Retext(card, ProbeName, swappedText);
            var swapResult = CardCompiler.Compile(swapped);
            var bucket = Bucket(unread.First(IsExileSelfCost));
            if (swapResult.IsComplete)
            {
                swapCompletes++;
                swapByBucket[bucket] = swapByBucket.GetValueOrDefault(bucket) + 1;
                swapWins.Add(bucket + " | " + card.Name + " | " + string.Join(" ## ", unread));
            }
            else
            {
                swapStillFails.Add(
                    bucket + " | " + card.Name + " -> " + string.Join(" ## ", swapResult.Unhandled));
            }
        }

        output.WriteLine($"BASELINE={baseline}");
        output.WriteLine($"EXCISE exile-self-from-BATTLEFIELD = {battlefield}  DELTA={battlefield - baseline}");
        output.WriteLine($"EXCISE exile-self-from-GRAVEYARD   = {graveyard}  DELTA={graveyard - baseline}");
        output.WriteLine($"EXCISE exile-self ANY zone         = {anySelf}  DELTA={anySelf - baseline}");
        output.WriteLine($"EXCISE exile-SOMETHING-ELSE        = {elseExile}  DELTA={elseExile - baseline}");
        output.WriteLine(string.Empty);
        output.WriteLine($"probed={probed} skipped(multi-face)={skippedFaces}");
        output.WriteLine($"CONTROL uncut, still incomplete = {uncutStillFails} of {probed}");
        foreach (var n in uncutWrong.Take(25))
        {
            output.WriteLine("  UNCUT-COMPLETED " + n);
        }

        output.WriteLine($"CONTROL swap to 'Sacrifice' completes = {swapCompletes} of {probed}");
        foreach (var kv in swapByBucket.OrderByDescending(kv => kv.Value))
        {
            output.WriteLine($"  SWAP-WINS-BY-BUCKET [{kv.Key}] {kv.Value}");
        }

        foreach (var n in swapWins)
        {
            output.WriteLine("  SWAP-WIN " + n);
        }

        foreach (var n in swapStillFails.Take(90))
        {
            output.WriteLine("  SWAP-STILL-FAILS " + n);
        }
    }

    /// <summary>
    /// Every card's compiled shape, one line each, for a before/after diff. Effects are rendered
    /// rather than counted: a change that swaps one effect for another leaves the count alone.
    /// </summary>
    [Fact]
    public void Dump()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("NO CORPUS");
            return;
        }

        var to = System.Environment.GetEnvironmentVariable("R2104_DUMP");
        if (string.IsNullOrEmpty(to))
        {
            output.WriteLine("NO R2104_DUMP");
            return;
        }

        using var writer = new StreamWriter(to);

        foreach (var card in corpus.OrderBy(c => c.OracleId, System.StringComparer.Ordinal))
        {
            var c = CardCompiler.Compile(card);
            var parts = new List<string>
            {
                card.OracleId,
                card.Name,
                c.IsComplete ? "COMPLETE" : "INCOMPLETE",
                "unread=" + string.Join(" ## ", c.Unhandled),
            };

            if (c.Spell is { } spell)
            {
                parts.Add("spell=" + string.Join(",", spell.Effects.Select(e => e.ToString())));
            }

            foreach (var a in c.Activated)
            {
                parts.Add(
                    $"act[{a.Id}] self={a.SelfCost} from={a.FunctionsFrom} mana={a.ManaCost} "
                        + $"tap={a.RequiresTap} life={a.LifeCost} chosen={a.ChosenCosts.Count} "
                        + $"eff=" + string.Join(",", a.Effects.Select(e => e.ToString())));
            }

            foreach (var t in c.Triggers)
            {
                parts.Add($"trig[{t.Id}] eff=" + string.Join(",", t.Effects.Select(e => e.ToString())));
            }

            writer.WriteLine(string.Join(" | ", parts));
        }

        output.WriteLine("WROTE " + to);
    }
}
