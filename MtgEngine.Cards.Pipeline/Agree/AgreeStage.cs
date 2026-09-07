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
/// A reading the majority left behind: with three readings, two that say the same thing agree
/// and the third is reported here — with the first place it differs from what was agreed — and
/// is not kept as a candidate, because the card is not in dispute.
/// </summary>
public sealed record OutvotedReading(string Model, string? Difference);

/// <summary>
/// What the agreement stage decided: the status, the file to write (null when there is nothing
/// to write), each model's lint report, which models linted clean, the first place the
/// readings differ, the models whose identical readings carried the decision, and the readings
/// they outvoted.
/// </summary>
public sealed record AgreeResult(
    string OracleId,
    string Name,
    ImplementationStatus Status,
    CardFile? File,
    IReadOnlyDictionary<string, LintReport?> Lint,
    IReadOnlyList<string> CleanModels,
    string? Difference,
    IReadOnlyList<string>? AgreedModels = null,
    IReadOnlyList<OutvotedReading>? Outvoted = null)
{
    /// <summary>True only for the agreed branch: a majority of the readings parsed, linted clean and said the same thing.</summary>
    public bool Agreed => Status == ImplementationStatus.Translated;

    /// <summary>The models whose readings were identical after canonicalisation: the provenance of the written file. Empty unless agreed.</summary>
    public IReadOnlyList<string> AgreedBy => AgreedModels ?? [];

    /// <summary>The readings the majority outvoted. Empty for a two-reading card and for a dispute.</summary>
    public IReadOnlyList<OutvotedReading> OutvotedReadings => Outvoted ?? [];
}

/// <summary>
/// The third stage: two or three readings in, one status out. Agreement is the only evidence the
/// pipeline has before the runner exists, so it is spent carefully — a reading that fails the
/// serializer or carries a lint error is not evidence of anything, and readings of which no
/// majority say the same thing leave the card <see cref="ImplementationStatus.Disputed"/> with
/// every parsed reading kept rather than silently picking one and calling it translated.
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

        // Every reading is judged, linted and written in its canonical spelling, so two models
        // that understood the card the same way agree however verbosely either wrote it, and the
        // card file that comes out is canonical too.
        var readings = request.Readings
            .Select(r => r.Definition is null ? r : r with { Definition = Canonical.Of(r.Definition) })
            .ToList();

        var lint = new Dictionary<string, LintReport?>(StringComparer.Ordinal);
        foreach (var reading in readings)
            lint[reading.Model] = LintOf(request, reading);

        var parsed = readings.Where(r => r.Definition is not null).ToList();
        var clean = parsed.Where(r => lint[r.Model] is { HasErrors: false }).ToList();
        var cleanModels = clean.Select(r => r.Model).ToList();

        if (parsed.Count == 0)
        {
            // Nothing to write: the card file would carry no definition. The report is the output.
            return new AgreeResult(request.OracleId, request.Name, ImplementationStatus.Untranslated, null, lint, [], null);
        }

        // A card both models could only mark Unsupported is not a dispute: they read the card the
        // same way and hit the same hole in the vocabulary. Calling that Disputed sent the dispute
        // queue two identical readings to choose between and hid the real answer, which is a
        // missing primitive. L001 still carries the marker into the file, the linter still errors
        // on it, and the deck gate still refuses it — only the status is now honest.
        var evidence = parsed.Where(r => IsEvidence(lint[r.Model])).ToList();

        // More than half of the readings saying the same thing is agreement: both of two, any
        // two of three (wave 15, P17). The readings that said something else are outvoted —
        // reported with where they differ, and not kept as candidates, because the card is not
        // in dispute. The majority's order is the readings' order, so provenance is stable.
        var required = (readings.Count / 2) + 1;
        var majority = evidence
            .GroupBy(r => JsonDiff.CanonicalText(r.Definition!), StringComparer.Ordinal)
            .Select(g => g.ToList())
            .FirstOrDefault(g => g.Count >= required);

        if (majority is not null)
        {
            var agreedModels = majority.Select(r => r.Model).ToList();
            var outvoted = readings
                .Where(r => !agreedModels.Contains(r.Model, StringComparer.Ordinal))
                .Select(r => new OutvotedReading(r.Model, r.Definition is null ? r.ParseError : JsonDiff.Between(majority[0].Definition!, r.Definition)))
                .ToList();
            var file = FileFor(request, majority[0].Definition!, agreedModels, ImplementationStatus.Translated, agreed: true);
            return new AgreeResult(request.OracleId, request.Name, ImplementationStatus.Translated, file, lint, cleanModels, null, agreedModels, outvoted);
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

    /// <summary>
    /// Whether a reading is evidence of what the card does. Any lint error but L001 means the
    /// reading is broken and says nothing; L001 alone means the reading is right about the card
    /// and the vocabulary is short a primitive, which two readings can agree on.
    /// </summary>
    private static bool IsEvidence(LintReport? report) =>
        report is not null
        && report.Findings.All(f => f.Severity != LintSeverity.Error || string.Equals(f.Code, "L001", StringComparison.Ordinal));

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
