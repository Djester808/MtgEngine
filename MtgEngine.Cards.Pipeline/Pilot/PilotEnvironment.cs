using System.Text.Json;
using Anthropic;
using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline;

/// <summary>One selected face, as <c>pilot-submit</c> records it so <c>pilot-collect</c> can find it again.</summary>
public sealed record PilotCard(string OracleId, string Name);

/// <summary>
/// What <c>pilot-submit</c> leaves in the out directory for <c>pilot-collect</c>: one batch id
/// per model, the faces that were sent, and the prompt version they were sent at.
///
/// The wave-0 pilot wrote the same two files with the same shapes — <c>batches.json</c> is
/// <c>{"model":"batchId"}</c> and <c>selection.json</c> a list of oracle ids and names — so a
/// batch it submitted is collectable by this command. Only <c>prompt-version.txt</c> is new,
/// and its absence means "the version this build asks for", which is what the old pilot used.
/// </summary>
public sealed record PilotSubmission(
    string? PromptVersion,
    IReadOnlyDictionary<string, string> Batches,
    IReadOnlyList<PilotCard> Cards)
{
    public const string BatchesFile = "batches.json";
    public const string SelectionFile = "selection.json";
    public const string PromptVersionFile = "prompt-version.txt";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public IReadOnlyList<string> OracleIds => [.. Cards.Select(c => c.OracleId)];

    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, BatchesFile), JsonSerializer.Serialize(Batches, Options));
        File.WriteAllText(Path.Combine(directory, SelectionFile), JsonSerializer.Serialize(Cards, Options));
        if (PromptVersion is not null)
            File.WriteAllText(Path.Combine(directory, PromptVersionFile), PromptVersion);
    }

    /// <summary>The submission in this directory, or null when nothing was submitted from it.</summary>
    public static PilotSubmission? Read(string directory)
    {
        var batchesPath = Path.Combine(directory, BatchesFile);
        var selectionPath = Path.Combine(directory, SelectionFile);
        if (!File.Exists(batchesPath) || !File.Exists(selectionPath))
            return null;

        var batches = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(batchesPath), Options) ?? [];
        var cards = JsonSerializer.Deserialize<List<PilotCard>>(File.ReadAllText(selectionPath), Options) ?? [];
        var versionPath = Path.Combine(directory, PromptVersionFile);
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;
        return new PilotSubmission(string.IsNullOrEmpty(version) ? null : version, batches, cards);
    }
}

/// <summary>
/// Everything the pilot talks to, injected: the transport, the translation stage's store, the
/// only writer of <c>cards/</c>, and the way an oracle id becomes a corpus card. A test supplies
/// a fake client, scratch directories and two made-up cards; nothing in the pilot itself reaches
/// the network, the corpus or a key.
/// </summary>
public sealed record PilotEnvironment(
    ITranslationClient Client,
    TranslationStore Store,
    CardFileWriter Writer,
    Func<IReadOnlyList<string>, IReadOnlyList<CorpusCard>> Cards,
    IReadOnlyList<string> Models,
    string PromptVersion,
    TextWriter Log,
    bool Repair = true)
{
    /// <summary>The real one: the Anthropic transport, the shared translation store, and <c>cards/</c>.</summary>
    public static PilotEnvironment ForRepo(string repoRoot, bool repair)
    {
        var client = new AnthropicClient { ApiKey = Secrets.AnthropicApiKey() };
        return new PilotEnvironment(
            new AnthropicTranslationClient(client),
            new TranslationStore(TranslationStore.DefaultRoot(repoRoot)),
            new CardFileWriter(Path.Combine(repoRoot, "cards")),
            ids => FromCorpus(repoRoot, ids),
            Pilot.Models,
            TranslationPrompt.VersionOf("pilot"),
            Console.Out,
            repair);
    }

    /// <summary>
    /// The offline one: answers come from files under <c>{outDir}/local</c> written by whoever
    /// translates (an agent on the subscription, a person), never from the API. Same store, same
    /// writer, same models as labels, no repair (a translator rewrites the answer file instead).
    /// </summary>
    public static PilotEnvironment Local(string repoRoot, string outDir) =>
        new(
            new FileTranslationClient(Path.Combine(outDir, "local")),
            new TranslationStore(TranslationStore.DefaultRoot(repoRoot)),
            new CardFileWriter(Path.Combine(repoRoot, "cards")),
            ids => FromCorpus(repoRoot, ids),
            Pilot.Models,
            TranslationPrompt.VersionOf("pilot"),
            Console.Out,
            Repair: false);

    /// <summary>The deck faces the pilot may choose from, most-played first, as the plan orders them.</summary>
    public static IEnumerable<CorpusCard> DeckCandidates(string repoRoot)
    {
        var raw = Corpus.LoadRaw(Corpus.DefaultPath(repoRoot));
        foreach (var id in Corpus.DeckOracleIds(Path.Combine(repoRoot, "MtgEngine.Api", "mtgengine.db")))
        {
            var card = Corpus.Card(raw, id);
            if (card is not null)
                yield return card;
        }
    }

    private static IReadOnlyList<CorpusCard> FromCorpus(string repoRoot, IReadOnlyList<string> ids)
    {
        var raw = Corpus.LoadRaw(Corpus.DefaultPath(repoRoot));
        var cards = new List<CorpusCard>();
        foreach (var id in ids)
        {
            var card = Corpus.Card(raw, id);
            if (card is not null)
                cards.Add(card);
        }

        return cards;
    }
}
