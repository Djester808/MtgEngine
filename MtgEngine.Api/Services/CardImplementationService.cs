using MtgEngine.Api.Dtos;
using MtgEngine.Cards;

namespace MtgEngine.Api.Services;

/// <summary>Where the generated card files live. The pipeline writes them; this only reads, and writes one when a dispute is settled.</summary>
public sealed record CardImplementationOptions(string Directory);

/// <summary>
/// What the dashboard and the dispute queue ask the server: how far along one card is, how far
/// along the whole corpus is, and — when a card's two readings disagreed — which one wins.
/// </summary>
/// <remarks>
/// The status is the pool's, so this and the deck gate can never disagree about what a card is:
/// the fourth rule of the card plan is one status field, read in one place. Everything else here
/// is evidence the gate does not need and a person does — the scenarios, the provenance, the
/// primitive that is blocking the rest — and it is read from the files rather than held in
/// memory, because a verification run rewrites them under a server that is already up.
/// </remarks>
public sealed class CardImplementationService(ICardFilePool pool, CardImplementationOptions options, ILogger<CardImplementationService>? logger = null)
{
    private readonly string _directory = options?.Directory ?? throw new ArgumentNullException(nameof(options));

    /// <summary>How far along one card is, with the evidence.</summary>
    public CardImplementationDto Implementation(string oracleId)
    {
        var file = Load(oracleId) ?? throw new ResourceNotFoundException("No card file for that oracle id.");
        return Dto(file);
    }

    /// <summary>
    /// How far along each of these cards is, in the order they were asked about. An oracle id no
    /// file covers is <see cref="ImplementationStatus.Untranslated"/> rather than an error: a deck
    /// view asks about every card it holds and a card nothing has translated yet is the ordinary
    /// case, not a failed request.
    /// </summary>
    /// <remarks>
    /// The directory is scanned at most once, and only when a file was not where its oracle id
    /// says it should be, so a deck of a hundred cards is a hundred file reads rather than a
    /// hundred scans of the corpus.
    /// </remarks>
    public IReadOnlyList<CardImplementationDto> Implementations(IReadOnlyList<string> oracleIds)
    {
        ArgumentNullException.ThrowIfNull(oracleIds);
        var found = new Dictionary<string, CardFile>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oracleId in oracleIds)
        {
            if (string.IsNullOrWhiteSpace(oracleId) || found.ContainsKey(oracleId) || missing.Contains(oracleId))
                continue;

            var path = Path(oracleId);
            var file = System.IO.File.Exists(path) ? Read(path) : null;
            if (file is null)
                missing.Add(oracleId);
            else
                found[oracleId] = file;
        }

        if (missing.Count > 0)
        {
            foreach (var file in All().Where(f => missing.Contains(f.OracleId)))
                found[file.OracleId] = file;
        }

        return [.. oracleIds.Select(id => id is not null && found.TryGetValue(id, out var file) ? Dto(file) : Unknown(id))];
    }

    /// <summary>The status the deck gate reads, and the evidence a person reads beside it.</summary>
    private CardImplementationDto Dto(CardFile file)
    {
        var status = pool.StatusOf(file.OracleId);
        return new CardImplementationDto(
            file.OracleId,
            file.Name,
            status.Status == ImplementationStatus.Untranslated ? file.Status : status.Status,
            Excluded(file),
            file.Source.GetType().Name,
            [.. file.ScenarioList.Select(s => new ScenarioSummaryDto(s.Title, s.Source.GetType().Name, Passed(file, s)))],
            Verification(file.Verification));
    }

    /// <summary>A card no file covers: named by its id, because nothing here knows better.</summary>
    private static CardImplementationDto Unknown(string? oracleId) =>
        new(oracleId ?? string.Empty, oracleId ?? string.Empty, ImplementationStatus.Untranslated, null, nameof(Unknown), [], null);

    /// <summary>Counts by status, what is blocking the rest, and the last run the ledger saw.</summary>
    public CardCoverageDto Coverage(ImplementationStatus? status = null)
    {
        var files = All().ToList();
        var byStatus = files
            .GroupBy(f => f.Status)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);

        var backlog = files
            .SelectMany(f => Reasons(f).Distinct(StringComparer.Ordinal))
            .GroupBy(r => r, StringComparer.Ordinal)
            .Select(g => new PrimitiveBacklogDto(g.Key, g.Count()))
            .OrderByDescending(b => b.CardsBlocked)
            .ThenBy(b => b.Reason, StringComparer.Ordinal)
            .ToList();

        var last = files
            .Select(f => f.Verification)
            .Where(v => v is not null)
            .OrderByDescending(v => v!.At)
            .FirstOrDefault();

        var disputed = status == ImplementationStatus.Disputed
            ? files.Where(f => f.Status == ImplementationStatus.Disputed).Select(Dispute).ToList()
            : null;

        return new CardCoverageDto(files.Count, byStatus, backlog, Verification(last), disputed);
    }

    /// <summary>
    /// A person read both readings and chose one. The chosen definition becomes the card's, and
    /// the status goes back to <see cref="ImplementationStatus.Translated"/> so the runner
    /// verifies it: choosing settles a disagreement, it does not prove a card plays.
    /// </summary>
    public CardImplementationDto Choose(string oracleId, string model)
    {
        var file = Load(oracleId) ?? throw new ResourceNotFoundException("No card file for that oracle id.");
        if (file.Status != ImplementationStatus.Disputed)
            throw new InvalidResourceStateException("That card is not disputed, so there is nothing to choose between.");

        var chosen = file.Candidates?.FirstOrDefault(c => string.Equals(c.Model, model, StringComparison.OrdinalIgnoreCase))
            ?? throw new ResourceNotFoundException("No reading from that model.");

        var settled = file with
        {
            Definition = chosen.Definition,
            Status = ImplementationStatus.Translated,
            Candidates = null,
            Verification = null,
            Source = new TranslatedProvenance([chosen.Model], PromptVersion(file), Agreed: false, chosen.BatchId),
        };

        System.IO.File.WriteAllText(Path(settled.OracleId), CardFileSerializer.Write(settled));
        logger?.LogInformation("Card {OracleId} settled on the reading from {Model}.", settled.OracleId, chosen.Model);

        return new CardImplementationDto(
            settled.OracleId,
            settled.Name,
            settled.Status,
            null,
            settled.Source.GetType().Name,
            [.. settled.ScenarioList.Select(s => new ScenarioSummaryDto(s.Title, s.Source.GetType().Name, null))],
            null);
    }

    private static string PromptVersion(CardFile file) =>
        file.Source is TranslatedProvenance translated ? translated.PromptVersion : "chosen";

    private static string? Excluded(CardFile file) =>
        file.Source is ExcludedProvenance excluded ? excluded.Reason : null;

    private static VerificationDto? Verification(Verification? verification) =>
        verification is null ? null : new VerificationDto(verification.EngineHash, verification.RunId, verification.Passed, verification.Total, verification.At);

    /// <summary>
    /// A scenario's own pass is not stored per scenario; the run stores how many of them passed.
    /// So a card whose run passed every scenario says so, one that failed some says nothing about
    /// which, and a card with no run says nothing at all. Guessing would be worse than null.
    /// </summary>
    private static bool? Passed(CardFile file, Scenario scenario)
    {
        _ = scenario;
        if (file.Verification is not { } run || run.Total == 0)
            return null;
        return run.Passed == run.Total ? true : null;
    }

    /// <summary>The primitives the translation stage could not express, CARD_ENGINE_PLAN.md § "Triage".</summary>
    private static IEnumerable<string> Reasons(CardFile file) =>
        file.Definition.Faces
            .SelectMany(f => f.AbilityList)
            .SelectMany(Effects)
            .OfType<Unsupported>()
            .Select(u => u.Reason);

    private static IEnumerable<Effect> Effects(Ability ability) => ability switch
    {
        SpellAbility spell => Walk(spell.Do),
        TriggeredAbility triggered => Walk(triggered.Do),
        ActivatedAbility activated => Walk(activated.Do),
        _ => [],
    };

    private static IEnumerable<Effect> Walk(Effect effect) =>
        effect is Sequence sequence ? sequence.Steps.SelectMany(Walk) : [effect];

    private DisputedCardDto Dispute(CardFile file)
    {
        var readings = (file.Candidates ?? [])
            .Select(c => new ReadingDto(c.Model, CardFileSerializer.WriteDefinition(c.Definition)))
            .ToList();
        return new DisputedCardDto(file.OracleId, file.Name, readings, FirstDifference(readings));
    }

    /// <summary>Where the two readings first part company, so the queue can show the disagreement and not two whole documents.</summary>
    private static string? FirstDifference(IReadOnlyList<ReadingDto> readings)
    {
        if (readings.Count < 2)
            return null;
        var (a, b) = (readings[0].DefinitionJson, readings[1].DefinitionJson);
        var at = 0;
        while (at < a.Length && at < b.Length && a[at] == b[at])
            at++;
        if (at == a.Length && at == b.Length)
            return null;
        return $"{Excerpt(a, at)} | {Excerpt(b, at)}";
    }

    private static string Excerpt(string text, int at)
    {
        var from = Math.Max(0, at - 20);
        var to = Math.Min(text.Length, at + 40);
        return text[from..to].ReplaceLineEndings(" ").Trim();
    }

    private string Path(string oracleId) => System.IO.Path.Combine(_directory, oracleId + ".json");

    private CardFile? Load(string oracleId)
    {
        if (string.IsNullOrWhiteSpace(oracleId))
            return null;
        var path = Path(oracleId);
        return System.IO.File.Exists(path) ? Read(path) : All().FirstOrDefault(f => string.Equals(f.OracleId, oracleId, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<CardFile> All()
    {
        if (!System.IO.Directory.Exists(_directory))
            yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var file = Read(path);
            if (file is not null)
                yield return file;
        }
    }

    /// <summary>One unreadable file out of thousands must not take an endpoint down; the pipeline's gate is where it is caught.</summary>
    private CardFile? Read(string path)
    {
        try
        {
            return CardFileSerializer.Read(System.IO.File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Skipping unreadable card file {File}.", System.IO.Path.GetFileName(path));
            return null;
        }
    }
}
