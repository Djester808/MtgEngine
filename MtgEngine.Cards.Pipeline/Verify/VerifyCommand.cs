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
    IReadOnlyList<VerifyOutcome> Outcomes,
    int Crashed = 0,
    int Unsupported = 0);

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

    /// <summary>
    /// A run that ended is a success whatever it found. A failing card is the measurement, not an
    /// error: the whole point of the loop is to count what does not play yet, so a directory of
    /// failures still exits zero and the counts in the summary are what a caller reads.
    /// </summary>
    public const int Completed = 0;

    /// <summary>Run over a directory and print the summary. The exit code is for the shell; the files are the record.</summary>
    public static int Run(string cardsDir, string? only, TextWriter log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var directory = CardDirectory.Load(cardsDir);
        foreach (var bad in directory.Unreadable)
            log.WriteLine("verify: could not read " + bad);

        var summary = Verify(directory, new CardFileWriter(cardsDir), only, log, runner: null, ct);
        if (summary is null)
        {
            log.WriteLine(FormattableString.Invariant($"verify: nothing to verify in {cardsDir}."));
            return NothingToDo;
        }

        return Completed;
    }

    /// <summary>The run itself, over a directory that is already read, so a test can supply its own.</summary>
    /// <param name="runner">
    /// How the run gets its runner, given the directory it plays over. The default is the real
    /// engine, which is the only thing this command ever uses; a test hands in a runner of its own
    /// so the crash path — a card whose play throws out of the engine — keeps a test of its own.
    /// It used to be proved with a definition that made the interpreter throw, and that card is now
    /// refused before a game starts (seam-requests/P12.md § 4), which left the path untested.
    /// </param>
    public static VerifySummary? Verify(
        CardDirectory directory,
        CardFileWriter writer,
        string? only,
        TextWriter log,
        Func<CardDirectory, IScenarioRunner>? runner = null,
        CancellationToken ct = default)
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
        // CR 101.4: a generated scenario cannot name an option id, so the runner answers the
        // questions it did not ask with the first legal option. A scenario that answers its own
        // questions still does (seam-requests/P8.md § 2).
        // CR 101.4: who answers is the scenario's own opt-in (ScenarioSetup.AutoAnswer) and no
        // longer the command's, so a card file's hand-written scenario keeps its own answers while
        // the generated one beside it is answered first-legal.
        var play = runner ?? (cards => new ScenarioRunner(new Game(Subsystems.Wave2(), cards)));
        var stage = new VerifyStage(play(directory), writer, engineHash);
        var at = DateTimeOffset.UtcNow;

        var outcomes = new List<VerifyOutcome>();
        var crashes = new List<VerifyOutcome>();
        var unsupported = new List<VerifyOutcome>();
        var noScenarios = 0;
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (NotPlayable(file) is { } reason)
            {
                unsupported.Add(Refused(file, reason, log));
                continue;
            }

            CardFile ready;
            try
            {
                ready = WithScenarios(file);
            }
            catch (Exception generating) when (generating is not OperationCanceledException)
            {
                crashes.Add(Crashed(file, generating, log));
                continue;
            }

            if (ready.ScenarioList.Count == 0)
            {
                // Untouched: a card with nothing to play is not a failure, it is a gap in the
                // scenarios, and rewriting its file would only restate that.
                noScenarios++;
                log.WriteLine(FormattableString.Invariant($"  no scenarios  {file.Name} ({file.OracleId})"));
                continue;
            }

            try
            {
                var outcome = stage.Verify(ready, runId, at, ct);
                outcomes.Add(outcome);
                log.WriteLine(Line(outcome));
            }
            catch (Exception playing) when (playing is not OperationCanceledException)
            {
                // One card that makes the engine throw used to end the run and lose every card
                // after it. The exception is this card's result, in the words the engine used.
                crashes.Add(Crashed(ready, playing, log));
            }
        }

        var summary = new VerifySummary(
            engineHash,
            runId,
            outcomes.Count(o => o.Verified),
            outcomes.Count(o => !o.Verified),
            noScenarios,
            [.. outcomes, .. crashes, .. unsupported],
            crashes.Count,
            unsupported.Count);
        log.WriteLine(FormattableString.Invariant(
            $"verify: {summary.Verified} verified, {summary.Failed} failed, {summary.NoScenarios} with no scenarios, {summary.Unsupported} unsupported, {summary.Crashed} crashed; {engineHash}, run {runId}"));
        return summary;
    }

    /// <summary>
    /// Why this card is not played at all: the first <see cref="MtgEngine.Cards.Unsupported"/> marker
    /// in its definition when no part of the card is expressible, or null when something still is.
    /// </summary>
    /// <remarks>
    /// A marker is the translator saying, in the vocabulary's own words, that that clause is not
    /// expressed — so a game played from it would be a game of a card nobody wrote. Refusing before
    /// the game starts is not the same as the engine throwing when it reaches the marker: the run
    /// has a reason to report rather than an exception, and the count says how many cards are
    /// waiting on vocabulary rather than on the engine.
    ///
    /// The refusal is per ability, not per card (CR 113.7b with CR 602.2): an ability is its own
    /// object, and one clause nobody could write says nothing about the ability beside it. Emergence
    /// Zone's flash permission carried the only marker on the card and took its <c>{T}: Add {C}</c>
    /// down with it for seven waves (seam-requests/P25.md § 4). The generators refuse the marked
    /// ability — <see cref="SmokeActivation"/> per activation, <see cref="SmokeScenario"/> for the
    /// cast — and the file is called unsupported only when nothing is left for them to play: every
    /// ability is marked, or a marker sits on no ability at all and so belongs to the card itself.
    /// </remarks>
    [RuleRef("602.2")]
    private static string? NotPlayable(CardFile file)
    {
        var markers = SmokeScenario.Markers(file.Definition);
        if (markers.Count == 0)
            return null;

        // The abilities are disjoint objects, so their markers add up; a marker the sum does not
        // reach is on the printed data itself and there is no ability to refuse in the card's place.
        var abilities = file.Definition.Faces.SelectMany(f => f.AbilityList).ToList();
        var onAbilities = abilities.Sum(a => SmokeScenario.Markers(a).Count);
        var anyExpressible = abilities.Any(a => SmokeScenario.Markers(a).Count == 0);
        if (onAbilities == markers.Count && anyExpressible)
            return null;

        // The reason is the first ability's marker in printed order; the walk keeps no order of its own.
        var first = abilities.Select(a => SmokeScenario.Markers(a)).FirstOrDefault(m => m.Count > 0);
        return (first is null ? markers[0] : first[0]).Reason;
    }

    /// <summary>
    /// A card whose definition says part of it is unsupported. Its file is left exactly as it was —
    /// the status it already has is still the truth about it — and the reason is recorded as the
    /// first expectation's actual, the same place <see cref="Crashed"/> puts an exception, so the
    /// dispute queue reads one shape whatever stopped the card from playing.
    /// </summary>
    private static VerifyOutcome Refused(CardFile file, string reason, TextWriter log)
    {
        var actual = nameof(Unsupported) + ": " + reason;
        var results = (IReadOnlyList<ScenarioResult>)[new ScenarioResult("smoke-" + file.OracleId, false, [], actual)];
        log.WriteLine(FormattableString.Invariant($"  unsupported   {file.Name} ({file.OracleId}): {reason}"));
        return new VerifyOutcome(file, results, null);
    }

    /// <summary>
    /// A card whose scenario threw inside the engine. Its file is left exactly as it was — a crash
    /// says nothing new about the card's content — except that a card which claimed to be verified
    /// no longer does, and the exception is recorded as the first expectation's actual so the run's
    /// output names the defect rather than "it failed".
    /// </summary>
    private static VerifyOutcome Crashed(CardFile file, Exception error, TextWriter log)
    {
        var actual = error.GetType().Name + ": " + error.Message;
        var scenario = file.ScenarioList.FirstOrDefault();
        var results = scenario is null
            ? (IReadOnlyList<ScenarioResult>)[new ScenarioResult("smoke-" + file.OracleId, false, [], actual)]
            : [new ScenarioResult(scenario.Id, false, [.. scenario.Expect.Select((e, i) => new ExpectationResult(e, false, i == 0 ? actual : "not run"))], actual)];

        var demoted = file.Status == ImplementationStatus.Verified
            ? file with { Status = ImplementationStatus.Translated }
            : file;
        log.WriteLine(FormattableString.Invariant($"  CRASHED       {file.Name} ({file.OracleId}): {actual}"));
        return new VerifyOutcome(demoted, results, null);
    }

    /// <summary>
    /// The scenarios a card is verified by: the ones a person or a ruling put there, kept exactly
    /// as they are, plus the smoke scenario generated from the card's own definition, generated
    /// afresh every run.
    /// </summary>
    /// <remarks>
    /// A stored scenario whose source is this generator is this generator's own output, and a
    /// previous run wrote it into the file. Keeping it would freeze the generator at whatever it
    /// said the day the card was first verified: every later improvement — an Aura cast at what
    /// its filter describes, a printed cost naming the body it eats, an opt-in to being answered —
    /// would be invisible to exactly the cards that already have a scenario, and the run would
    /// report the old shape as the card's behaviour. `cards/` is generated (CARD_ENGINE_PLAN.md),
    /// so the generator, and not the file, is the authority on its own scenarios.
    /// </remarks>
    public static CardFile WithScenarios(CardFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var scenarios = file.ScenarioList.Where(NotGenerated).ToList();
        var smoke = SmokeScenario.Of(file);
        if (smoke is not null)
            scenarios.Add(smoke);
        // CR 602.2: an activated ability is a second thing the card does, and a card whose spell
        // cannot be asserted may still have an ability that can — so these are added whether or not
        // the cast scenario was generated.
        scenarios.AddRange(SmokeActivation.Of(file));

        return scenarios.Count == 0 && file.ScenarioList.Count == 0 ? file : file with { Scenarios = scenarios };

        static bool NotGenerated(Scenario scenario) =>
            scenario.Source is not GeneratedSource generated
            || !string.Equals(generated.Generator, SmokeScenario.Generator, StringComparison.Ordinal);
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
