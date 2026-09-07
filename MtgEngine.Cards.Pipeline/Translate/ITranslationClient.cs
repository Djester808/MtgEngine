namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// Where a submitted batch has got to. <paramref name="Ended"/> is the only state that lets
/// results be collected; the counts are what the stage prints while it waits.
/// </summary>
public sealed record BatchStatus(bool Ended, long Succeeded, long Errored, long Processing);

/// <summary>
/// One answer, from a batch or from a repair call: the raw text the model produced and what it
/// cost. <paramref name="Error"/> is set when the request itself failed, in which case
/// <paramref name="Text"/> is empty.
/// </summary>
public sealed record BatchItem(
    string CustomId,
    string Text,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string? Error = null);

/// <summary>
/// The transport the translation stage speaks: submit a batch, poll it, stream its results,
/// and ask one repair question. Everything model-shaped lives behind this, so
/// <see cref="TranslateStage"/> is testable without the network and without a key —
/// CARD_ENGINE_PLAN.md § "Wave 1": no test may reach the API.
/// </summary>
public interface ITranslationClient
{
    /// <summary>Submit one Message Batch and return its id. The system text is the cached prefix.</summary>
    Task<string> SubmitBatch(string model, IReadOnlyList<(string customId, string system, string user)> requests, CancellationToken ct);

    /// <summary>Where the batch is. The stage polls this until <see cref="BatchStatus.Ended"/>.</summary>
    Task<BatchStatus> Poll(string batchId, CancellationToken ct);

    /// <summary>The answers, streamed, one per request that was submitted.</summary>
    IAsyncEnumerable<BatchItem> Results(string batchId, CancellationToken ct);

    /// <summary>One repair round for an answer that did not parse or lint: the problem goes back with the original question.</summary>
    Task<BatchItem> Repair(string model, string system, string user, string previousAnswer, string problem, CancellationToken ct);
}
