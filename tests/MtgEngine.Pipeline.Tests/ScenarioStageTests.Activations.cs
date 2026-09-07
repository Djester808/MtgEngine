using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// CR 608.2d: an ability that adds "one mana of any color" asks the activator which colour, and the
/// engine refuses an activation that did not bring the answer. Five cards lost their activation
/// scenario to that (seam-requests/P11.md), because the choice travels with the command and a
/// generated scenario had no field to put it in. It has one now, so the generator chooses a colour
/// and asserts the pool the activation leaves behind. Every definition here is invented.
/// </summary>
public sealed class ScenarioStageTestsActivations
{
    [Fact]
    [RuleRef("608.2d")]
    public void An_ability_that_adds_one_mana_of_any_colour_chooses_a_colour_and_asserts_the_pool()
    {
        var scenario = Activation("any colour", new AddMana(new ManaProduction(AnyColor: true)));

        Assert.Equal("W", Assert.IsType<ActivateStep>(scenario.Steps[0]).ManaColors);
        Assert.Equal("W", Assert.Single(scenario.Expect.OfType<ManaPoolIs>()).Mana);
        // CR 106.4: the pool it was paid from held exactly the cost, so what is left is the ability's.
        Assert.Equal(string.Empty, scenario.Setup.Players[0].ManaPool);
    }

    /// <summary>One letter per mana added, in the order the effect adds them (seam-requests/P12.md § 1).</summary>
    [Fact]
    [RuleRef("608.2d")]
    public void Two_manas_of_any_colour_are_two_letters_on_the_step()
    {
        var scenario = Activation(
            "two of any colour",
            new Sequence([new AddMana(new ManaProduction(AnyColor: true)), new AddMana(new ManaProduction(AnyColor: true))]));

        Assert.Equal("WW", Assert.IsType<ActivateStep>(scenario.Steps[0]).ManaColors);
        Assert.Equal("WW", Assert.Single(scenario.Expect.OfType<ManaPoolIs>()).Mana);
    }

    /// <summary>A printed colour was never a choice: the step names none, exactly as it did before.</summary>
    [Fact]
    [RuleRef("106.1")]
    public void An_ability_that_adds_printed_mana_still_names_no_colour()
    {
        var scenario = Activation("printed mana", new AddMana(new ManaProduction("{G}")));

        Assert.Null(Assert.IsType<ActivateStep>(scenario.Steps[0]).ManaColors);
        Assert.Empty(scenario.Expect.OfType<ManaPoolIs>());
    }

    /// <summary>
    /// A production the step has no field for — the colour this card chose earlier, its commander's
    /// identity, whatever a land could produce — still gets no scenario at all, rather than one the
    /// engine refuses for a choice nobody made.
    /// </summary>
    [Theory]
    [RuleRef("608.2d")]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void A_production_the_step_cannot_choose_for_gets_no_scenario(bool chosen, bool commander, bool land)
    {
        var file = File(
            "unchoosable mana",
            new ActivatedAbility(
                [new TapSelf()],
                new AddMana(new ManaProduction(
                    ChosenColor: chosen,
                    CommanderIdentity: commander,
                    LandCouldProduce: land))));

        Assert.Empty(SmokeActivation.Of(file));
    }

    /// <summary>
    /// CR 601.2f: the assertion is only honest when the pool the activation was paid from held
    /// exactly the cost. A cost with a symbol that is a choice cannot be stocked that way, so the
    /// ability keeps its colour unasserted rather than asserting a pool the run cannot predict.
    /// </summary>
    [Fact]
    [RuleRef("601.2f")]
    public void An_ability_whose_cost_cannot_be_stocked_exactly_gets_no_scenario()
    {
        var file = File(
            "hybrid cost",
            new ActivatedAbility([new ManaCost("{B/R}"), new TapSelf()], new AddMana(new ManaProduction(AnyColor: true))));

        Assert.Empty(SmokeActivation.Of(file));
    }

    /// <summary>The cost is stocked to the symbol, so the pool afterwards is the ability's own mana.</summary>
    [Fact]
    [RuleRef("602.2a")]
    public void A_paid_activation_stocks_the_pool_with_exactly_what_it_costs()
    {
        var scenario = Activation(
            "paid any colour",
            new AddMana(new ManaProduction(AnyColor: true)),
            [new ManaCost("{2}"), new TapSelf()]);

        Assert.Equal("2", scenario.Setup.Players[0].ManaPool);
        Assert.Equal("W", Assert.Single(scenario.Expect.OfType<ManaPoolIs>()).Mana);
    }

    private static Scenario Activation(string name, Effect effect, IReadOnlyList<Cost>? costs = null)
    {
        var file = File(name, new ActivatedAbility(costs ?? [new TapSelf()], effect));
        var scenario = Assert.Single(SmokeActivation.Of(file));
        return scenario;
    }

    /// <summary>An artifact whose one ability is the activation under test.</summary>
    private static CardFile File(string name, Ability ability) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}", Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
