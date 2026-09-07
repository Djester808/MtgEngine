using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Agree;
using MtgEngine.Cards.Pipeline.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The scenario stages: the smoke scenario a card generates from its own definition, and the
/// verify stage that decides whether a card may call itself Verified. Neither reaches the
/// network, and the verify stage is exercised against a runner that says what the test wants
/// so the decision itself is what is under test.
/// </summary>
public sealed class ScenarioStageTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "ScenarioStageTests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void The_smoke_scenario_for_a_draw_two_asserts_the_hand_count()
    {
        var file = File("draw two", new SpellAbility(new Draw(new Fixed(2))), CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal($"smoke-{file.OracleId}", scenario!.Id);
        Assert.Equal(SmokeScenario.Generator, Assert.IsType<GeneratedSource>(scenario.Source).Generator);
        var count = Assert.Single(scenario.Expect.OfType<ZoneCount>());
        Assert.Equal(Zone.Hand, count.Zone);
        Assert.Equal(2, count.Count);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard });
        var cast = Assert.IsType<CastStep>(scenario.Steps[0]);
        Assert.Equal(file.Name, cast.Card);
    }

    [Fact]
    public void The_smoke_scenario_for_a_burn_spell_asserts_the_life_total()
    {
        var file = File("burn two", new SpellAbility(new DealDamage(new Fixed(2), ToPlayer: new EachOpponent())), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(18, Assert.Single(scenario!.Expect.OfType<Life>()).Value);
    }

    [Fact]
    public void A_permanent_smoke_scenario_expects_the_battlefield()
    {
        var scenario = SmokeScenario.Of(File("a bear", null, CardType.Creature));

        Assert.NotNull(scenario);
        Assert.Equal(Zone.Battlefield, Assert.Single(scenario!.Expect.OfType<InZone>()).Zone);
    }

    [Fact]
    public void A_card_whose_definition_asserts_nothing_gets_no_smoke_scenario() =>
        Assert.Null(SmokeScenario.Of(File("does nothing assertable", null, CardType.Instant)));

    [Fact]
    public void The_verify_stage_marks_a_card_verified_only_when_every_scenario_passed()
    {
        var file = WithScenarios(File("verified card", new SpellAbility(new Draw(new Fixed(1))), CardType.Sorcery), 2);
        var stage = new VerifyStage(new ScenarioStageRunner(_ => true), new CardFileWriter(_directory), "engine:test");

        var outcome = stage.Verify(file, "run-1", DateTimeOffset.UnixEpoch);

        Assert.True(outcome.Verified);
        Assert.Equal(ImplementationStatus.Verified, outcome.File.Status);
        Assert.NotNull(outcome.File.Verification);
        Assert.Equal("engine:test", outcome.File.Verification!.EngineHash);
        Assert.Equal("run-1", outcome.File.Verification.RunId);
        Assert.Equal(2, outcome.File.Verification.Passed);
        Assert.Equal(2, outcome.File.Verification.Total);

        // It was written, and reads back as what was written.
        var written = new CardFileWriter(_directory).Load(file.OracleId);
        Assert.NotNull(written);
        Assert.Equal(ImplementationStatus.Verified, written!.Status);
        Assert.Equal("engine:test", written.Verification!.EngineHash);
    }

    [Fact]
    public void One_failing_scenario_keeps_a_card_out_of_verified_and_demotes_one_that_claimed_it()
    {
        var file = WithScenarios(File("failing card", new SpellAbility(new Draw(new Fixed(1))), CardType.Sorcery), 2)
            with
        { Status = ImplementationStatus.Verified };
        var stage = new VerifyStage(new ScenarioStageRunner(s => s.EndsWith('0')), new CardFileWriter(_directory), "engine:test");

        var outcome = stage.Verify(file, "run-2", DateTimeOffset.UnixEpoch);

        Assert.False(outcome.Verified);
        Assert.Equal(ImplementationStatus.Translated, outcome.File.Status);
        Assert.Equal(1, outcome.File.Verification!.Passed);
        Assert.Equal(2, outcome.File.Verification.Total);
    }

    [Fact]
    public void A_card_with_no_scenarios_is_never_verified()
    {
        var stage = new VerifyStage(new ScenarioStageRunner(_ => true), new CardFileWriter(_directory), "engine:test");

        var outcome = stage.Verify(File("no scenarios", null, CardType.Instant), "run-3", DateTimeOffset.UnixEpoch);

        Assert.False(outcome.Verified);
        Assert.Equal(0, outcome.File.Verification!.Total);
    }

    [Fact]
    public void A_ruling_answer_that_is_not_json_is_an_error_and_not_a_scenario()
    {
        var ruling = new RulingRequest("oracle-1", "some card", "It does the thing.");

        Assert.NotNull(RulingsStage.Parse(ruling, "I am afraid I cannot help with that.").Error);
        Assert.NotNull(RulingsStage.Parse(ruling, "{ \"nope\": ").Error);
    }

    [Fact]
    public void A_ruling_answer_that_deserialises_becomes_a_scenario_sourced_from_the_ruling()
    {
        var ruling = new RulingRequest("oracle-1", "some card", "It does the thing.", "2020-01-01");
        var answer = System.Text.Json.JsonSerializer.Serialize(new Scenario(
            "model-chose-this-id",
            "the ruling",
            new GeneratedSource("model"),
            new ScenarioSetup([new PlayerSetup("A"), new PlayerSetup("B")], "A"),
            [new ResolveAllStep()],
            [new Life("A", 20)]), CardFileSerializer.Options);

        var parsed = RulingsStage.Parse(ruling, "Here it is:\n" + answer);

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Scenario);
        var source = Assert.IsType<RulingSource>(parsed.Scenario!.Source);
        Assert.Equal("oracle-1", source.OracleId);
        Assert.Equal("2020-01-01", source.PublishedAt);
        Assert.StartsWith("ruling-oracle-1-", parsed.Scenario.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rulings_prompt_carries_the_scenario_schema() =>
        Assert.Contains("ScenarioSetup", RulingsStage.SystemPrompt(), StringComparison.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static CardFile File(string name, Ability? ability, CardType types) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: "{1}", Abilities: ability is null ? null : [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private static CardFile WithScenarios(CardFile file, int count) => file with
    {
        Scenarios = [.. Enumerable.Range(0, count).Select(i => new Scenario(
            $"{file.OracleId}-{i}",
            "a scenario",
            new GeneratedSource("test"),
            new ScenarioSetup([new PlayerSetup("A")], "A"),
            [],
            [new Life("A", 20)]))],
    };

    /// <summary>A runner that passes the scenarios the test names, so the stage's decision is what is tested.</summary>
    private sealed class ScenarioStageRunner(Func<string, bool> passes) : IScenarioRunner
    {
        public ScenarioResult Run(Scenario scenario, CancellationToken ct = default) =>
            new(scenario.Id, passes(scenario.Id), [], passes(scenario.Id) ? null : "the runner was told to fail this one");
    }
}
