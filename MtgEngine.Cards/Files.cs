using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MtgEngine.Cards;

/// <summary>The vocabulary version a card file was written against. Bump with a migration when a record's shape changes.</summary>
public static class IrVersion
{
    public const int Current = 1;
}

/// <summary>Where a card is on the road from oracle text to a table. The deck gate reads this and nothing else.</summary>
public enum ImplementationStatus
{
    Untranslated,
    Translated,
    Disputed,
    Verified,
    Excluded,
}

/// <summary>How the definition in a card file came to be.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TemplateProvenance), nameof(TemplateProvenance))]
[JsonDerivedType(typeof(TranslatedProvenance), nameof(TranslatedProvenance))]
[JsonDerivedType(typeof(HumanProvenance), nameof(HumanProvenance))]
[JsonDerivedType(typeof(ExcludedProvenance), nameof(ExcludedProvenance))]
public abstract record Provenance;

/// <summary>Written straight from the Scryfall JSON: keyword-only, vanilla, or a mana pattern.</summary>
public sealed record TemplateProvenance(string Template) : Provenance;

/// <summary>Written by the translation stage. <paramref name="Agreed"/> is whether both models produced the same definition.</summary>
public sealed record TranslatedProvenance(IReadOnlyList<string> Models, string PromptVersion, bool Agreed, string? BatchId = null) : Provenance;

/// <summary>Corrected by a person through the pipeline's edit command.</summary>
public sealed record HumanProvenance(string By, string? Note = null) : Provenance;

/// <summary>On the published exclusion list, with the reason.</summary>
public sealed record ExcludedProvenance(string Reason) : Provenance;

/// <summary>The runner's record of the last verification: which engine, which run, how many scenarios passed.</summary>
public sealed record Verification(string EngineHash, string RunId, int Passed, int Total, DateTimeOffset At);

/// <summary>One model's reading, kept when the readings disagreed so the dispute view can show both.</summary>
public sealed record TranslationCandidate(string Model, CardDefinition Definition, string? BatchId = null);

/// <summary>
/// One file under <c>cards/</c>: the definition, its status, where it came from, and the
/// scenarios that verify it. Written only by the pipeline.
/// </summary>
public sealed record CardFile(
    string OracleId,
    string Name,
    string OracleHash,
    int IrVersion,
    CardDefinition Definition,
    ImplementationStatus Status,
    Provenance Source,
    IReadOnlyList<string>? ScryfallKeywords = null,
    IReadOnlyList<Scenario>? Scenarios = null,
    IReadOnlyList<TranslationCandidate>? Candidates = null,
    Verification? Verification = null)
{
    public IReadOnlyList<string> KeywordList => ScryfallKeywords ?? [];

    public IReadOnlyList<Scenario> ScenarioList => Scenarios ?? [];
}

/// <summary>The hash that ties a file to the oracle text it was translated from. Errata changes it; the pipeline re-translates.</summary>
public static class OracleHashing
{
    public static string Of(IEnumerable<string?> faceTexts)
    {
        var joined = string.Join("\n---\n", faceTexts.Select(t => (t ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim()));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return "sha256:" + System.Convert.ToHexStringLower(bytes);
    }
}
