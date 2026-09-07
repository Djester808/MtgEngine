using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The four things P9 measured a generated scenario could not say (seam-requests/P9.md): who
/// answers the game's questions, which permanent a printed cost eats, what is in a mana pool, and
/// what an Aura is attached to. Each has to be in the name table, in the scenario schema the
/// generation stage is constrained to, and readable beside the scenarios already written, or a
/// card that needs it is reported as failing when it is only unsayable.
/// </summary>
public sealed class ScenarioVocabularyTests
{
    private static readonly JsonSerializerOptions Options = CardFileSerializer.Options;

    private static string ScenarioWrite(Scenario scenario) => JsonSerializer.Serialize(scenario, Options);

    private static Scenario ScenarioRead(string json) =>
        JsonSerializer.Deserialize<Scenario>(json, Options) ?? throw new JsonException("null scenario");

    private static Scenario ScenarioRoundTrip(Scenario scenario) => ScenarioRead(ScenarioWrite(scenario));

    /// <summary>The plainest scenario the runner accepts, with the steps and expectations passed in.</summary>
    private static Scenario ScenarioWith(IReadOnlyList<ScenarioStep> steps, IReadOnlyList<Expectation> expect, bool autoAnswer = false) =>
        new(
            Id: "scenario-vocabulary",
            Title: "A scenario that exercises the wave-7 additions",
            Source: new GeneratedSource("smoke"),
            Setup: new ScenarioSetup(
                Players: [new PlayerSetup("alice"), new PlayerSetup("bob")],
                ActivePlayer: "alice",
                AutoAnswer: autoAnswer),
            Steps: steps,
            Expect: expect);

    private static string ScenarioSchemaText() =>
        CardFileSerializer.ScenarioSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    [Fact]
    public void ManaPoolIs_and_AttachedToIs_are_registered_under_Expectation()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Expectation));

        Assert.True(registered.ContainsKey(typeof(ManaPoolIs)), "ManaPoolIs has no [JsonDerivedType] on Expectation.");
        Assert.Equal(nameof(ManaPoolIs), registered[typeof(ManaPoolIs)]);
        Assert.True(registered.ContainsKey(typeof(AttachedToIs)), "AttachedToIs has no [JsonDerivedType] on Expectation.");
        Assert.Equal(nameof(AttachedToIs), registered[typeof(AttachedToIs)]);
    }

    [Fact]
    public void ManaPoolIs_round_trips_with_its_kind()
    {
        var json = ScenarioWrite(ScenarioWith([new PassStep("alice")], [new ManaPoolIs("alice", "{G}{G}")]));

        Assert.Equal("ManaPoolIs", JsonNode.Parse(json)!["expect"]![0]!["kind"]!.GetValue<string>());

        var back = ScenarioRead(json);
        var expectation = Assert.IsType<ManaPoolIs>(back.Expect[0]);
        Assert.Equal("alice", expectation.Player);
        Assert.Equal("{G}{G}", expectation.Mana);
        Assert.Equal(json, ScenarioWrite(back));
    }

    [Fact]
    public void AttachedToIs_round_trips_with_its_kind()
    {
        var json = ScenarioWrite(ScenarioWith([new PassStep("alice")], [new AttachedToIs("Rancor", "Grizzly Bears")]));

        Assert.Equal("AttachedToIs", JsonNode.Parse(json)!["expect"]![0]!["kind"]!.GetValue<string>());

        var back = ScenarioRead(json);
        var expectation = Assert.IsType<AttachedToIs>(back.Expect[0]);
        Assert.Equal("Rancor", expectation.Card);
        Assert.Equal("Grizzly Bears", expectation.To);
        Assert.Equal(json, ScenarioWrite(back));
    }

    [Fact]
    public void The_scenario_schema_names_the_two_new_expectations()
    {
        var schema = ScenarioSchemaText();

        Assert.Contains("\"ManaPoolIs\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"AttachedToIs\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scenario_schema_names_auto_answer_and_cost_objects()
    {
        var schema = ScenarioSchemaText();

        Assert.Contains("\"autoAnswer\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"costObjects\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scenario_carrying_all_four_new_members_round_trips_whole()
    {
        var scenario = ScenarioWith(
            [
                // CR 601.2c: Village Rites names the creature its own cost eats.
                new CastStep("alice", "Village Rites", CostObjects: ["bear"]),
                // CR 602.2b: the same field, for an activation cost.
                new ActivateStep("alice", "Altar", AbilityIndex: 0, CostObjects: ["token"]),
                new ResolveAllStep(),
            ],
            [
                new ManaPoolIs("alice", "{B}"),
                new AttachedToIs("Rancor", "bear"),
            ],
            autoAnswer: true);

        var json = ScenarioWrite(scenario);
        var back = ScenarioRoundTrip(scenario);

        Assert.True(back.Setup.AutoAnswer);
        Assert.Equal(["bear"], Assert.IsType<CastStep>(back.Steps[0]).CostObjects);
        Assert.Equal(["token"], Assert.IsType<ActivateStep>(back.Steps[1]).CostObjects);
        Assert.Equal("{B}", Assert.IsType<ManaPoolIs>(back.Expect[0]).Mana);
        Assert.Equal("bear", Assert.IsType<AttachedToIs>(back.Expect[1]).To);
        // The whole file, not just the members read back: a member the writer drops or reorders
        // shows up here even when every assertion above passes. Record equality would not do it —
        // the lists compare by reference.
        Assert.Equal(json, ScenarioWrite(back));
        Assert.Equal(scenario.Setup with { Players = back.Setup.Players }, back.Setup);
    }

    [Fact]
    public void A_scenario_written_before_these_members_still_reads()
    {
        // IrVersion is not bumped: every addition here is an optional member with the old
        // behaviour as its default, so a scenario file written last wave deserialises unchanged.
        const string json = """
            {
              "id": "old",
              "title": "An older scenario",
              "source": { "kind": "GeneratedSource", "generator": "smoke" },
              "setup": {
                "players": [ { "name": "alice" }, { "name": "bob" } ],
                "activePlayer": "alice",
                "startAt": "PrecombatMain",
                "seed": 1,
                "turn": 1
              },
              "steps": [
                { "kind": "CastStep", "player": "alice", "card": "Lightning Bolt", "targets": [ "bob" ] },
                { "kind": "ActivateStep", "player": "alice", "card": "Altar", "abilityIndex": 0 },
                { "kind": "ResolveAllStep" }
              ],
              "expect": [ { "kind": "Life", "player": "bob", "value": 17 } ]
            }
            """;

        var scenario = ScenarioRead(json);

        Assert.False(scenario.Setup.AutoAnswer);
        Assert.Null(Assert.IsType<CastStep>(scenario.Steps[0]).CostObjects);
        Assert.Null(Assert.IsType<ActivateStep>(scenario.Steps[1]).CostObjects);
        Assert.Equal(17, Assert.IsType<Life>(scenario.Expect[0]).Value);
    }

    [Fact]
    public void AutoAnswer_is_off_and_cost_objects_are_absent_unless_a_scenario_asks()
    {
        var setup = new ScenarioSetup([new PlayerSetup("alice")], "alice");

        Assert.False(setup.AutoAnswer);
        Assert.Null(new CastStep("alice", "Lightning Bolt").CostObjects);
        Assert.Null(new ActivateStep("alice", "Altar", 0).CostObjects);

        // WhenWritingNull: a scenario that does not use them looks exactly as it did before.
        var json = ScenarioWrite(ScenarioWith([new CastStep("alice", "Lightning Bolt")], [new StackEmpty()]));
        Assert.DoesNotContain("costObjects", json, StringComparison.Ordinal);
        Assert.Contains("\"autoAnswer\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Ring_carries_no_emblem_of_its_own()
    {
        // CR 701.54b prints the emblem's four abilities in the rulebook, not on the card, and which
        // of them it has depends on how many times you have been tempted. So RingTemptsYou takes no
        // EmblemSpec: the engine supplies the emblem, and a card file never spells it out.
        var properties = typeof(RingTemptsYou).GetProperties()
            .Where(p => p.DeclaringType == typeof(RingTemptsYou))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["Who"], properties);
    }

    [Fact]
    public void CreateEmblem_is_registered_and_round_trips_the_abilities_a_card_prints()
    {
        var registered = CardFileSerializer.RegisteredNames(typeof(Effect));
        Assert.True(registered.ContainsKey(typeof(CreateEmblem)), "CreateEmblem has no [JsonDerivedType] on Effect.");

        // CR 114.2: the emblem a planeswalker's own text spells out is card data, and this is where
        // it goes — unlike the Ring's, which is not.
        Effect effect = new CreateEmblem(
            new EmblemSpec("Emblem", [new StaticAbility(new GrantKeyword(KeywordKind.Flying), To: new Self())]));

        var json = JsonSerializer.Serialize(effect, Options);
        Assert.Contains("\"CreateEmblem\"", json, StringComparison.Ordinal);

        var back = Assert.IsType<CreateEmblem>(JsonSerializer.Deserialize<Effect>(json, Options));
        Assert.Equal("Emblem", back.Emblem.Name);
        Assert.Single(back.Emblem.Abilities);
        Assert.Equal(json, JsonSerializer.Serialize<Effect>(back, Options));
    }
}
