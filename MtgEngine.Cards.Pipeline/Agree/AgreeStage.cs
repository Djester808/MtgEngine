namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// One model's reading of one card: what it said, whether the serializer accepted it, and what
/// the linter made of it. <paramref name="Lint"/> may be left null and the stage will lint the
/// definition itself; a caller that already linted passes its report so the work is done once.
/// </summary>
public sealed record ModelReading(string Model, CardDefinition? Definition, string? ParseError = null, LintReport? Lint = null);

/// <summary>The card two or more models read, and everything the written file needs that the definition does not carry.</summary>
public sealed record AgreeRequest(
    string OracleId,
    string Name,
    string OracleHash,
    string PromptVersion,
    IReadOnlyList<ModelReading> Readings,
    IReadOnlyList<string>? ScryfallKeywords = null);

/// <summary>
/// What the agreement stage decided: the status, the file to write (null when there is nothing
/// to write), each model's lint report, which models linted clean, and the first place the
/// readings differ.
/// </summary>
public sealed record AgreeResult(
    string OracleId,
    string Name,
    ImplementationStatus Status,
    CardFile? File,
    IReadOnlyDictionary<string, LintReport?> Lint,
    IReadOnlyList<string> CleanModels,
    string? Difference)
{
    /// <summary>True only for the agreed branch: every reading parsed, linted clean and said the same thing.</summary>
    public bool Agreed => Status == ImplementationStatus.Translated;
}

/// <summary>
/// The third stage: two readings in, one status out. Agreement is the only evidence the
/// pipeline has before the runner exists, so it is spent carefully — a reading that fails the
/// serializer or carries a lint error is not evidence of anything, and two readings that
/// differ leave the card <see cref="ImplementationStatus.Disputed"/> with both readings kept
/// rather than silently picking one and calling it translated.
///
/// CARD_ENGINE_PLAN.md § "Wave 1", P3: structural diff, keyword cross-check (the linter's
/// L002, which this stage runs by linting each reading against the Scryfall keyword list),
/// status assignment, file writer.
/// </summary>
public static class AgreeStage
{
    public static AgreeResult Agree(AgreeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Readings.Count < 2)
            throw new ArgumentException("Agreement needs the readings of two or more models; one reading is evidence of nothing.", nameof(request));

        var lint = new Dictionary<string, LintReport?>(StringComparer.Ordinal);
        foreach (var reading in request.Readings)
            lint[reading.Model] = LintOf(request, reading);

        var parsed = request.Readings.Where(r => r.Definition is not null).ToList();
        var clean = parsed.Where(r => lint[r.Model] is { HasErrors: false }).ToList();
        var cleanModels = clean.Select(r => r.Model).ToList();

        if (parsed.Count == 0)
        {
            // Nothing to write: the card file would carry no definition. The report is the output.
            return new AgreeResult(request.OracleId, request.Name, ImplementationStatus.Untranslated, null, lint, [], null);
        }

        var models = request.Readings.Select(r => r.Model).ToList();
        var agreed = clean.Count == request.Readings.Count
            && clean.Select(r => JsonDiff.CanonicalText(r.Definition!)).Distinct(StringComparer.Ordinal).Count() == 1;

        if (agreed)
        {
            var file = FileFor(request, clean[0].Definition!, models, ImplementationStatus.Translated, agreed: true);
            return new AgreeResult(request.OracleId, request.Name, ImplementationStatus.Translated, file, lint, cleanModels, null);
        }

        // The first clean reading is the one written; every reading that parsed is kept as a
        // candidate so the dispute queue can show them side by side and a person can choose.
        var chosen = clean.Count > 0 ? clean[0] : parsed[0];
        var disputed = FileFor(request, chosen.Definition!, [chosen.Model], ImplementationStatus.Disputed, agreed: false) with
        {
            Candidates = [.. parsed.Select(r => new TranslationCandidate(r.Model, r.Definition!))],
        };

        return new AgreeResult(request.OracleId, request.Name, ImplementationStatus.Disputed, disputed, lint, cleanModels, FirstDifference(parsed));
    }

    /// <summary>
    /// A reading's own lint report. The file it is linted in carries
    /// <see cref="ImplementationStatus.Translated"/> deliberately: linting a lone reading as
    /// Disputed would raise L013 ("a Disputed file must keep both readings") on every reading
    /// and no card could ever agree.
    /// </summary>
    private static LintReport? LintOf(AgreeRequest request, ModelReading reading)
    {
        if (reading.Definition is null)
            return null;
        return reading.Lint ?? Linter.Check(FileFor(request, reading.Definition, [reading.Model], ImplementationStatus.Translated, agreed: false));
    }

    private static string? FirstDifference(IReadOnlyList<ModelReading> parsed)
    {
        for (var i = 1; i < parsed.Count; i++)
        {
            var difference = JsonDiff.Between(parsed[0].Definition!, parsed[i].Definition!);
            if (difference is not null)
                return difference;
        }

        // The readings said the same thing; the dispute is a lint or parse failure, not a difference.
        return null;
    }

    private static CardFile FileFor(
        AgreeRequest request,
        CardDefinition definition,
        IReadOnlyList<string> models,
        ImplementationStatus status,
        bool agreed) => new(
            request.OracleId,
            request.Name,
            request.OracleHash,
            IrVersion.Current,
            definition,
            status,
            new TranslatedProvenance(models, request.PromptVersion, agreed),
            request.ScryfallKeywords);
}
