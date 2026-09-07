using System.Text.Json;
using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Verify;

/// <summary>One ruling on its way to a scenario, with the id its batch request carried.</summary>
public sealed record RulingEntry(string CustomId, string OracleId, string CardName, string Comment, string? PublishedAt = null)
{
    /// <summary>The request the stage's prompt is built from.</summary>
    public RulingRequest Request => new(OracleId, CardName, Comment, PublishedAt);
}

/// <summary>
/// What <c>rulings-submit</c> leaves for <c>rulings-collect</c>: one batch id per model, the
/// rulings that went into them, and the directory whose cards they belong to.
/// </summary>
public sealed record RulingsSubmission(
    string CardsDirectory,
    IReadOnlyDictionary<string, string> Batches,
    IReadOnlyList<RulingEntry> Rulings)
{
    public const string FileName = "rulings-submission.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, Options));
    }

    /// <summary>The submission in this directory, or null when nothing was submitted from it.</summary>
    public static RulingsSubmission? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<RulingsSubmission>(File.ReadAllText(path), Options) : null;
    }
}

/// <summary>
/// Everything the rulings commands talk to. <paramref name="Rulings"/> is null when the corpus is
/// not in this checkout — <c>oracle_cards.json</c> lives in <c>bin/</c> and is not in git, and a
/// command that silently found no rulings would look exactly like a corpus with none.
/// </summary>
public sealed record RulingsEnvironment(
    ITranslationClient Client,
    CardFileWriter Writer,
    Func<IReadOnlyList<RulingEntry>>? Rulings,
    IReadOnlyList<string> Models,
    TextWriter Log);

/// <summary>
/// <c>pipeline rulings-submit &lt;cardsDir&gt; &lt;outDir&gt;</c> and
/// <c>pipeline rulings-collect &lt;outDir&gt;</c>: the rulings stage split the way the pilot is
/// split, because a batch takes hours and no process may hold the pipeline dll while it waits.
/// </summary>
/// <remarks>
/// The prompt, the schema and the validation are <see cref="RulingsStage"/>'s and are not written
/// a second time here: this owns only the split — record the batch ids before anything waits on
/// them, check once, and store what came back beside the card. Adding a scenario does not change
/// a card's status: a stored scenario is a claim about the card, and <c>pipeline verify</c> is
/// what turns a claim into a verdict.
/// </remarks>
public static class RulingsCommand
{
    /// <summary>A batch has not ended. Nothing was written; run collect again later.</summary>
    public const int NotEnded = 3;

    /// <summary>The corpus is not in this checkout, so which cards have rulings cannot be known.</summary>
    public const int NoCorpus = 2;

    /// <summary>No rulings to submit, or no submission to collect.</summary>
    public const int NothingToDo = 64;

    /// <summary>Submit one batch per model and exit, having recorded the batch ids first.</summary>
    public static async Task<int> SubmitAsync(RulingsEnvironment env, string cardsDir, string outDir, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.Rulings is null)
        {
            env.Log.WriteLine("rulings-submit: the card corpus is not in this checkout, so no rulings can be found. Refresh the API's bulk data first.");
            return NoCorpus;
        }

        var rulings = env.Rulings();
        if (rulings.Count == 0)
        {
            env.Log.WriteLine("rulings-submit: no card in the directory has a ruling in the corpus; nothing submitted.");
            return NothingToDo;
        }

        var system = RulingsStage.SystemPrompt();
        var requests = rulings.Select(r => (customId: r.CustomId, system, user: RulingsStage.User(r.Request))).ToList();
        var batches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in env.Models)
        {
            ct.ThrowIfCancellationRequested();
            var batchId = await env.Client.SubmitBatch(model, requests, ct).ConfigureAwait(false);
            batches[model] = batchId;
            env.Log.WriteLine(FormattableString.Invariant($"submitted {rulings.Count} rulings to {model}: batch {batchId}"));
        }

        new RulingsSubmission(cardsDir, batches, rulings).Write(outDir);
        env.Log.WriteLine(FormattableString.Invariant(
            $"rulings-submit: {rulings.Count} rulings, {batches.Count} batches, recorded in {outDir}. Collect with `pipeline rulings-collect {outDir}`."));
        return 0;
    }

    /// <summary>
    /// Check each batch once. If any is still running, print its status and exit
    /// <see cref="NotEnded"/> having written nothing. Otherwise validate each answer through
    /// <see cref="RulingsStage.Parse"/> and store the scenarios beside their cards.
    /// </summary>
    public static async Task<int> CollectAsync(RulingsEnvironment env, string outDir, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        var submission = RulingsSubmission.Read(outDir);
        if (submission is null || submission.Batches.Count == 0 || submission.Rulings.Count == 0)
        {
            env.Log.WriteLine(FormattableString.Invariant($"rulings-collect: no submission in {outDir}; run `pipeline rulings-submit` first."));
            return NothingToDo;
        }

        var ended = true;
        foreach (var (model, batchId) in submission.Batches)
        {
            ct.ThrowIfCancellationRequested();
            var status = await env.Client.Poll(batchId, ct).ConfigureAwait(false);
            env.Log.WriteLine(FormattableString.Invariant(
                $"{model} {batchId}: {(status.Ended ? "ended" : "in_progress")} succeeded={status.Succeeded} errored={status.Errored} processing={status.Processing}"));
            ended &= status.Ended;
        }

        if (!ended)
            return NotEnded;

        var byCustomId = submission.Rulings.ToDictionary(r => r.CustomId, r => r, StringComparer.Ordinal);
        var stored = 0;
        var refused = 0;
        foreach (var batchId in submission.Batches.Values)
        {
            await foreach (var item in env.Client.Results(batchId, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (!byCustomId.TryGetValue(item.CustomId, out var ruling))
                    continue;

                var answer = item.Error is { Length: > 0 }
                    ? new RulingScenario(ruling.Request, null, item.Error)
                    : RulingsStage.Parse(ruling.Request, item.Text);
                if (answer.Scenario is null)
                {
                    refused++;
                    env.Log.WriteLine(FormattableString.Invariant($"  refused  {ruling.CardName}: {answer.Error}"));
                    continue;
                }

                if (Store(env, ruling.OracleId, answer.Scenario))
                    stored++;
            }
        }

        env.Log.WriteLine(FormattableString.Invariant(
            $"rulings-collect: {stored} scenarios stored, {refused} answers refused. Run `pipeline verify` to play them."));
        return 0;
    }

    /// <summary>
    /// Adds one scenario to its card's file. The status is left alone: a new scenario is not
    /// evidence, and lowering a Verified card here would only be refused by the writer, which
    /// keeps a verified file over a lower one for the same oracle text.
    /// </summary>
    private static bool Store(RulingsEnvironment env, string oracleId, Scenario scenario)
    {
        var file = env.Writer.Load(oracleId);
        if (file is null)
        {
            env.Log.WriteLine("  refused  no card file for " + oracleId);
            return false;
        }

        if (file.ScenarioList.Any(s => string.Equals(s.Id, scenario.Id, StringComparison.Ordinal)))
            return false;

        env.Writer.Write(file with { Scenarios = [.. file.ScenarioList, scenario] });
        return true;
    }

    /// <summary>
    /// The rulings the corpus holds for the cards in this directory. Scryfall's oracle file carries
    /// them under <c>rulings</c> when the bulk export includes them; a card with none contributes
    /// nothing, which is why a whole run of zero is worth saying out loud.
    /// </summary>
    public static IReadOnlyList<RulingEntry> FromCorpus(string corpusPath, CardDirectory cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var raw = Corpus.LoadRaw(corpusPath);
        var entries = new List<RulingEntry>();
        foreach (var file in cards.Files)
        {
            if (!raw.TryGetValue(file.OracleId, out var card)
                || !card.TryGetProperty("rulings", out var rulings)
                || rulings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var ruling in rulings.EnumerateArray())
            {
                var comment = ruling.TryGetProperty("comment", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(comment))
                    continue;
                var published = ruling.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                entries.Add(new RulingEntry(
                    FormattableString.Invariant($"ruling-{entries.Count}"),
                    file.OracleId,
                    file.Name,
                    comment,
                    published));
            }
        }

        return entries;
    }
}
