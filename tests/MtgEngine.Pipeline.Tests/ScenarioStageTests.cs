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
        var count = Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Hand);
        Assert.Equal(SmokeScenario.HandSize + 2, count.Count);
        // The library the draw came from is one smaller, which is the other half of CR 121.1.
        Assert.Equal(SmokeScenario.LibrarySize - 2, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Library).Count);
        Assert.Equal(SmokeScenario.LibrarySize, scenario.Setup.Players[0].Library!.Count);
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
    public void A_land_is_played_and_not_cast()
    {
        var scenario = SmokeScenario.Of(File("a forest", null, CardType.Land));

        Assert.NotNull(scenario);
        var play = Assert.IsType<PlayLandStep>(scenario!.Steps[0]);
        Assert.Equal("a forest", play.Card);
        Assert.Equal(SmokeScenario.Caster, play.Player);
        Assert.DoesNotContain(scenario.Steps, s => s is CastStep);
        var arrived = Assert.Single(scenario.Expect.OfType<InZone>());
        Assert.Equal(Zone.Battlefield, arrived.Zone);
        Assert.Contains("playing", scenario.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spell_with_X_in_its_cost_announces_one_and_says_so()
    {
        var scenario = SmokeScenario.Of(Spell("burn X", new DealDamage(new Fixed(2), ToPlayer: new EachOpponent()), mana: "{X}{R}"));

        Assert.NotNull(scenario);
        // CR 601.2b: X is chosen as the spell is cast, and a scenario that does not choose it is refused.
        Assert.Equal(SmokeScenario.AnnouncedX, Assert.IsType<CastStep>(scenario!.Steps[0]).X);
        Assert.Contains("X=1", scenario.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Destroying_a_creature_puts_the_opponents_creature_in_their_graveyard()
    {
        var scenario = SmokeScenario.Of(Spell("kill it", new Destroy(new Target(ObjectFilter.Creature))));

        Assert.NotNull(scenario);
        var body = Assert.Single(scenario!.Setup.Players[1].Battlefield!);
        Assert.Equal(body.Alias ?? body.Name, Assert.IsType<CastStep>(scenario.Steps[0]).Targets!.Single());
        var dead = scenario.Expect.OfType<InZone>().Single(z => z.Card == (body.Alias ?? body.Name));
        Assert.Equal(Zone.Graveyard, dead.Zone);
        Assert.Equal(SmokeScenario.Opponent, dead.Owner);
    }

    [Fact]
    public void Exiling_a_creature_puts_it_in_exile()
    {
        var scenario = SmokeScenario.Of(Spell("blink it away", new Exile(new Target(ObjectFilter.Creature))));

        Assert.NotNull(scenario);
        var body = Assert.Single(scenario!.Setup.Players[1].Battlefield!);
        Assert.Equal(Zone.Exile, scenario.Expect.OfType<InZone>().Single(z => z.Card == (body.Alias ?? body.Name)).Zone);
    }

    [Fact]
    public void Making_tokens_expects_them_on_the_casters_battlefield()
    {
        var token = new TokenSpec("Soldier", Color.White, CardType.Creature, ["Soldier"], Power: 1, Toughness: 1);
        var scenario = SmokeScenario.Of(Spell("two soldiers", new CreateToken(token, new Fixed(2))));

        Assert.NotNull(scenario);
        var count = scenario!.Expect.OfType<ZoneCount>().Single(z => z.Zone == Zone.Battlefield);
        Assert.Equal(SmokeScenario.Caster, count.Player);
        Assert.Equal(2, count.Count);
    }

    [Fact]
    public void Putting_counters_on_a_creature_expects_the_counters_on_it()
    {
        var scenario = SmokeScenario.Of(Spell(
            "grow it",
            new PutCounters(new Target(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())), "+1/+1", new Fixed(3))));

        Assert.NotNull(scenario);
        // The target is the caster's own, because that is what the filter said (CR 115.1).
        var body = Assert.Single(scenario!.Setup.Players[0].Battlefield!);
        var counters = Assert.Single(scenario.Expect.OfType<CountersAre>());
        Assert.Equal(body.Alias ?? body.Name, counters.Card);
        Assert.Equal("+1/+1", counters.Counter);
        Assert.Equal(3, counters.Count);
    }

    [Fact]
    public void Searching_a_library_for_a_land_takes_one_out_of_it()
    {
        var scenario = SmokeScenario.Of(Spell("go get one", new Search(new ObjectFilter(Types: CardType.Land), Zone.Hand), CardType.Sorcery));

        Assert.NotNull(scenario);
        Assert.Equal(SmokeScenario.HandSize + 1, scenario!.Expect.OfType<ZoneCount>().Single(z => z.Zone == Zone.Hand).Count);
        Assert.Equal(SmokeScenario.LibrarySize - 1, scenario.Expect.OfType<ZoneCount>().Single(z => z.Zone == Zone.Library).Count);
    }

    [Fact]
    public void Countering_a_spell_casts_one_first_and_expects_it_in_the_graveyard()
    {
        var scenario = SmokeScenario.Of(Spell("say no", new Counter(new Target(new ObjectFilter(Spell: true)))));

        Assert.NotNull(scenario);
        // CR 701.5 needs something on the stack, so the opponent casts before the counterspell does.
        var first = Assert.IsType<CastStep>(scenario!.Steps[0]);
        Assert.Equal(SmokeScenario.Opponent, first.Player);
        var counterspell = Assert.IsType<CastStep>(scenario.Steps[1]);
        Assert.Equal(first.Card, counterspell.Targets!.Single());
        var countered = scenario.Expect.OfType<InZone>().Single(z => z.Card == first.Card);
        Assert.Equal(Zone.Graveyard, countered.Zone);
        Assert.Equal(SmokeScenario.Opponent, countered.Owner);
    }

    [Fact]
    public void Milling_an_opponent_moves_their_library_into_their_graveyard()
    {
        var scenario = SmokeScenario.Of(Spell("mill three", new Mill(new Fixed(3), new EachOpponent()), CardType.Sorcery));

        Assert.NotNull(scenario);
        var counts = scenario!.Expect.OfType<ZoneCount>().Where(z => z.Player == SmokeScenario.Opponent).ToList();
        Assert.Equal(SmokeScenario.LibrarySize - 3, counts.Single(z => z.Zone == Zone.Library).Count);
        Assert.Equal(3, counts.Single(z => z.Zone == Zone.Graveyard).Count);
    }

    [Fact]
    public void Making_an_opponent_discard_shrinks_their_hand()
    {
        var scenario = SmokeScenario.Of(Spell("drop two", new Discard(new Fixed(2), new EachOpponent()), CardType.Sorcery));

        Assert.NotNull(scenario);
        var counts = scenario!.Expect.OfType<ZoneCount>().Where(z => z.Player == SmokeScenario.Opponent).ToList();
        Assert.Equal(SmokeScenario.HandSize - 2, counts.Single(z => z.Zone == Zone.Hand).Count);
        Assert.Equal(2, counts.Single(z => z.Zone == Zone.Graveyard).Count);
    }

    [Fact]
    public void A_target_the_board_cannot_place_gets_no_scenario_rather_than_a_refused_cast() =>
        // A blue creature with flying is not furniture the runner has, and casting without a legal
        // target is refused — which would report the generator's gap as the card's failure.
        Assert.Null(SmokeScenario.Of(Spell(
            "kill a flier",
            new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, Has: KeywordKind.Flying))))));

    [Fact]
    public void Two_targeted_effects_are_more_than_a_smoke_scenario_can_aim() =>
        Assert.Null(SmokeScenario.Of(Spell(
            "kill two",
            new Sequence([new Destroy(new Target(ObjectFilter.Creature)), new Exile(new Target(ObjectFilter.Creature))]))));

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

    /// <summary>An instant whose one spell ability is the effect under test.</summary>
    private static CardFile Spell(string name, Effect effect, CardType types = CardType.Instant, string mana = "{1}")
    {
        var file = File(name, new SpellAbility(effect), types);
        var face = file.Definition.Faces[0] with { ManaCost = mana };
        return file with { Definition = file.Definition with { Faces = [face] } };
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
