using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// Runs a scenario through the real engine: builds the board from the setup, applies each
/// step as a command, checks each expectation against the final state. The shape is the
/// wave-0 seam; wave 2's P4 fills it in. Until then every scenario reports itself unrun,
/// never passed.
/// </summary>
public sealed class ScenarioRunner(Game game) : IScenarioRunner
{
    public Game Game { get; } = game;

    public ScenarioResult Run(Scenario scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ct.ThrowIfCancellationRequested();
        return new ScenarioResult(
            scenario.Id,
            Passed: false,
            Results: [.. scenario.Expect.Select(e => new ExpectationResult(e, false, "not run"))],
            Error: "The scenario runner is not built yet: wave 2, agent P4.");
    }
}
