using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Two things a generated scenario could not stage before this wave. CR 305.6: a permanent with a
/// basic land type has that type's mana ability whether or not it prints one, so a filler Forest is
/// tapped for {G} as the first — and only — mana ability it has, which is what an Aura on a Forest
/// or any land-fetch scenario needs (seam-requests/R2.md § 3). CR 202.2: a filter that names a
/// colour describes a permanent the board can stand, so the four coloured bodies beside
/// <see cref="ScenarioFiller.GreenCreature"/> turn "creatures of a colour you control" from a set
/// that is empty — and so never gets a continuous effect at all, CR 611.2c — into a play
/// (seam-requests/P24.md § 2).
/// </summary>
public sealed class ScenarioRunnerTestsWave26
{
    /// <summary>
    /// CR 305.6: the ability is the land type's, not the printed face's, and the fillers print
    /// nothing at all — so the mana in the pool afterwards is the only evidence the runner read the
    /// object's current abilities and not what the card says.
    /// </summary>
    [Theory]
    [RuleRef("305.6")]
    [InlineData(ScenarioFiller.Plains, "W")]
    [InlineData(ScenarioFiller.Island, "U")]
    [InlineData(ScenarioFiller.Swamp, "B")]
    [InlineData(ScenarioFiller.Mountain, "R")]
    [InlineData(ScenarioFiller.Forest, "G")]
    public void A_basic_land_filler_taps_for_the_mana_its_type_names(string filler, string mana)
    {
        var result = Run(new Scenario(
            "wave26-tap-basic-filler",
            "a filler basic land tapped for the mana its type names",
            new GeneratedSource("test"),
            Board([new CardRef(filler)]),
            [new TapForManaStep("A", filler, AbilityIndex: 0)],
            [new ManaPoolIs("A", mana), new TappedIs(filler, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// The intrinsic ability is the permanent's first mana ability, so the older spelling — a step
    /// that names no number — reaches the same one. A scenario written before the parameter existed
    /// keeps meaning what it meant (CR 605.3).
    /// </summary>
    [Fact]
    [RuleRef("605.3")]
    public void A_basic_land_filler_without_a_number_taps_for_the_same_ability()
    {
        var result = Run(new Scenario(
            "wave26-tap-basic-filler-default",
            "a filler Forest tapped without naming an ability",
            new GeneratedSource("test"),
            Board([new CardRef(ScenarioFiller.Forest)]),
            [new TapForManaStep("A", ScenarioFiller.Forest)],
            [new ManaPoolIs("A", "G"), new TappedIs(ScenarioFiller.Forest, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// CR 305.6 gives the ability for the basic land *type*, so the two typeless land fillers still
    /// have none and say so with the rule. Without this the arm could be "every land taps for
    /// something", which is a different rule and the wrong one.
    /// </summary>
    [Theory]
    [RuleRef("305.6")]
    [InlineData(ScenarioFiller.Land)]
    [InlineData(ScenarioFiller.BasicLand)]
    public void A_land_filler_with_no_basic_land_type_still_has_no_mana_ability(string filler)
    {
        var result = Run(new Scenario(
            "wave26-tap-typeless-land",
            "a land filler with no basic land type, tapped for mana",
            new GeneratedSource("test"),
            Board([new CardRef(filler)]),
            [new TapForManaStep("A", filler)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.1a", result.Error!, StringComparison.Ordinal);
        Assert.Contains("no mana ability", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CR 202.2: one body per colour, standing on the battlefield together, so a scenario that has
    /// to show a card treating each colour differently has all five to point at. A name with no
    /// definition behind it is a scenario the runner refuses, so this fails outright without the
    /// four new fillers rather than passing vacuously.
    /// </summary>
    [Fact]
    [RuleRef("202.2")]
    public void A_filler_creature_of_every_colour_can_stand_on_the_battlefield()
    {
        var result = Run(new Scenario(
            "wave26-coloured-fillers",
            "one filler creature of each colour on the battlefield",
            new GeneratedSource("test"),
            Board([.. Coloured.Select(colour => new CardRef(colour.Filler))]),
            [],
            [.. Coloured.Select(colour => (Expectation)new InZone(colour.Filler, Zone.Battlefield)),
             new ZoneCount("A", Zone.Battlefield, Coloured.Count)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// CR 202.2/202.2b: a generator reads a filler's colour off <see cref="ScenarioFiller.ColorOf"/>
    /// rather than parsing the cost, so each coloured body has to answer with the colour its name
    /// says — and be a 1/1 for one mana, the shape <see cref="ScenarioFiller.GreenCreature"/> set.
    /// </summary>
    [Fact]
    [RuleRef("202.2b")]
    public void Every_coloured_filler_answers_with_the_colour_its_cost_prints()
    {
        foreach (var (filler, color) in Coloured)
        {
            Assert.Equal(color, ScenarioFiller.ColorOf(filler));
            Assert.Equal(1, ScenarioFiller.ManaValueOf(filler));
            Assert.Equal(CardType.Creature, ScenarioFiller.TypesOf(filler)?.Types);
            Assert.Contains(filler, ScenarioFiller.Names);
        }

        Assert.Equal(Coloured.Select(c => c.Color).Distinct().Count(), Coloured.Count);
    }

    /// <summary>The five coloured bodies, CR 105.1: one filler for each colour of Magic.</summary>
    [RuleRef("105.1")]
    private static IReadOnlyList<(string Filler, Color Color)> Coloured =>
    [
        (ScenarioFiller.WhiteCreature, Color.White),
        (ScenarioFiller.BlueCreature, Color.Blue),
        (ScenarioFiller.BlackCreature, Color.Black),
        (ScenarioFiller.RedCreature, Color.Red),
        (ScenarioFiller.GreenCreature, Color.Green),
    ];

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioRunnerWave26Definitions())).Run(scenario);

    private static ScenarioSetup Board(IReadOnlyList<CardRef> battlefield) =>
        new([new PlayerSetup("A", Battlefield: battlefield), new PlayerSetup("B")], "A");

    private static string Explain(ScenarioResult result) =>
        string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    /// <summary>Every card these scenarios name is a filler, so the store behind them holds nothing.</summary>
    private sealed class ScenarioRunnerWave26Definitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;

        public CardDefinition? ByOracleId(string oracleId) => null;
    }
}
