using System.Runtime.CompilerServices;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>One batch the pilot submitted: which model it went to and what was in it.</summary>
internal sealed record PilotSubmittedBatch(string Model, string BatchId, IReadOnlyList<string> OracleIds);

/// <summary>One repair round the pilot asked for, kept so a test can prove it happened exactly once.</summary>
internal sealed record PilotRepairCall(string Model, string User, string Problem);

/// <summary>
/// A translation client that answers from fixtures written in the test, per model, so two models
/// can be made to agree or to differ. No network and no key: CARD_ENGINE_PLAN.md forbids a test
/// that reaches the API, and the pilot is the thing under test.
/// </summary>
internal sealed class PilotFakeClient : ITranslationClient
{
    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _repairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Model, List<string> OracleIds)> _batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _polls = new(StringComparer.Ordinal);
    private int _nextBatch;

    public List<PilotSubmittedBatch> Submissions { get; } = [];

    public List<PilotRepairCall> Repairs { get; } = [];

    /// <summary>How many status checks a batch reports as still processing before it ends.</summary>
    public int PollsBeforeEnd { get; set; }

    /// <summary>How many status checks have been made, across every batch.</summary>
    public int PollCount { get; private set; }

    /// <summary>What this model says about this card in the batch.</summary>
    public void Answer(string model, string oracleId, string text) => _answers[Key(model, oracleId)] = text;

    /// <summary>What this model says when its first answer is sent back to it.</summary>
    public void RepairWith(string model, string oracleId, string text) => _repairs[Key(model, oracleId)] = text;

    public Task<string> SubmitBatch(string model, IReadOnlyList<(string customId, string system, string user)> requests, CancellationToken ct)
    {
        var batchId = "pilot-batch-" + (++_nextBatch).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ids = requests.Select(r => r.customId).ToList();
        _batches[batchId] = (model, ids);
        Submissions.Add(new(model, batchId, ids));
        return Task.FromResult(batchId);
    }

    public Task<BatchStatus> Poll(string batchId, CancellationToken ct)
    {
        PollCount++;
        _polls.TryGetValue(batchId, out var seen);
        _polls[batchId] = seen + 1;
        var count = _batches.TryGetValue(batchId, out var batch) ? batch.OracleIds.Count : 0;
        var ended = seen >= PollsBeforeEnd;
        return Task.FromResult(new BatchStatus(ended, ended ? count : 0, 0, ended ? 0 : count));
    }

    public async IAsyncEnumerable<BatchItem> Results(string batchId, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (!_batches.TryGetValue(batchId, out var batch))
            yield break;

        foreach (var oracleId in batch.OracleIds)
        {
            ct.ThrowIfCancellationRequested();
            yield return _answers.TryGetValue(Key(batch.Model, oracleId), out var text)
                ? new BatchItem(oracleId, text, 100, 20, 9000, 0)
                : new BatchItem(oracleId, string.Empty, 0, 0, 0, 0, "no fixture for " + oracleId);
        }
    }

    public Task<BatchItem> Repair(string model, string system, string user, string previousAnswer, string problem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        Repairs.Add(new(model, user, problem));
        var match = _repairs.FirstOrDefault(kv =>
            kv.Key.StartsWith(model + "|", StringComparison.Ordinal)
            && user.Contains(kv.Key[(model.Length + 1)..], StringComparison.Ordinal));
        return Task.FromResult(match.Value is null
            ? new BatchItem("repair", string.Empty, 0, 0, 0, 0, "no repair fixture")
            : new BatchItem("repair", match.Value, 40, 10, 9000, 0));
    }

    private static string Key(string model, string oracleId) => model + "|" + oracleId;
}
