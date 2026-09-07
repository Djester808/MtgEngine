using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline;

/// <summary>What the pilot learned about one card: every reading, what agreement decided, and what was written.</summary>
public sealed record PilotCardResult(
    CorpusCard Card,
    IReadOnlyList<TranslationReading> Readings,
    AgreeResult Agreement,
    CardWrite? Written);

/// <summary>
/// The decision point of CARD_ENGINE_PLAN.md § "Decision points": a slice of deck faces through
/// both translators, then the agreement rate, the parse and lint failure rates and the cost per
/// face. It owns none of that work — <see cref="TranslateStage"/> translates, repairs and lints,
/// <see cref="AgreeStage"/> decides the status and <see cref="CardFileWriter"/> writes
/// <c>cards/</c> — so what the pilot measures is what the pipeline does, not a second
/// implementation of it that can drift.
///
/// It is two commands, because a batch takes hours and a process that waits for one holds the
/// pipeline dll and blocks every other build in the repository. <see cref="SubmitAsync"/> sends
/// the batches and exits; <see cref="CollectAsync"/> checks their status once and either exits
/// <see cref="NotEnded"/> or does the whole of the rest.
/// </summary>
public static class Pilot
{
    public static readonly IReadOnlyList<string> Models = ["claude-opus-5", "claude-sonnet-5"];

    /// <summary>Collect's exit code when a batch has not ended. Nothing is written; run it again later.</summary>
    public const int NotEnded = 3;

    /// <summary>Nothing to do: no faces to submit, or no submission to collect.</summary>
    public const int NothingToDo = 64;

    /// <summary>
    /// The faces the pilot measures: deck cards with printed text, skipping the free slice, which
    /// P2's templates describe from the JSON alone and no model needs to read.
    /// </summary>
    public static IReadOnlyList<CorpusCard> Select(IEnumerable<CorpusCard> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var chosen = new List<CorpusCard>();
        foreach (var card in candidates)
        {
            if (card.IsFreeSlice || card.Faces.All(f => f.OracleText.Length == 0))
                continue;
            chosen.Add(card);
            if (chosen.Count >= count)
                break;
        }

        return chosen;
    }

    /// <summary>
    /// Submit one batch per model and exit. The batch ids are recorded in the translation store
    /// before anything waits on them — that is what makes the collect command able to resume —
    /// and in the out directory, which is what a person reads.
    /// </summary>
    public static async Task<int> SubmitAsync(PilotEnvironment env, IReadOnlyList<CorpusCard> cards, string outDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0)
        {
            env.Log.WriteLine("pilot-submit: no faces selected; nothing submitted.");
            return NothingToDo;
        }

        if (cards.Count > TranslateStage.MaxBatchSize)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The pilot submits one batch per model and a batch takes at most {TranslateStage.MaxBatchSize} requests; {cards.Count} were selected."),
                nameof(cards));
        }

        var system = TranslationPrompt.SystemWithVocabulary();
        var oracleIds = cards.Select(c => c.OracleId).ToList();
        var batches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in env.Models)
        {
            ct.ThrowIfCancellationRequested();
            var requests = cards
                .Select(c => (customId: c.OracleId, system, user: TranslationPrompt.UserMessage(c)))
                .ToList();
            var batchId = await env.Client.SubmitBatch(model, requests, ct);
            env.Store.WriteBatch(new StoredBatch(batchId, model, env.PromptVersion, oracleIds, Collected: false));
            batches[model] = batchId;
            env.Log.WriteLine(FormattableString.Invariant($"submitted {cards.Count} requests to {model}: batch {batchId}"));
        }

        new PilotSubmission(env.PromptVersion, batches, [.. cards.Select(c => new PilotCard(c.OracleId, c.Name))]).Write(outDir);
        env.Log.WriteLine(FormattableString.Invariant($"pilot-submit: {cards.Count} faces, {batches.Count} batches, recorded in {outDir}. Collect with `pipeline pilot-collect {outDir}`."));
        return 0;
    }

    /// <summary>
    /// Check each batch once. If any has not ended, print its status and exit <see cref="NotEnded"/>
    /// having written nothing — the caller decides when to look again, so no process sleeps on a
    /// batch. When all of them have ended: collect, repair once where a reading needs it, agree,
    /// write the card files and the report.
    /// </summary>
    /// <param name="again">
    /// Read every answer of the submission's batches again, forgetting the readings stored from
    /// the first collect: a local batch is a directory whose answer files a later translator may
    /// have replaced, and a batch once marked collected is otherwise never read twice.
    /// </param>
    public static async Task<int> CollectAsync(PilotEnvironment env, string outDir, CancellationToken ct, bool again = false)
    {
        ArgumentNullException.ThrowIfNull(env);
        var submission = PilotSubmission.Read(outDir);
        if (submission is null || submission.Batches.Count == 0 || submission.Cards.Count == 0)
        {
            env.Log.WriteLine(FormattableString.Invariant($"pilot-collect: no submission in {outDir}; run `pipeline pilot-submit` first."));
            return NothingToDo;
        }

        var promptVersion = submission.PromptVersion ?? env.PromptVersion;
        Record(env, submission, promptVersion);
        if (again)
            Forget(env, submission, promptVersion);
        if (!await Ended(env, submission, ct))
            return NotEnded;

        var cards = env.Cards(submission.OracleIds);
        if (cards.Count == 0)
        {
            env.Log.WriteLine("pilot-collect: none of the submitted oracle ids resolved to a card.");
            return NothingToDo;
        }

        var stage = new TranslateStage(
            env.Client,
            env.Store,
            new TranslateStageOptions(TimeSpan.Zero, Repair: env.Repair, MaxPolls: 1));
        var readings = await stage.Run(cards, env.Models, promptVersion, ct);

        var results = Decide(env, cards, readings, promptVersion);
        var report = PilotReport.Build(results, env, promptVersion);
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), report, ct);
        env.Log.Write(report);
        return 0;
    }

    /// <summary>
    /// A batch the store has never heard of was submitted by the wave-0 pilot, whose record was
    /// the out directory rather than the store. Recording it is what lets the stage collect it.
    /// </summary>
    private static void Record(PilotEnvironment env, PilotSubmission submission, string promptVersion)
    {
        foreach (var (model, batchId) in submission.Batches)
        {
            if (env.Store.Batch(batchId) is null)
                env.Store.WriteBatch(new StoredBatch(batchId, model, promptVersion, submission.OracleIds, Collected: false));
        }
    }

    /// <summary>
    /// Un-collect the submission's batches: every stored reading of theirs is dropped and each
    /// batch is marked uncollected, so the stage reads the answers as they are now.
    /// </summary>
    private static void Forget(PilotEnvironment env, PilotSubmission submission, string promptVersion)
    {
        foreach (var (model, batchId) in submission.Batches)
        {
            if (env.Store.Batch(batchId) is not { } batch)
                continue;
            foreach (var oracleId in batch.OracleIds)
                env.Store.ForgetReading(oracleId, model, promptVersion);
            env.Store.WriteBatch(batch with { Collected = false });
            env.Log.WriteLine(FormattableString.Invariant($"{model} {batchId}: reading its {batch.OracleIds.Count} answers again"));
        }
    }

    /// <summary>One status call per uncollected batch — the batch's own status check, and no loop around it.</summary>
    private static async Task<bool> Ended(PilotEnvironment env, PilotSubmission submission, CancellationToken ct)
    {
        var ended = true;
        foreach (var (model, batchId) in submission.Batches)
        {
            ct.ThrowIfCancellationRequested();
            if (env.Store.Batch(batchId) is { Collected: true })
                continue;

            var status = await env.Client.Poll(batchId, ct);
            env.Log.WriteLine(FormattableString.Invariant(
                $"{model} {batchId}: {(status.Ended ? "ended" : "in_progress")} succeeded={status.Succeeded} errored={status.Errored} processing={status.Processing}"));
            ended &= status.Ended;
        }

        return ended;
    }

    /// <summary>
    /// One agreement decision per card, from the readings the stage produced. The lint report the
    /// stage already computed is passed on rather than recomputed, and it linted each reading on
    /// its own as <see cref="ImplementationStatus.Translated"/>: a lone reading linted as Disputed
    /// raises L013 on every reading and no card could ever agree (seam-requests/DECISIONS.md, P3).
    /// </summary>
    private static IReadOnlyList<PilotCardResult> Decide(
        PilotEnvironment env,
        IReadOnlyList<CorpusCard> cards,
        IReadOnlyList<TranslationReading> readings,
        string promptVersion)
    {
        var results = new List<PilotCardResult>();
        foreach (var card in cards)
        {
            var mine = readings.Where(r => string.Equals(r.OracleId, card.OracleId, StringComparison.Ordinal)).ToList();
            var request = new AgreeRequest(
                card.OracleId,
                card.Name,
                card.OracleHash,
                promptVersion,
                [.. env.Models.Select(m => Reading(mine, m))],
                card.Keywords);
            var agreement = AgreeStage.Agree(request);
            results.Add(new PilotCardResult(card, mine, agreement, env.Writer.Write(agreement)));
        }

        return results;
    }

    private static ModelReading Reading(IReadOnlyList<TranslationReading> readings, string model)
    {
        var reading = readings.FirstOrDefault(r => string.Equals(r.Model, model, StringComparison.Ordinal));
        return reading is null
            ? new ModelReading(model, null, "no reading came back for this model")
            : new ModelReading(model, reading.Definition, reading.ParseError, reading.Lint);
    }
}
