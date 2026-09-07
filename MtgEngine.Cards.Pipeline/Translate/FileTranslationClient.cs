using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// The translation transport when the answers come from people or agents rather than the
/// Anthropic API: a batch is a directory of request files, an answer is a file written beside
/// them, and nothing here reaches the network or a key. <c>SubmitBatch</c> writes
/// <c>{batch}/system.md</c> once and <c>{batch}/requests/{customId}.md</c> per card;
/// whoever translates writes <c>{batch}/answers/{customId}.json</c> holding only the card
/// definition; <c>Poll</c> reports the batch ended when every request has its answer, and
/// <c>Results</c> streams the answers as the stage expects them. Repair is not available
/// offline: the stage keeps the unrepaired reading, which is what a translator then fixes by
/// rewriting the answer file and collecting again.
/// </summary>
public sealed class FileTranslationClient(string root) : ITranslationClient
{
    public const string SystemFile = "system.md";
    public const string RequestsDirectory = "requests";
    public const string AnswersDirectory = "answers";
    public const string BatchFile = "batch.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>The batch directory: what a translator opens.</summary>
    public string Directory(string batchId) => Path.Combine(root, batchId);

    public Task<string> SubmitBatch(string model, IReadOnlyList<(string customId, string system, string user)> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        System.IO.Directory.CreateDirectory(root);
        var number = System.IO.Directory.GetDirectories(root).Length + 1;
        var batchId = FormattableString.Invariant($"local-{model}-{number:000}");
        var directory = Directory(batchId);
        System.IO.Directory.CreateDirectory(Path.Combine(directory, RequestsDirectory));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, AnswersDirectory));

        File.WriteAllText(Path.Combine(directory, SystemFile), requests.Count > 0 ? requests[0].system : string.Empty);
        foreach (var (customId, _, user) in requests)
        {
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(directory, RequestsDirectory, customId + ".md"), user);
        }

        File.WriteAllText(
            Path.Combine(directory, BatchFile),
            JsonSerializer.Serialize(new LocalBatch(batchId, model, [.. requests.Select(r => r.customId)]), Options));
        return Task.FromResult(batchId);
    }

    public Task<BatchStatus> Poll(string batchId, CancellationToken ct)
    {
        var batch = Read(batchId);
        var answered = batch.CustomIds.Count(id => File.Exists(AnswerPath(batchId, id)));
        var missing = batch.CustomIds.Count - answered;
        return Task.FromResult(new BatchStatus(missing == 0, answered, 0, missing));
    }

    public async IAsyncEnumerable<BatchItem> Results(string batchId, [EnumeratorCancellation] CancellationToken ct)
    {
        var batch = Read(batchId);
        foreach (var customId in batch.CustomIds)
        {
            ct.ThrowIfCancellationRequested();
            var path = AnswerPath(batchId, customId);
            yield return File.Exists(path)
                ? new BatchItem(customId, await File.ReadAllTextAsync(path, ct), 0, 0, 0, 0)
                : new BatchItem(customId, string.Empty, 0, 0, 0, 0, "no answer file was written for this card");
        }
    }

    /// <summary>Offline there is nobody to ask again; the stage keeps the reading it has and a translator rewrites the answer file.</summary>
    public Task<BatchItem> Repair(string model, string system, string user, string previousAnswer, string problem, CancellationToken ct) =>
        Task.FromResult(new BatchItem(string.Empty, string.Empty, 0, 0, 0, 0, "repair is not available offline: rewrite the answer file and collect again"));

    /// <summary>The answers still owed for a batch, by custom id, so a collect can say what is missing.</summary>
    public IReadOnlyList<string> Missing(string batchId) =>
        [.. Read(batchId).CustomIds.Where(id => !File.Exists(AnswerPath(batchId, id)))];

    private string AnswerPath(string batchId, string customId) => Path.Combine(Directory(batchId), AnswersDirectory, customId + ".json");

    private LocalBatch Read(string batchId)
    {
        var path = Path.Combine(Directory(batchId), BatchFile);
        if (!File.Exists(path))
            throw new InvalidOperationException("No local batch " + batchId + " under " + root + ".");
        return JsonSerializer.Deserialize<LocalBatch>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("The local batch file for " + batchId + " is empty.");
    }

    /// <summary>What a local batch remembers: its id, the model label its answers count as, and the cards it asked about.</summary>
    public sealed record LocalBatch(string BatchId, string Model, IReadOnlyList<string> CustomIds);
}
