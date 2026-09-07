using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The furniture a generated scenario builds a board out of. A scenario that needs an artifact to
/// destroy, an enchantment to point at or a basic land to find must not have to name a real card
/// to get one — a failure in the filler would then be read as a failure of whatever was under test.
/// Nothing here is a card name: every definition is invented in <see cref="ScenarioFiller"/>.
/// </summary>
public sealed class ScenarioRunnerTestsFillers
{
    [Theory]
    [InlineData(ScenarioFiller.Artifact, CardType.Artifact)]
    [InlineData(ScenarioFiller.Enchantment, CardType.Enchantment)]
    [InlineData(ScenarioFiller.Creature, CardType.Creature)]
    [InlineData(ScenarioFiller.Sorcery, CardType.Sorcery)]
    [RuleRef("301.1")]
    public void The_runner_places_a_filler_of_each_kind_the_generator_can_ask_for(string card, CardType types)
    {
        var scenario = Board([new CardRef(card)], [new TypesAre(card, types)]);

        var result = Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => r.Actual)));
    }

    [Fact]
    [RuleRef("205.4a")]
    public void The_basic_land_filler_is_basic_and_the_plain_one_is_not()
    {
        // CR 205.4a: a search that says "basic land card" needs a card that is one, and the plain
        // filler land has to stay nonbasic so it still answers a filter that says nonbasic.
        Assert.Equal(Supertype.Basic, ScenarioFiller.TypesOf(ScenarioFiller.BasicLand)!.Supertypes);
        Assert.Equal(Supertype.None, ScenarioFiller.TypesOf(ScenarioFiller.Land)!.Supertypes);
        Assert.Null(ScenarioFiller.TypesOf("a card nobody printed"));
        Assert.All(ScenarioFiller.Names, name => Assert.NotNull(ScenarioFiller.For(name)));
    }

    [Theory]
    [RuleRef("305.6")]
    [InlineData(ScenarioFiller.Plains, "Plains")]
    [InlineData(ScenarioFiller.Island, "Island")]
    [InlineData(ScenarioFiller.Swamp, "Swamp")]
    [InlineData(ScenarioFiller.Mountain, "Mountain")]
    [InlineData(ScenarioFiller.Forest, "Forest")]
    public void A_basic_land_filler_carries_its_one_basic_land_type(string card, string landType)
    {
        // CR 305.6: a basic land is a Basic land with one of the five basic land types, and a search
        // that names a type (CR 205.3i) has to find a filler that carries it and nothing else printed.
        var types = ScenarioFiller.TypesOf(card)!;
        Assert.Equal(CardType.Land, types.Types);
        Assert.Equal(Supertype.Basic, types.Supertypes);
        Assert.Equal([landType], types.Subtypes);
        Assert.Contains(card, ScenarioFiller.Names);
    }

    [Fact]
    [RuleRef("701.7")]
    public void An_artifact_filler_on_the_board_can_be_destroyed_and_lands_in_its_owners_graveyard()
    {
        // The whole point of the artifact filler: "destroy target artifact" has something to be
        // pointed at, and what happens to it is a real CR 701.7 destruction in the real engine.
        var scenario = new Scenario(
            "runner-filler-destroy",
            "the filler artifact is destroyed",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Hand: [new CardRef(Shatter)], ManaPool: "RRR"),
                    new PlayerSetup("B", Battlefield: [new CardRef(ScenarioFiller.Artifact), new CardRef(ScenarioFiller.Enchantment)]),
                ],
                "A"),
            [new CastStep("A", Shatter, [ScenarioFiller.Artifact]), new ResolveAllStep()],
            [
                new InZone(ScenarioFiller.Artifact, Zone.Graveyard, "B"),
                new InZone(ScenarioFiller.Enchantment, Zone.Battlefield, "B"),
                new StackEmpty(),
            ]);

        var result = Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
    }

    private const string Shatter = "runner-filler-artifact-removal";

    private static Scenario Board(IReadOnlyList<CardRef> battlefield, IReadOnlyList<Expectation> expect) => new(
        "runner-filler-board",
        "the filler stands on the board",
        new GeneratedSource("test"),
        new ScenarioSetup([new PlayerSetup("A", Battlefield: battlefield), new PlayerSetup("B")], "A"),
        [],
        expect);

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioRunnerFillerDefinitions())).Run(scenario);

    private sealed class ScenarioRunnerFillerDefinitions : IDefinitionStore
    {
        private static CardDefinition Removal { get; } = new(
            Shatter,
            Shatter,
            Layout.Normal,
            [
                new Face(Shatter, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}{R}",
                    Abilities: [new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Artifact))))]),
            ]);

        public CardDefinition? ByName(string name) => name == Shatter ? Removal : null;

        public CardDefinition? ByOracleId(string oracleId) => oracleId == Shatter ? Removal : null;
    }
}
