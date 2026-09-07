using System.Text.Json;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Verify;

/// <summary>
/// <c>pipeline answers-check &lt;batchDir&gt;</c>: the same parse and lint the collect step applies,
/// run over one local batch's answer files so a translator sees its problems before anyone
/// collects. Reads files only; prints one line per answer and the count still missing; exits
/// non-zero when any answer fails to parse or lint.
/// </summary>
public static class AnswersCheckCommand
{
    public const int Clean = 0;
    public const int Problems = 1;
    public const int NoBatch = 66;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Run over a batch directory; <paramref name="cards"/> resolves an oracle id to its corpus card (null when unknown).</summary>
    public static int Run(string batchDir, Func<string, CorpusCard?> cards, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(log);
        var batchPath = Path.Combine(batchDir, FileTranslationClient.BatchFile);
        if (!File.Exists(batchPath))
        {
            log.WriteLine("answers-check: no " + FileTranslationClient.BatchFile + " in " + batchDir);
            return NoBatch;
        }

        var batch = JsonSerializer.Deserialize<FileTranslationClient.LocalBatch>(File.ReadAllText(batchPath), Options);
        if (batch is null)
        {
            log.WriteLine("answers-check: the batch file is empty");
            return NoBatch;
        }

        var problems = 0;
        var missing = 0;
        var ok = 0;
        foreach (var oracleId in batch.CustomIds)
        {
            var path = Path.Combine(batchDir, FileTranslationClient.AnswersDirectory, oracleId + ".json");
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }

            var card = cards(oracleId);
            var (definition, error) = TranslationPrompt.Parse(File.ReadAllText(path));
            if (definition is null)
            {
                problems++;
                log.WriteLine(FormattableString.Invariant($"  PARSE   {Name(card, oracleId)}: {error ?? "not a card definition"}"));
                continue;
            }

            if (card is null)
            {
                problems++;
                log.WriteLine(FormattableString.Invariant($"  UNKNOWN {oracleId}: the corpus has no card with this oracle id"));
                continue;
            }

            var lint = Cards.Linter.Check(new CardFile(
                card.OracleId,
                card.Name,
                card.OracleHash,
                IrVersion.Current,
                definition,
                ImplementationStatus.Translated,
                new TranslatedProvenance([batch.Model], "local", Agreed: false, batch.BatchId),
                card.Keywords));
            var errors = lint.Findings.Where(f => f.Severity == LintSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                problems++;
                log.WriteLine(FormattableString.Invariant($"  LINT    {card.Name}: {string.Join("; ", errors.Select(f => f.Code + " " + f.Message + " at " + f.Path))}"));
                continue;
            }

            ok++;
            var unsupported = Cards.Linter.Walk(definition).OfType<Unsupported>().Count();
            log.WriteLine(FormattableString.Invariant($"  ok      {card.Name}{(unsupported > 0 ? $" ({unsupported} unsupported)" : string.Empty)}"));
        }

        log.WriteLine(FormattableString.Invariant($"answers-check {batch.BatchId}: {ok} ok, {problems} with problems, {missing} missing of {batch.CustomIds.Count}"));
        return problems > 0 ? Problems : Clean;
    }

    private static string Name(CorpusCard? card, string oracleId) => card?.Name ?? oracleId;
}
