namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// What one model said about one card, and what happened when it was checked: the raw text, the
/// definition if it parsed, the lint report if there was something to lint, and whether this is
/// the repair round's answer.
/// </summary>
public sealed record TranslationReading(
    string OracleId,
    string Name,
    string Model,
    string PromptVersion,
    string Raw,
    CardDefinition? Definition,
    string? ParseError,
    LintReport? Lint,
    bool Repaired,
    string? BatchId)
{
    /// <summary>A reading is usable when it parsed and the linter found no errors.</summary>
    public bool Ok => Definition is not null && Lint is { HasErrors: false };
}

/// <summary>
/// How the stage runs. <paramref name="PollDelay"/> is injected so tests wait for nothing;
/// <paramref name="BatchSize"/> is clamped to the API's per-batch limit.
/// </summary>
public sealed record TranslateStageOptions(TimeSpan PollDelay, int BatchSize = TranslateStage.MaxBatchSize, bool Repair = true, int MaxPolls = 2000);

/// <summary>
/// The translation stage: corpus cards in, readings out. It submits only what it has never
/// read, records every batch id before it waits on it, stores every raw answer before anything
/// tries to parse it, then parses, lints, and asks at most one repair per reading that failed
/// either. A batch that was recorded and not collected is collected on the next run rather
/// than resubmitted, so a killed run costs nothing but the wait.
/// </summary>
public sealed class TranslateStage(ITranslationClient client, TranslationStore store, TranslateStageOptions options)
{
    /// <summary>The Message Batches API takes at most this many requests in one batch.</summary>
    public const int MaxBatchSize = 1000;

    public async Task<IReadOnlyList<TranslationReading>> Run(
        IReadOnlyList<CorpusCard> cards,
        IReadOnlyList<string> models,
        string promptVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(models);

        var system = TranslationPrompt.SystemWithVocabulary();
        var size = Math.Clamp(options.BatchSize, 1, MaxBatchSize);

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            await CollectPending(model, promptVersion, ct);

            var pending = cards.Where(c => store.Reading(c.OracleId, model, promptVersion) is null).ToList();
            foreach (var chunk in pending.Chunk(size))
            {
                ct.ThrowIfCancellationRequested();
                var requests = chunk
                    .Select(c => (customId: c.OracleId, system, user: TranslationPrompt.UserMessage(c)))
                    .ToList();
                var batchId = await client.SubmitBatch(model, requests, ct);
                store.WriteBatch(new StoredBatch(batchId, model, promptVersion, [.. chunk.Select(c => c.OracleId)], Collected: false));
                await Collect(batchId, model, promptVersion, ct);
            }
        }

        var readings = new List<TranslationReading>();
        foreach (var card in cards)
        {
            foreach (var model in models)
                readings.Add(await Evaluate(card, model, promptVersion, system, ct));
        }

        return readings;
    }

    /// <summary>A batch this store submitted and never collected: poll it and collect it now.</summary>
    private async Task CollectPending(string model, string promptVersion, CancellationToken ct)
    {
        foreach (var batch in store.Batches())
        {
            if (batch.Collected
                || !string.Equals(batch.Model, model, StringComparison.Ordinal)
                || !string.Equals(batch.PromptVersion, promptVersion, StringComparison.Ordinal))
            {
                continue;
            }

            await Collect(batch.BatchId, model, promptVersion, ct);
        }
    }

    /// <summary>Wait for the batch to end, store every answer raw, then mark it collected.</summary>
    private async Task Collect(string batchId, string model, string promptVersion, CancellationToken ct)
    {
        var polls = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = await client.Poll(batchId, ct);
            if (status.Ended)
                break;
            if (++polls > options.MaxPolls)
                throw new InvalidOperationException("Batch " + batchId + " did not end within the poll budget.");
            if (options.PollDelay > TimeSpan.Zero)
                await Task.Delay(options.PollDelay, ct);
        }

        await foreach (var item in client.Results(batchId, ct))
        {
            store.WriteReading(new StoredReading(
                item.CustomId,
                model,
                promptVersion,
                item.Text,
                item.InputTokens,
                item.OutputTokens,
                item.CacheReadTokens,
                item.CacheWriteTokens,
                batchId,
                item.Error));
        }

        var stored = store.Batch(batchId);
        if (stored is not null)
            store.WriteBatch(stored with { Collected = true });
    }

    /// <summary>Parse and lint one stored reading, repairing once if it failed either.</summary>
    private async Task<TranslationReading> Evaluate(CorpusCard card, string model, string promptVersion, string system, CancellationToken ct)
    {
        var stored = store.Reading(card.OracleId, model, promptVersion);
        if (stored is null)
        {
            return new TranslationReading(
                card.OracleId, card.Name, model, promptVersion, string.Empty,
                null, "no reading was stored for this card and model", null, false, null);
        }

        var reading = Judge(card, stored, repaired: false);
        if (reading.Ok || !options.Repair)
            return reading;

        var repair = store.RepairReading(card.OracleId, model, promptVersion);
        if (repair is null)
        {
            ct.ThrowIfCancellationRequested();
            var item = await client.Repair(model, system, TranslationPrompt.UserMessage(card), stored.Text, ProblemOf(reading), ct);

            // A repair the transport could not make is not stored: the unrepaired reading is
            // judged as it stands, and the next run with a working transport repairs it.
            if (item.Error is not null)
                return reading;

            repair = new StoredReading(
                card.OracleId, model, promptVersion, item.Text,
                item.InputTokens, item.OutputTokens, item.CacheReadTokens, item.CacheWriteTokens,
                stored.BatchId, item.Error, Repaired: true);
            store.WriteRepair(repair);
        }

        return Judge(card, repair, repaired: true);
    }

    private static TranslationReading Judge(CorpusCard card, StoredReading stored, bool repaired)
    {
        var (definition, error) = TranslationPrompt.Parse(stored.Text);
        var lint = definition is null ? null : Linter.Check(FileFor(card, definition, stored));
        return new TranslationReading(
            card.OracleId,
            card.Name,
            stored.Model,
            stored.PromptVersion,
            stored.Text,
            definition,
            stored.Error ?? error,
            lint,
            repaired,
            stored.BatchId);
    }

    /// <summary>The file the linter judges: one model's reading, so provenance says which and at what prompt.</summary>
    private static CardFile FileFor(CorpusCard card, CardDefinition definition, StoredReading stored) => new(
        card.OracleId,
        card.Name,
        card.OracleHash,
        IrVersion.Current,
        definition,
        ImplementationStatus.Translated,
        new TranslatedProvenance([stored.Model], stored.PromptVersion, Agreed: false, stored.BatchId),
        card.Keywords);

    /// <summary>What the repair round is told: the parse error, or every lint error.</summary>
    private static string ProblemOf(TranslationReading reading)
    {
        if (reading.Definition is null)
            return reading.ParseError ?? "the answer was not a CardDefinition";
        var errors = (reading.Lint?.Findings ?? [])
            .Where(f => f.Severity == LintSeverity.Error)
            .Select(f => f.Code + ": " + f.Message);
        return string.Join("; ", errors);
    }
}
