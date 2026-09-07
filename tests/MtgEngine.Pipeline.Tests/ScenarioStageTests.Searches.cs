using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The two search shapes wave 8 left without a scenario: a search for more than one card, which
/// used to assert one and stay silent about the rest, and a search whose filter compares a mana
/// value, which the filler library could not answer because every filler had one printed cost.
/// Each reads a made-up definition and then plays it through the real engine.
/// </summary>
public sealed class ScenarioStageTestsSearches
{
    [Fact]
    [RuleRef("701.19")]
    public void A_search_for_up_to_two_stocks_two_and_asserts_where_both_of_them_went()
    {
        var file = SearchFile(
            "two basics",
            new Search(new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic), Zone.Battlefield, UpTo: new Fixed(2)));

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var library = scenario!.Setup.Players[0].Library!;
        var stocked = library.Where(c => c.Name == ScenarioFiller.BasicLand).ToList();
        Assert.Equal(2, stocked.Count);

        // Both zones the search touched: each found card is on the battlefield by its own alias,
        // and the library it came out of is two cards shorter (CR 701.19).
        foreach (var card in stocked)
            Assert.Contains(scenario.Expect, e => e is InZone found && found.Card == card.Alias && found.Zone == Zone.Battlefield);
        Assert.Contains(scenario.Expect, e => e is ZoneCount { Zone: Zone.Library } count && count.Count == SmokeScenario.LibrarySize - 2);
        Assert.Contains(scenario.Expect, e => e is Shuffled { Player: SmokeScenario.Caster });
        SearchPasses(scenario, file);
    }

    [Fact]
    [RuleRef("202.3")]
    public void A_search_bounded_by_the_announced_x_finds_a_filler_on_the_right_side_of_it()
    {
        // "Search your library for a creature card with mana value X or less." X is announced as
        // one (CR 601.2b), so the library has to hold a creature costing one or less; the plain
        // filler creature costs two and would leave the search with nothing to find.
        var file = SearchFile(
            "call it",
            new Search(new ObjectFilter(Types: CardType.Creature, ManaValue: new Comparison(ComparisonOp.Le, new X())), Zone.Battlefield),
            manaCost: "{X}{G}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(SmokeScenario.AnnouncedX, Assert.IsType<CastStep>(scenario!.Steps[0]).X);
        var stocked = Assert.Single(scenario.Setup.Players[0].Library!, c => c.Name == ScenarioFiller.CheapCreature);
        Assert.Contains(scenario.Expect, e => e is InZone found && found.Card == stocked.Alias && found.Zone == Zone.Battlefield);
        SearchPasses(scenario, file);
    }

    [Fact]
    [RuleRef("202.3")]
    public void A_search_bounded_above_the_cheap_filler_finds_the_costly_one()
    {
        var file = SearchFile(
            "call something big",
            new Search(new ObjectFilter(Types: CardType.Creature, ManaValue: new Comparison(ComparisonOp.Ge, new Fixed(5))), Zone.Hand));

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Single(scenario!.Setup.Players[0].Library!, c => c.Name == ScenarioFiller.CostlyCreature);
    }

    [Fact]
    [RuleRef("202.3")]
    public void A_bound_the_scenario_cannot_put_a_number_to_stocks_nothing()
    {
        // No X was announced, so "mana value X or less" is a bound the board cannot read, and
        // guessing one would be inventing the card the search is meant to find.
        Assert.Null(SmokeFillersProbe(new ObjectFilter(Types: CardType.Creature, ManaValue: new Comparison(ComparisonOp.Le, new X()))));
    }

    /// <summary>The filler a search of this filter would be answered with, read through a generated scenario.</summary>
    private static string? SmokeFillersProbe(ObjectFilter filter)
    {
        var file = SearchFile("probe", new Search(filter, Zone.Hand));
        var scenario = SmokeScenario.Of(file);
        return scenario?.Setup.Players[0].Library!.FirstOrDefault(c => c.Alias is not null)?.Name;
    }

    private static void SearchPasses(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageSearchDefinitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile SearchFile(string name, Effect effect, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: manaCost, Abilities: [new SpellAbility(effect)])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageSearchDefinitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
