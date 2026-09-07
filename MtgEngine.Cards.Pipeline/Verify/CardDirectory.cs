namespace MtgEngine.Cards.Pipeline.Verify;

/// <summary>
/// Every card file in one directory, read once, and the definition store the runner resolves a
/// scenario's card names through.
/// </summary>
/// <remarks>
/// The API has its own reader (<c>CardFilePool</c>) because it serves a running server; this one
/// exists because the pipeline cannot reference the API. Both read the same files the same way
/// and neither writes them — <c>CardFileWriter</c> is still the only writer of <c>cards/</c>.
/// A file that will not parse is collected in <see cref="Unreadable"/> rather than thrown, so one
/// bad file does not stop a verification run over thousands of good ones; the run reports them.
/// </remarks>
public sealed class CardDirectory : IDefinitionStore
{
    private readonly Dictionary<string, CardFile> _byOracleId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CardFile> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CardFile> _files = [];
    private readonly List<string> _unreadable = [];

    private CardDirectory(string root) => Root = root;

    /// <summary>The directory the files were read from.</summary>
    public string Root { get; }

    /// <summary>Every file that parsed, in the order the directory listed them.</summary>
    public IReadOnlyList<CardFile> Files => _files;

    /// <summary>The file names that did not parse.</summary>
    public IReadOnlyList<string> Unreadable => _unreadable;

    /// <summary>Reads every <c>*.json</c> at the top level of the directory. A missing directory is an empty one.</summary>
    public static CardDirectory Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var directory = new CardDirectory(root);
        if (!Directory.Exists(root))
            return directory;

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            CardFile file;
            try
            {
                file = CardFileSerializer.Read(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
            {
                directory._unreadable.Add(Path.GetFileName(path));
                continue;
            }

            directory._files.Add(file);
            directory._byOracleId[file.OracleId] = file;
            directory._byName[file.Name] = file;
            foreach (var face in file.Definition.Faces)
                directory._byName.TryAdd(face.Name, file);
        }

        return directory;
    }

    /// <summary>The file for this oracle id, or null when the directory does not cover it.</summary>
    public CardFile? Get(string oracleId) =>
        oracleId is not null && _byOracleId.TryGetValue(oracleId, out var file) ? file : null;

    public CardDefinition? ByName(string name) =>
        name is not null && _byName.TryGetValue(name, out var file) ? file.Definition : null;

    public CardDefinition? ByOracleId(string oracleId) => Get(oracleId)?.Definition;
}
