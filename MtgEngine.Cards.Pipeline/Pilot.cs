using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// The wave-0 decision point: a slice of deck cards through both translators, then the
/// agreement rate, the lint pass rate and the cost per face. Writes what it learned as card
/// files and a report; the decision is the coordinator's.
/// </summary>
public static class Pilot
{
    public static readonly IReadOnlyList<string> Models = ["claude-opus-5", "claude-sonnet-5"];

    /// <summary>List prices per million tokens, from the API's cached rate table; batch requests are billed at half.</summary>
    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> Prices = new Dictionary<string, (decimal, decimal)>
    {
        ["claude-opus-5"] = (5m, 25m),
        ["claude-sonnet-5"] = (2m, 10m),
    };

    public static async Task<int> RunAsync(string repoRoot, int count, string outDir, bool repair, CancellationToken ct)
    {
        var raw = Corpus.LoadRaw(Corpus.DefaultPath(repoRoot));
        var deckIds = Corpus.DeckOracleIds(Path.Combine(repoRoot, "MtgEngine.Api", "mtgengine.db"));
        var cards = new List<CorpusCard>();
        foreach (var id in deckIds)
        {
            var card = Corpus.Card(raw, id);
            if (card is null || card.IsFreeSlice || card.Faces.All(f => f.OracleText.Length == 0))
                continue;
            cards.Add(card);
            if (cards.Count == count)
                break;
        }

        Directory.CreateDirectory(outDir);
        Console.WriteLine($"pilot: {cards.Count} faces selected from {deckIds.Count} deck cards (free slice skipped)");
        File.WriteAllText(Path.Combine(outDir, "selection.json"), JsonSerializer.Serialize(cards.Select(c => new { c.OracleId, c.Name }), new JsonSerializerOptions { WriteIndented = true }));

        var client = new Anthropic.AnthropicClient { ApiKey = Secrets.AnthropicApiKey() };
        var translator = new Translator(client, "pilot-v1-" + Translator.Hash(Translator.SystemWithVocabulary()));
        File.WriteAllText(Path.Combine(outDir, "system-prompt.txt"), Translator.SystemWithVocabulary());

        var batches = await translator.SubmitAsync(cards, Models, ct);
        File.WriteAllText(Path.Combine(outDir, "batches.json"), JsonSerializer.Serialize(batches));
        var readings = (await translator.CollectAsync(batches, TimeSpan.FromSeconds(30), ct)).ToList();

        var byCard = cards.ToDictionary(c => c.OracleId, StringComparer.Ordinal);
        var results = new List<CardResult>();
        foreach (var card in cards)
        {
            var perModel = new Dictionary<string, Reading>(StringComparer.Ordinal);
            foreach (var model in Models)
            {
                var reading = readings.FirstOrDefault(r => r.OracleId == card.OracleId && r.Model == model)
                    ?? new Reading(card.OracleId, model, string.Empty, null, "missing from batch", 0, 0, 0, 0);
                var lint = reading.Definition is null ? null : Linter.Check(FileFor(card, reading.Definition, [model], agreed: false, translator.PromptVersion));
                if (repair && (reading.Definition is null || lint!.HasErrors))
                {
                    var problem = reading.ParseError ?? string.Join("; ", lint!.Findings.Where(f => f.Severity == LintSeverity.Error).Select(f => f.Message));
                    reading = await translator.RepairAsync(card, reading, problem, ct);
                }

                perModel[model] = reading;
            }

            results.Add(Evaluate(card, perModel, translator.PromptVersion));
        }

        WriteCardFiles(repoRoot, results);
        var report = Report(results, cards.Count);
        File.WriteAllText(Path.Combine(outDir, "report.md"), report);
        File.WriteAllText(Path.Combine(outDir, "readings.json"), JsonSerializer.Serialize(results.Select(r => new { r.OracleId, r.Name, r.Agreed, readings = r.Readings.Select(x => new { x.Model, x.ParseError, lint = r.Lint[x.Model], x.Raw }) }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report);
        return 0;
    }

    private static CardFile FileFor(CorpusCard card, CardDefinition definition, IReadOnlyList<string> models, bool agreed, string promptVersion) => new(
        card.OracleId,
        card.Name,
        card.OracleHash,
        IrVersion.Current,
        definition,
        agreed ? ImplementationStatus.Translated : ImplementationStatus.Disputed,
        new TranslatedProvenance(models, promptVersion, agreed),
        card.Keywords);

    private sealed record CardResult(string OracleId, string Name, IReadOnlyList<Reading> Readings, IReadOnlyDictionary<string, LintReport?> Lint, bool Agreed, CardFile? File);

    private static CardResult Evaluate(CorpusCard card, IReadOnlyDictionary<string, Reading> perModel, string promptVersion)
    {
        var lint = perModel.ToDictionary(p => p.Key, p => p.Value.Definition is null ? null : Linter.Check(FileFor(card, p.Value.Definition, [p.Key], false, promptVersion)), StringComparer.Ordinal);
        var good = perModel.Where(p => p.Value.Definition is not null && lint[p.Key]!.HasErrors == false).Select(p => p.Value).ToList();

        var agreed = good.Count == Models.Count && good.Select(r => Canonical(r.Definition!)).Distinct(StringComparer.Ordinal).Count() == 1;
        CardFile? file = null;
        if (agreed)
            file = FileFor(card, good[0].Definition!, Models, true, promptVersion);
        else if (good.Count > 0)
        {
            file = FileFor(card, good[0].Definition!, [good[0].Model], false, promptVersion) with
            {
                Candidates = [.. perModel.Values.Where(r => r.Definition is not null).Select(r => new TranslationCandidate(r.Model, r.Definition!))],
            };
        }

        return new CardResult(card.OracleId, card.Name, [.. perModel.Values], lint, agreed, file);
    }

    /// <summary>Two readings agree when they are the same after the printed text fields are dropped.</summary>
    public static string Canonical(CardDefinition definition)
    {
        var node = JsonNode.Parse(CardFileSerializer.WriteDefinition(definition))!;
        Strip(node);
        return node.ToJsonString();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("text");
                obj.Remove("oracleText");
                foreach (var child in obj.Select(p => p.Value).ToList())
                    Strip(child);
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    Strip(child);
                break;
        }
    }

    private static void WriteCardFiles(string repoRoot, IReadOnlyList<CardResult> results)
    {
        var dir = Path.Combine(repoRoot, "cards");
        Directory.CreateDirectory(dir);
        foreach (var r in results.Where(r => r.File is not null))
            File.WriteAllText(Path.Combine(dir, r.OracleId + ".json"), CardFileSerializer.Write(r.File!) + "\n");
    }

    private static string Report(IReadOnlyList<CardResult> results, int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Pilot report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"faces: {count}");
        foreach (var model in Models)
        {
            var readings = results.Select(r => r.Readings.First(x => x.Model == model)).ToList();
            var parsed = readings.Count(r => r.Definition is not null);
            var clean = results.Count(r => r.Lint[model] is { HasErrors: false });
            var unsupported = results.Count(r => r.Lint[model]?.Findings.Any(f => f.Code == "L001") == true);
            var input = readings.Sum(r => r.InputTokens);
            var output = readings.Sum(r => r.OutputTokens);
            var cacheRead = readings.Sum(r => r.CacheReadTokens);
            var cacheWrite = readings.Sum(r => r.CacheWriteTokens);
            var price = Prices[model];
            var cost = ((input * price.Input) + (cacheRead * price.Input * 0.1m) + (cacheWrite * price.Input * 1.25m) + (output * price.Output)) / 1_000_000m * 0.5m;
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {model}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- parsed: {parsed}/{count}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- lint clean: {clean}/{count}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- with Unsupported markers: {unsupported}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- tokens: input {input:N0}, cache read {cacheRead:N0}, cache write {cacheWrite:N0}, output {output:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- estimated batch cost: ${cost:F2} (${cost / Math.Max(1, count):F3} per face)");
            var codes = results.SelectMany(r => r.Lint[model]?.Findings ?? []).GroupBy(f => f.Code).OrderByDescending(g => g.Count());
            sb.AppendLine("- lint findings by code: " + string.Join(", ", codes.Select(g => $"{g.Key}×{g.Count()}")));
            var reasons = results.SelectMany(r => r.Lint[model]?.Findings.Where(f => f.Code == "L001") ?? []).GroupBy(f => f.Message).OrderByDescending(g => g.Count()).Take(15);
            foreach (var g in reasons)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {g.Count()}× {g.Key}");
            sb.AppendLine();
        }

        var agreed = results.Count(r => r.Agreed);
        var bothClean = results.Count(r => Models.All(m => r.Lint[m] is { HasErrors: false }));
        sb.AppendLine("## Agreement");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- both models lint clean: {bothClean}/{count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- agreed (identical after stripping printed text): {agreed}/{count} ({100.0 * agreed / Math.Max(1, count):F1}%)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- agreed among both-clean: {agreed}/{Math.Max(1, bothClean)} ({100.0 * agreed / Math.Max(1, bothClean):F1}%)");
        sb.AppendLine();
        sb.AppendLine("## Disagreements (first 40)");
        foreach (var r in results.Where(r => !r.Agreed).Take(40))
        {
            var verdicts = string.Join(" | ", Models.Select(m => m + ": " + Verdict(r, m)));
            sb.Append("- ").Append(r.Name).Append(": ").AppendLine(verdicts);
        }

        return sb.ToString();
    }

    private static string Verdict(CardResult r, string model)
    {
        if (r.Readings.First(x => x.Model == model).Definition is null)
            return "parse fail";
        var lint = r.Lint[model]!;
        return lint.HasErrors
            ? "lint " + string.Join(",", lint.Findings.Where(f => f.Severity == LintSeverity.Error).Select(f => f.Code).Distinct())
            : "clean";
    }
}
