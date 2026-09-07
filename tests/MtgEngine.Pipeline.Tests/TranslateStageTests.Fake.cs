using System.Runtime.CompilerServices;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>One submitted batch, kept so a test can prove what was asked and how it was chunked.</summary>
internal sealed record SubmittedBatch(string Model, string BatchId, IReadOnlyList<(string CustomId, string System, string User)> Requests);

/// <summary>One repair round, kept so a test can prove it happened once and what it was told.</summary>
internal sealed record RepairCall(string Model, string User, string PreviousAnswer, string Problem);

/// <summary>
/// A translation client that answers from hand-written fixtures. No network, no key: the stage
/// is the thing under test, and CARD_ENGINE_PLAN.md forbids a test that reaches the API.
/// </summary>
internal sealed class FakeTranslationClient : ITranslationClient
{
    private readonly Dictionary<string, string> answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> repairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> polls = new(StringComparer.Ordinal);
    private int nextBatch;

    public List<SubmittedBatch> Submissions { get; } = [];

    public List<RepairCall> Repairs { get; } = [];

    /// <summary>How many polls a batch reports as still processing before it ends.</summary>
    public int PollsBeforeEnd { get; set; }

    /// <summary>When set, every repair call comes back as an item with an error, the way a refused request does.</summary>
    public bool RepairsFail { get; set; }

    /// <summary>The first answer this model run gives for a card.</summary>
    public void Answer(string oracleId, string text) => answers[oracleId] = text;

    /// <summary>The answer the repair round gives for a card.</summary>
    public void RepairWith(string oracleId, string text) => repairs[oracleId] = text;

    /// <summary>A batch the store already holds and this run has to collect rather than resubmit.</summary>
    public void PrepareBatch(string batchId, params string[] oracleIds) => batches[batchId] = [.. oracleIds];

    public Task<string> SubmitBatch(string model, IReadOnlyList<(string customId, string system, string user)> requests, CancellationToken ct)
    {
        var batchId = "batch-" + (++nextBatch).ToString(System.Globalization.CultureInfo.InvariantCulture);
        batches[batchId] = [.. requests.Select(r => r.customId)];
        Submissions.Add(new(model, batchId, [.. requests.Select(r => (r.customId, r.system, r.user))]));
        return Task.FromResult(batchId);
    }

    public Task<BatchStatus> Poll(string batchId, CancellationToken ct)
    {
        polls.TryGetValue(batchId, out var seen);
        polls[batchId] = seen + 1;
        var count = batches.TryGetValue(batchId, out var ids) ? ids.Count : 0;
        var ended = seen >= PollsBeforeEnd;
        return Task.FromResult(new BatchStatus(ended, ended ? count : 0, 0, ended ? 0 : count));
    }

    public async IAsyncEnumerable<BatchItem> Results(string batchId, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (!batches.TryGetValue(batchId, out var ids))
            yield break;

        foreach (var oracleId in ids)
        {
            ct.ThrowIfCancellationRequested();
            yield return answers.TryGetValue(oracleId, out var text)
                ? new BatchItem(oracleId, text, 100, 20, 9000, 0)
                : new BatchItem(oracleId, string.Empty, 0, 0, 0, 0, "no fixture for " + oracleId);
        }
    }

    public Task<BatchItem> Repair(string model, string system, string user, string previousAnswer, string problem, CancellationToken ct)
    {
        Repairs.Add(new(model, user, previousAnswer, problem));
        if (RepairsFail)
            return Task.FromResult(new BatchItem("repair", string.Empty, 0, 0, 0, 0, "repair unavailable: test"));
        var match = repairs.FirstOrDefault(kv => user.Contains(kv.Key, StringComparison.Ordinal));
        return Task.FromResult(match.Value is null
            ? new BatchItem("repair", string.Empty, 0, 0, 0, 0, "no repair fixture")
            : new BatchItem("repair", match.Value, 120, 30, 9000, 0));
    }
}
