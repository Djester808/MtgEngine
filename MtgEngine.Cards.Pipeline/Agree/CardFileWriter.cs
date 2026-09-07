using System.Text.Json;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>What a write did. A refusal is not an error: the file on disk is better than the one offered.</summary>
public enum CardWrite
{
    /// <summary>The card file was replaced.</summary>
    Written,

    /// <summary>The file on disk is Verified for the same oracle text, so the lower status was refused.</summary>
    KeptVerified,
}

/// <summary>
/// The only writer of <c>cards/</c>. Two rules it exists to hold:
///
/// - A write is atomic. The file is written as <c>&lt;oracleId&gt;.json.tmp</c> beside its
///   target and moved over it, so a killed pipeline never leaves a half-written card file for
///   the API to read — and never leaves the temp file behind either.
/// - A <see cref="ImplementationStatus.Verified"/> file is not lowered by a re-translation of
///   the same oracle text. Verification is a verified play (CARD_ENGINE_PLAN.md rule 3) and a
///   later Translated reading is not new information about it. When the incoming
///   <c>OracleHash</c> differs the card was errata'd or re-printed with new text, the old
///   verification no longer describes it, and the write goes through. So does a write over a
///   Verified file whose definition carries an Unsupported marker: that file never verified in
///   truth, and the re-translation is the first reading that could.
///
/// The linter's report lands beside the file under <c>cards/.lint/&lt;oracleId&gt;.json</c>, so
/// the dashboard can count files by cause without re-linting the corpus. Everything is under an
/// explicit directory; nothing here touches the network.
/// </summary>
public sealed class CardFileWriter(string directory)
{
    /// <summary>The subdirectory of <see cref="Root"/> the lint reports go in.</summary>
    public const string LintDirectoryName = ".lint";

    private const string TempSuffix = ".tmp";

    /// <summary>The directory the card files live in — <c>cards/</c> in the repository, a scratch directory in a test.</summary>
    public string Root { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>The path of one card's file, whether or not it exists.</summary>
    public string FileFor(string oracleId) => Path.Combine(Root, oracleId + ".json");

    /// <summary>The path of one card's lint report, whether or not it exists.</summary>
    public string LintFileFor(string oracleId) => Path.Combine(Root, LintDirectoryName, oracleId + ".json");

    /// <summary>The file on disk, or null when this card has never been written.</summary>
    public CardFile? Load(string oracleId)
    {
        var path = FileFor(oracleId);
        return File.Exists(path) ? CardFileSerializer.Read(File.ReadAllText(path)) : null;
    }

    /// <summary>Every oracle id that has a card file on disk: what a corpus slice must not ask for again.</summary>
    public IReadOnlyList<string> OracleIds() =>
        Directory.Exists(Root)
            ? [.. Directory.EnumerateFiles(Root, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    /// <summary>The lint report on disk, or null when this card has never been written.</summary>
    public LintReport? LoadLint(string oracleId)
    {
        var path = LintFileFor(oracleId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<LintReport>(File.ReadAllText(path), CardFileSerializer.Options)
            : null;
    }

    /// <summary>
    /// Writes one card file and its lint report, unless doing so would lower a Verified file
    /// whose oracle text has not changed. <paramref name="lint"/> defaults to the linter's
    /// verdict on the file being written.
    /// </summary>
    public CardWrite Write(CardFile file, LintReport? lint = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        var existing = Load(file.OracleId);
        if (existing is not null && Lowers(existing, file))
            return CardWrite.KeptVerified;

        Directory.CreateDirectory(Root);
        WriteAtomic(FileFor(file.OracleId), CardFileSerializer.Write(file) + "\n");

        Directory.CreateDirectory(Path.Combine(Root, LintDirectoryName));
        var report = lint ?? Linter.Check(file);
        WriteAtomic(LintFileFor(file.OracleId), JsonSerializer.Serialize(report, CardFileSerializer.Options) + "\n");

        return CardWrite.Written;
    }

    /// <summary>Writes what the agreement stage decided, and reports the untranslated cards as a skip.</summary>
    public CardWrite? Write(AgreeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.File is null ? null : Write(result.File);
    }

    private static bool Lowers(CardFile existing, CardFile incoming) =>
        existing.Status == ImplementationStatus.Verified
        && !CarriesUnsupported(existing.Definition)
        && incoming.Status is ImplementationStatus.Untranslated or ImplementationStatus.Translated or ImplementationStatus.Disputed
        && string.Equals(existing.OracleHash, incoming.OracleHash, StringComparison.Ordinal);

    /// <summary>
    /// A definition carrying an <see cref="Unsupported"/> or <see cref="UnsupportedContinuous"/>
    /// marker was never played whole: the marker is reported rather than played, so a Verified on
    /// such a file is not a verified play of the card. Keeping it over a re-translation kept Myriad
    /// Landscape and Nyx Lotus stale after the vocabulary had closed their gaps.
    /// </summary>
    private static bool CarriesUnsupported(CardDefinition definition) =>
        Linter.Walk(definition).Any(node => node is Unsupported or UnsupportedContinuous);

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + TempSuffix;
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
