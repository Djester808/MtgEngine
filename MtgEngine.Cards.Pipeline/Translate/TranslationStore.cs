using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>
/// One raw reading as it came off the wire, before anything tried to parse it. Kept so a run
/// is resumable, so a prompt change never mixes with an older answer, and so a disputed card
/// can be argued from what the model actually said.
/// </summary>
public sealed record StoredReading(
    string OracleId,
    string Model,
    string PromptVersion,
    string Text,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string? BatchId = null,
    string? Error = null,
    bool Repaired = false);

/// <summary>
/// A batch that was submitted. <paramref name="Collected"/> is the resumability flag: a batch
/// recorded and not collected is polled and collected on the next run, never resubmitted.
/// </summary>
public sealed record StoredBatch(
    string BatchId,
    string Model,
    string PromptVersion,
    IReadOnlyList<string> OracleIds,
    bool Collected);

/// <summary>
/// The directory the translation stage keeps its work in: raw readings at
/// <c>&lt;oracleId&gt;/&lt;model&gt;/&lt;promptVersion&gt;.json</c>, a repair round beside its
/// reading as <c>.repair.json</c>, and submitted batch metadata under <c>batches/</c>. Reads
/// are idempotent — nothing here mutates on a read, and a second write of the same answer
/// leaves the same file.
/// </summary>
public sealed class TranslationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public TranslationStore(string root) => Root = root;

    /// <summary>The default store: machine-local agent state, never committed (see .gitignore).</summary>
    public static string DefaultRoot(string repoRoot) => Path.Combine(repoRoot, ".agents", "translations");

    public string Root { get; }

    public string BatchesDirectory => Path.Combine(Root, "batches");

    public string ReadingPath(string oracleId, string model, string promptVersion) =>
        Path.Combine(Root, Safe(oracleId), Safe(model), Safe(promptVersion) + ".json");

    public string RepairPath(string oracleId, string model, string promptVersion) =>
        Path.Combine(Root, Safe(oracleId), Safe(model), Safe(promptVersion) + ".repair.json");

    public string BatchPath(string batchId) => Path.Combine(BatchesDirectory, Safe(batchId) + ".json");

    /// <summary>The stored first answer, or null when this card has not been read by this model at this prompt version.</summary>
    public StoredReading? Reading(string oracleId, string model, string promptVersion) =>
        ReadFile<StoredReading>(ReadingPath(oracleId, model, promptVersion));

    /// <summary>The stored repair round, if one has already been asked for.</summary>
    public StoredReading? RepairReading(string oracleId, string model, string promptVersion) =>
        ReadFile<StoredReading>(RepairPath(oracleId, model, promptVersion));

    public void WriteReading(StoredReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        WriteFile(ReadingPath(reading.OracleId, reading.Model, reading.PromptVersion), reading);
    }

    public void WriteRepair(StoredReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        WriteFile(RepairPath(reading.OracleId, reading.Model, reading.PromptVersion), reading with { Repaired = true });
    }

    /// <summary>
    /// Drop the stored first answer and its repair, so the next collect reads this card again
    /// from its batch: the road for a local batch whose answer files were replaced in place.
    /// </summary>
    public void ForgetReading(string oracleId, string model, string promptVersion)
    {
        File.Delete(ReadingPath(oracleId, model, promptVersion));
        File.Delete(RepairPath(oracleId, model, promptVersion));
    }

    public StoredBatch? Batch(string batchId) => ReadFile<StoredBatch>(BatchPath(batchId));

    public void WriteBatch(StoredBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        WriteFile(BatchPath(batch.BatchId), batch);
    }

    /// <summary>Every batch this store knows about, oldest file first, so a resumed run collects in submission order.</summary>
    public IReadOnlyList<StoredBatch> Batches()
    {
        if (!Directory.Exists(BatchesDirectory))
            return [];

        var batches = new List<StoredBatch>();
        foreach (var path in Directory.EnumerateFiles(BatchesDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var batch = ReadFile<StoredBatch>(path);
            if (batch is not null)
                batches.Add(batch);
        }

        return batches;
    }

    private static T? ReadFile<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            // A half-written file from a killed run is treated as absent: the work is redone,
            // which is cheaper than reasoning about a torn answer.
            return null;
        }
    }

    private static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    /// <summary>Model names and prompt versions become directory names, so anything path-shaped is flattened.</summary>
    private static string Safe(string segment)
    {
        var text = string.IsNullOrWhiteSpace(segment) ? "unknown" : segment;
        foreach (var bad in Path.GetInvalidFileNameChars())
            text = text.Replace(bad, '_');
        return text;
    }
}
