using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Verify;

/// <summary>What one verification run did: the counts a person reads, and every outcome behind them.</summary>
public sealed record VerifySummary(
    string EngineHash,
    string RunId,
    int Verified,
    int Failed,
    int NoScenarios,
    IReadOnlyList<VerifyOutcome> Outcomes);

/// <summary>
/// <c>pipeline verify &lt;cardsDir&gt; [--only &lt;oracleId&gt;]</c>: the third rule of
/// CARD_ENGINE_PLAN.md as a command. Every Translated or Verified card in the directory gets its
/// smoke scenario generated, its stored scenarios added, and all of them run through the real
/// <see cref="ScenarioRunner"/> on <see cref="Subsystems.Wave2"/>, with the directory itself as
/// the definition store. <see cref="VerifyStage"/> writes the verdict and the engine hash.
/// </summary>
/// <remarks>
/// It never reaches the network and never reads a key: verification is a play, and a play is the
/// engine's business. The engine hash comes from <see cref="EventLogSerializer.EngineHash"/> here,
/// because this is the caller that built the runner and therefore the only thing that knows which
/// engine it built (seam-requests/DECISIONS.md, wave 3, P4).
/// </remarks>
public static class VerifyCommand
{
    /// <summary>Nothing in the directory to verify.</summary>
    public const int NothingToDo = 64;

    /// <summary>At least one card's scenarios failed. The files still say so; this is for a script.</summary>
    public const int Failures = 1;

    /// <summary>Run over a directory and print the summary. The exit code is for the shell; the files are the record.</summary>
    public static int Run(string cardsDir, string? only, TextWriter log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var directory = CardDirectory.Load(cardsDir);
        foreach (var bad in directory.Unreadable)
            log.WriteLine("verify: could not read " + bad);

        var summary = Verify(directory, new CardFileWriter(cardsDir), only, log, ct);
        if (summary is null)
        {
            log.WriteLine(FormattableString.Invariant($"verify: nothing to verify in {cardsDir}."));
            return NothingToDo;
        }

        return summary.Failed > 0 ? Failures : 0;
    }

    /// <summary>The run itself, over a directory that is already read, so a test can supply its own.</summary>
    public static VerifySummary? Verify(CardDirectory directory, CardFileWriter writer, string? only, TextWriter log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);

        var candidates = directory.Files
            .Where(f => f.Status is ImplementationStatus.Translated or ImplementationStatus.Verified)
            .Where(f => only is null || string.Equals(f.OracleId, only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var engineHash = EventLogSerializer.EngineHash();
        var runId = RunId();
        var stage = new VerifyStage(new ScenarioRunner(new Game(Subsystems.Wave2(), directory)), writer, engineHash);
        var at = DateTimeOffset.UtcNow;

        var outcomes = new List<VerifyOutcome>();
        var noScenarios = 0;
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var ready = WithScenarios(file);
            if (ready.ScenarioList.Count == 0)
            {
                // Untouched: a card with nothing to play is not a failure, it is a gap in the
                // scenarios, and rewriting its file would only restate that.
                noScenarios++;
                log.WriteLine(FormattableString.Invariant($"  no scenarios  {file.Name} ({file.OracleId})"));
                continue;
            }

            var outcome = stage.Verify(ready, runId, at, ct);
            outcomes.Add(outcome);
            log.WriteLine(Line(outcome));
        }

        var summary = new VerifySummary(
            engineHash,
            runId,
            outcomes.Count(o => o.Verified),
            outcomes.Count(o => !o.Verified),
            noScenarios,
            outcomes);
        log.WriteLine(FormattableString.Invariant(
            $"verify: {summary.Verified} verified, {summary.Failed} failed, {summary.NoScenarios} with no scenarios; {engineHash}, run {runId}"));
        return summary;
    }

    /// <summary>
    /// The scenarios a card is verified by: the ones it carries — rulings, reports, hand-written
    /// checks — plus the smoke scenario generated from its own definition, which is added only
    /// when the card does not already carry one with that id, so a run is idempotent.
    /// </summary>
    public static CardFile WithScenarios(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var scenarios = new List<Scenario>(file.ScenarioList);
        var smoke = SmokeScenario.Of(file);
        if (smoke is not null && !scenarios.Any(s => string.Equals(s.Id, smoke.Id, StringComparison.Ordinal)))
            scenarios.Add(smoke);

        return scenarios.Count == file.ScenarioList.Count ? file : file with { Scenarios = scenarios };
    }

    private static string Line(VerifyOutcome outcome)
    {
        var file = outcome.File;
        if (outcome.Verified)
            return FormattableString.Invariant($"  verified      {file.Name} ({file.OracleId}) {outcome.Passed}/{outcome.Total}");

        var failed = outcome.Results.FirstOrDefault(r => !r.Passed);
        return FormattableString.Invariant(
            $"  FAILED        {file.Name} ({file.OracleId}) {outcome.Passed}/{outcome.Total}: {Why(failed)}");
    }

    /// <summary>The first failing expectation, in the words the runner used, because "it failed" is not a report.</summary>
    private static string Why(ScenarioResult? result)
    {
        if (result is null)
            return "no scenario ran";
        if (result.Error is { Length: > 0 } error)
            return result.ScenarioId + ": " + error;

        var bad = result.Results.FirstOrDefault(r => !r.Passed);
        return bad is null
            ? result.ScenarioId + ": no expectation failed"
            : FormattableString.Invariant($"{result.ScenarioId}: {bad.Expectation.GetType().Name} was {bad.Actual ?? "not met"}");
    }

    private static string RunId() =>
        "verify-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("n")[..6];
}
