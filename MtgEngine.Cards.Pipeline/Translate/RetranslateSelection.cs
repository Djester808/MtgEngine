using MtgEngine.Cards.Pipeline.Verify;

namespace MtgEngine.Cards.Pipeline.Translate;

/// <summary>What a re-translation round would submit: which kinds of card, by name.</summary>
[Flags]
public enum RetranslateKinds
{
    None = 0,

    /// <summary>The two translators read the card differently and the canonical passes could not close it.</summary>
    Disputed = 1,

    /// <summary>The definition carries an <see cref="MtgEngine.Cards.Unsupported"/> marker: written before the vocabulary could say it.</summary>
    Unsupported = 2,

    /// <summary>A verification run played it and an expectation did not hold.</summary>
    Failed = 4,

    All = Disputed | Unsupported | Failed,
}

/// <summary>One card a re-translation round would submit, and the words for why.</summary>
public sealed record RetranslateItem(string OracleId, string Name, string Why);

/// <summary>The cards and the corpus faces behind them, plus the ids the corpus in this checkout does not cover.</summary>
public sealed record RetranslateBatch(IReadOnlyList<CorpusCard> Cards, IReadOnlyList<string> Missing);

/// <summary>
/// Which cards a re-translation round submits, read from <c>cards/</c> and from nothing else.
/// </summary>
/// <remarks>
/// Re-translation is the credit-bound half of the loop: the pilot's 200 faces cost about $19 a
/// run, so a round that resubmits a card already verified spends the user's money to be told
/// what the file already says. The selection is therefore a refusal as much as a filter — a
/// Verified card carrying no <see cref="MtgEngine.Cards.Unsupported"/> marker is never in it,
/// whatever kinds were asked for — and it is computed here, off the files, so a test can prove
/// what a run would submit without a run (CARD_ENGINE_PLAN.md § "Budget").
/// </remarks>
public static class RetranslateSelection
{
    /// <summary>What a re-translation round asks for when the caller names nothing: the disputed cards and the unsupported ones.</summary>
    public const string DefaultSpec = "disputed,unsupported";

    /// <summary>Reads a comma-separated spec ("disputed,unsupported", "all"). An unknown word is an error, never a silent empty selection.</summary>
    public static bool TryParse(string? spec, out RetranslateKinds kinds, out string? error)
    {
        kinds = RetranslateKinds.None;
        error = null;
        foreach (var word in (spec ?? DefaultSpec).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "disputed":
                    kinds |= RetranslateKinds.Disputed;
                    break;
                case "unsupported":
                    kinds |= RetranslateKinds.Unsupported;
                    break;
                case "failed":
                    kinds |= RetranslateKinds.Failed;
                    break;
                case "all":
                    kinds |= RetranslateKinds.All;
                    break;
                default:
                    error = "not a re-translation kind: " + word + " (disputed, unsupported, failed, all)";
                    return false;
            }
        }

        return kinds != RetranslateKinds.None;
    }

    /// <summary>Every card in the directory this round would submit, in the directory's own order, once each.</summary>
    public static IReadOnlyList<RetranslateItem> Select(CardDirectory directory, RetranslateKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var items = new List<RetranslateItem>();
        foreach (var file in directory.Files)
        {
            if (file.Status == ImplementationStatus.Excluded)
                continue;

            var why = Why(file, kinds);
            if (why.Count == 0)
                continue;

            items.Add(new RetranslateItem(file.OracleId, file.Name, string.Join("; ", why)));
        }

        return items;
    }

    /// <summary>
    /// The corpus faces for a selection, in the selection's order, and the ids the corpus does not
    /// cover — a card file whose face is not in this checkout's corpus is reported, not skipped
    /// silently, because a short batch is indistinguishable from a cheap one.
    /// </summary>
    public static RetranslateBatch Cards(IEnumerable<CorpusCard> corpus, IReadOnlyList<RetranslateItem> selection)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(selection);
        var byId = corpus
            .GroupBy(c => c.OracleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cards = new List<CorpusCard>();
        var missing = new List<string>();
        foreach (var item in selection)
        {
            if (byId.TryGetValue(item.OracleId, out var card))
                cards.Add(card);
            else
                missing.Add(item.OracleId);
        }

        return new RetranslateBatch(cards, missing);
    }

    /// <summary>What a run would submit, printed, so the user reads the bill before it is spent.</summary>
    public static void Print(IReadOnlyList<RetranslateItem> selection, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var item in selection)
            log.WriteLine(FormattableString.Invariant($"  {item.OracleId}  {item.Name}: {item.Why}"));
        log.WriteLine(FormattableString.Invariant($"retranslate: {selection.Count} faces would be submitted."));
    }

    /// <summary>Every reason this file is in the round, or none — which is the answer for a verified card that says everything it needs to.</summary>
    private static List<string> Why(CardFile file, RetranslateKinds kinds)
    {
        var why = new List<string>();
        if (kinds.HasFlag(RetranslateKinds.Disputed) && file.Status == ImplementationStatus.Disputed)
            why.Add("disputed");

        if (kinds.HasFlag(RetranslateKinds.Unsupported) && UnsupportedReason(file) is { } reason)
            why.Add("unsupported: " + reason);

        // A card the engine played and refuted. A Verified status with a failing verification
        // cannot happen — VerifyStage demotes it — so this reads the counts and not the status.
        if (kinds.HasFlag(RetranslateKinds.Failed)
            && file.Status != ImplementationStatus.Verified
            && file.Verification is { } verification
            && verification.Passed < verification.Total)
        {
            why.Add(FormattableString.Invariant($"failed {verification.Passed}/{verification.Total}"));
        }

        return why;
    }

    /// <summary>The first unsupported marker's reason, or null. The same read <c>verify</c> refuses a card with.</summary>
    public static string? UnsupportedReason(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Linter.Walk(file.Definition).OfType<Unsupported>().FirstOrDefault()?.Reason;
    }
}
