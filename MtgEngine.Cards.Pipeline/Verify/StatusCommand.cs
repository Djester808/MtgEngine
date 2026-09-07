using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Verify;

/// <summary>One unsupported reason and how many cards are waiting on it.</summary>
public sealed record StatusReason(string Reason, int Count);

/// <summary>
/// Where a directory of card files stands, as the files themselves record it: the five counts a
/// verification run reports, the disputed cards a re-translation round would take, and what the
/// unsupported ones are waiting for.
/// </summary>
public sealed record StatusSummary(
    int Files,
    int Verified,
    int Failed,
    int NoScenarios,
    int Unsupported,
    int NotRun,
    int Disputed,
    int Untranslated,
    int Excluded,
    IReadOnlyList<StatusReason> UnsupportedReasons,
    IReadOnlyList<string> Unreadable,
    string? EngineHash)
{
    /// <summary>The line a person reads: where the pilot stands, in one sentence.</summary>
    public string Line
    {
        get
        {
            var run = FormattableString.Invariant(
                $"status: {Verified} verified, {Failed} failed, {NoScenarios} with no scenarios, {Unsupported} unsupported, {NotRun} not run;");
            var rest = FormattableString.Invariant(
                $" {Disputed} disputed, {Untranslated} untranslated, {Excluded} excluded of {Files} files");
            return run + rest + (EngineHash is null ? string.Empty : "; engine " + EngineHash);
        }
    }
}

/// <summary>
/// <c>pipeline status [cardsDir]</c>: the counts, read and not played.
/// </summary>
/// <remarks>
/// <see cref="VerifyCommand"/> answers the same question by playing every card, which takes a
/// real game per scenario and rewrites <c>cards/</c>. That is the right way to learn something
/// new and the wrong way to look something up — the last run already wrote its verdict into each
/// file, so asking where the programme stands should cost a directory read. Nothing here plays,
/// writes or reaches the network; the numbers are the files' own, and a card whose run crashed
/// left no record in its file, so it is counted as not run and only the run's log names it.
/// </remarks>
public static class StatusCommand
{
    /// <summary>The directory held no card files.</summary>
    public const int NothingToDo = 64;

    /// <summary>Read a directory and print the summary.</summary>
    public static int Run(string cardsDir, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var summary = Of(CardDirectory.Load(cardsDir));
        Print(summary, log);
        return summary.Files == 0 && summary.Unreadable.Count == 0 ? NothingToDo : 0;
    }

    /// <summary>The counts for a directory that is already read, so a test can supply its own.</summary>
    public static StatusSummary Of(CardDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        int verified = 0, failed = 0, noScenarios = 0, unsupported = 0, notRun = 0;
        int disputed = 0, untranslated = 0, excluded = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in directory.Files)
        {
            if (file.Verification is { } verification)
                hashes[verification.EngineHash] = hashes.GetValueOrDefault(verification.EngineHash) + 1;

            switch (file.Status)
            {
                case ImplementationStatus.Disputed:
                    disputed++;
                    break;
                case ImplementationStatus.Untranslated:
                    untranslated++;
                    break;
                case ImplementationStatus.Excluded:
                    excluded++;
                    break;
                default:
                    break;
            }

            // An unsupported marker is counted wherever the card sits, because verify refuses the
            // card before it plays it whatever its status says (VerifyCommand.NotPlayable).
            if (RetranslateSelection.UnsupportedReason(file) is { } reason)
            {
                unsupported++;
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }

            if (file.Status is not (ImplementationStatus.Translated or ImplementationStatus.Verified))
                continue;

            if (file.Status == ImplementationStatus.Verified)
                verified++;
            else if (file.Verification is { } run && run.Passed < run.Total)
                failed++;
            else if (file.ScenarioList.Count == 0)
                noScenarios++;
            else
                notRun++;
        }

        return new StatusSummary(
            directory.Files.Count,
            verified,
            failed,
            noScenarios,
            unsupported,
            notRun,
            disputed,
            untranslated,
            excluded,
            [.. reasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => new StatusReason(r.Key, r.Value))],
            directory.Unreadable,
            hashes.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key).FirstOrDefault());
    }

    /// <summary>The summary line, then what the unsupported cards are waiting for.</summary>
    public static void Print(StatusSummary summary, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var bad in summary.Unreadable)
            log.WriteLine("status: could not read " + bad);

        log.WriteLine(summary.Line);
        foreach (var reason in summary.UnsupportedReasons)
            log.WriteLine(FormattableString.Invariant($"  unsupported x{reason.Count}: {reason.Reason}"));
    }
}
