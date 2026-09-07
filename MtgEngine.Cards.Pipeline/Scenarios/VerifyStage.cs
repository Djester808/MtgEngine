using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>What one card's verification run produced: the file as it now stands, and each scenario's result.</summary>
public sealed record VerifyOutcome(CardFile File, IReadOnlyList<ScenarioResult> Results, CardWrite? Written)
{
    public int Passed => Results.Count(r => r.Passed);

    public int Total => Results.Count;

    public bool Verified => File.Status == ImplementationStatus.Verified;
}

/// <summary>
/// Runs a card's scenarios through the real runner and records what happened.
/// </summary>
/// <remarks>
/// The third rule of the card plan: a card's status moves to <see cref="ImplementationStatus.Verified"/>
/// only when its scenarios pass in the engine, and the file then carries the engine hash and the
/// run id that prove it. A card that was Verified and now fails goes back to Translated, because
/// a stale pass is worse than no pass. This stage never reaches the network: it reads a file,
/// runs it, and writes the file back.
/// <para>
/// <paramref name="engineHash"/> is supplied rather than computed because the pipeline does not
/// reference <c>MtgEngine.Rules</c> — the caller that built the runner is the only thing that
/// knows which engine it built, and it passes <c>EventLogSerializer.EngineHash()</c> in.
/// </para>
/// </remarks>
public sealed class VerifyStage(IScenarioRunner runner, CardFileWriter writer, string engineHash)
{
    public string EngineHash { get; } = engineHash ?? throw new ArgumentNullException(nameof(engineHash));

    /// <summary>Run one card's scenarios and write the verdict into its file.</summary>
    public VerifyOutcome Verify(CardFile file, string runId, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var results = new List<ScenarioResult>(file.ScenarioList.Count);
        foreach (var scenario in file.ScenarioList)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(runner.Run(scenario, ct));
        }

        var passed = results.Count(r => r.Passed);
        var verified = results.Count > 0 && passed == results.Count;
        var updated = file with
        {
            Status = Status(file.Status, verified),
            Verification = new Verification(EngineHash, runId, passed, results.Count, at),
        };

        return new VerifyOutcome(updated, results, writer.Write(updated));
    }

    /// <summary>Every card in the directory with at least one scenario, in one run.</summary>
    public IReadOnlyList<VerifyOutcome> VerifyAll(IEnumerable<CardFile> files, string runId, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.Where(f => f.ScenarioList.Count > 0).Select(f => Verify(f, runId, at, ct))];
    }

    /// <summary>
    /// A pass verifies; a failure demotes a card that claimed to be verified and leaves every
    /// other status alone, so a failing scenario never invents a status the pipeline did not give.
    /// </summary>
    private static ImplementationStatus Status(ImplementationStatus current, bool verified)
    {
        if (current == ImplementationStatus.Excluded)
            return current;
        if (verified)
            return ImplementationStatus.Verified;
        return current == ImplementationStatus.Verified ? ImplementationStatus.Translated : current;
    }
}
