using System.Diagnostics;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The runner is where "coverage is counted in verified plays" stops being a slogan: a scenario
/// is a board, some steps and some expectations, and it either happens in the real engine or it
/// does not. These tests check that a passing scenario passes, that a failing one says what was
/// actually there, that a refusal can be the thing under test, and that a thousand of them run
/// inside a minute — the plan's performance gate, so the corpus's scenarios run in hours.
/// </summary>
public sealed class ScenarioRunnerTests
{
    private const string Bolt = "runner-bolt";
    private const string Bear = "runner-bear";

    [Fact]
    [RuleRef("608.2")]
    public void A_burn_spell_puts_the_creature_it_killed_in_the_graveyard()
    {
        var result = Run(Kill([new InZone(Bear, Zone.Graveyard), new InZone(Bolt, Zone.Graveyard), new StackEmpty()]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
        Assert.All(result.Results, r => Assert.True(r.Passed));
        Assert.NotNull(result.Log);
        Assert.NotEmpty(result.Log!);
    }

    [Fact]
    [RuleRef("400.7")]
    public void An_expectation_of_the_wrong_zone_fails_and_names_the_zone_the_card_is_in()
    {
        var result = Run(Kill([new InZone(Bear, Zone.Battlefield)]));

        Assert.False(result.Passed);
        var only = Assert.Single(result.Results);
        Assert.False(only.Passed);
        Assert.Equal(nameof(Zone.Graveyard), only.Actual);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_step_the_engine_refuses_passes_when_the_scenario_expected_the_refusal()
    {
        // A spell in the graveyard is not a spell anyone may cast, CR 601.3 and 608.2m.
        var scenario = new Scenario(
            "runner-refusal",
            "casting from the graveyard is refused",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A", Graveyard: [new CardRef(Bolt)]), new PlayerSetup("B")], "A"),
            [new ExpectRefusedStep(new CastStep("A", Bolt))],
            [new StackEmpty()]);

        var result = Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_step_the_engine_allows_fails_when_the_scenario_expected_a_refusal()
    {
        var scenario = new Scenario(
            "runner-wrong-refusal",
            "the engine allowed it",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A", Hand: [new CardRef(Bolt)], Battlefield: [new CardRef(Bear)], ManaPool: "RRR"), new PlayerSetup("B")], "A"),
            [new ExpectRefusedStep(new CastStep("A", Bolt, [Bear]))],
            [new StackEmpty()]);

        var result = Run(scenario);

        Assert.False(result.Passed);
        Assert.Contains("expected it to refuse", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("104.3b")]
    public void A_life_expectation_reads_the_life_the_setup_gave_a_player()
    {
        var scenario = new Scenario(
            "runner-life",
            "life is what the setup said",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A", Life: 13), new PlayerSetup("B")], "A"),
            [],
            [new Life("A", 13), new Life("B", 20)]);

        Assert.True(Run(scenario).Passed);
    }

    /// <summary>The plan's performance gate: an engine change that halves throughput fails here.</summary>
    [Fact]
    public void A_thousand_trivial_scenarios_run_in_under_a_minute()
    {
        var runner = Runner();
        var scenarios = Enumerable.Range(0, 1000).Select(i => new Scenario(
            $"runner-throughput-{i}",
            "a board and one expectation",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A", Battlefield: [new CardRef(Bear)]), new PlayerSetup("B")], "A"),
            [],
            [new PowerToughness(Bear, 2, 2), new Life("A", 20)])).ToList();

        var clock = Stopwatch.StartNew();
        var results = scenarios.Select(s => runner.Run(s)).ToList();
        clock.Stop();

        Assert.All(results, r => Assert.True(r.Passed, r.Error));
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1), $"1,000 scenarios took {clock.Elapsed}.");
    }

    private static Scenario Kill(IReadOnlyList<Expectation> expect) => new(
        "runner-bolt-kills",
        "a burn spell kills a creature",
        new GeneratedSource("test"),
        new ScenarioSetup(
            [
                new PlayerSetup("A", Hand: [new CardRef(Bolt)], ManaPool: "RRRRR"),
                new PlayerSetup("B", Battlefield: [new CardRef(Bear)]),
            ],
            "A"),
        [new CastStep("A", Bolt, [Bear]), new ResolveAllStep()],
        expect);

    private static ScenarioResult Run(Scenario scenario) => Runner().Run(scenario);

    private static ScenarioRunner Runner() =>
        new(new Game(Subsystems.Wave2(), new ScenarioRunnerDefinitions(BoltCard(), BearCard())));

    private static CardDefinition BoltCard() =>
        new(Bolt, Bolt, Layout.Normal,
            [new Face(Bolt, FaceRole.Main, new TypeLine(CardType.Instant, Supertype.None, null), ManaCost: "{R}",
                Abilities: [new SpellAbility(new DealDamage(new Fixed(3), To: new Target(ObjectFilter.Creature)))])]);

    private static CardDefinition BearCard() =>
        new(Bear, Bear, Layout.Normal,
            [new Face(Bear, FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, ["Bear"]), ManaCost: "{1}{G}",
                Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);

    private sealed class ScenarioRunnerDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
