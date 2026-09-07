using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The shapes wave 7 left without a scenario: a spell aimed at an artifact or an enchantment, a
/// tutor that searches for a card the filler library had not got, and an activated ability that
/// nothing generated activated. Each case reads a made-up definition and then plays it through the
/// real engine, so a generated scenario that cannot pass is caught here rather than in a verify run.
/// </summary>
public sealed class ScenarioStageTestsFillers
{
    [Fact]
    [RuleRef("701.7")]
    public void Destroy_target_artifact_or_enchantment_puts_one_of_each_on_the_board_and_kills_the_one_it_named()
    {
        var file = File("break it", new SpellAbility(new Destroy(new Target(
            new ObjectFilter(AnyTypes: CardType.Artifact | CardType.Enchantment)))), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var opponent = scenario!.Setup.Players[1].Battlefield!;
        Assert.Contains(opponent, c => c.Name == ScenarioFiller.Artifact);
        Assert.Contains(opponent, c => c.Name == ScenarioFiller.Enchantment);
        Assert.Contains(scenario.Expect, e => e is InZone { Card: "aim-1", Zone: Zone.Graveyard });
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("701.19")]
    public void A_tutor_for_a_basic_land_stocks_one_finds_it_and_sees_the_shuffle()
    {
        var file = File(
            "fetch a basic",
            new SpellAbility(new Search(new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic), Zone.Hand)),
            CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var library = scenario!.Setup.Players[0].Library!;
        var stocked = Assert.Single(library, c => c.Name == ScenarioFiller.BasicLand);
        Assert.Equal(library[0], stocked);
        Assert.NotNull(stocked.Alias);
        // CR 701.19 and 701.20: the card the search found is in hand, and the library was shuffled.
        Assert.Contains(scenario.Expect, e => e is InZone found && found.Card == stocked.Alias && found.Zone == Zone.Hand);
        Assert.Contains(scenario.Expect, e => e is Shuffled { Player: SmokeScenario.Caster });
        Assert.Equal(SmokeScenario.LibrarySize, library.Count);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("602.2")]
    public void An_activated_ability_gets_a_scenario_that_activates_it_from_the_battlefield()
    {
        var file = File(
            "tap for a card",
            new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new Draw(new Fixed(1))),
            CardType.Artifact);

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.Equal(SmokeActivation.IdFor(file.OracleId, 0), scenario.Id);
        // CR 602.2: activated from where it already is, so the card is on the battlefield, not in hand.
        Assert.Contains(scenario.Setup.Players[0].Battlefield!, c => c.Name == file.Name);
        Assert.DoesNotContain(scenario.Setup.Players[0].Hand!, c => c.Name == file.Name);
        var step = Assert.IsType<ActivateStep>(scenario.Steps[0]);
        Assert.Equal(0, step.AbilityIndex);
        Assert.Contains(scenario.Expect, e => e is TappedIs { Tapped: true } tapped && tapped.Card == file.Name);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void An_activation_that_sacrifices_a_creature_names_the_body_the_cost_eats()
    {
        var file = File(
            "eat a body",
            new ActivatedAbility([new SacrificeCost(new ObjectFilter(Types: CardType.Creature))], new GainLife(new Fixed(2))),
            CardType.Enchantment);

        var scenario = Assert.Single(SmokeActivation.Of(file));

        var step = Assert.IsType<ActivateStep>(scenario.Steps[0]);
        Assert.NotNull(step.CostObjects);
        Assert.Single(step.CostObjects!);
        Assert.Contains(scenario.Setup.Players[0].Battlefield!, c => c.Alias == step.CostObjects![0]);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("602.5")]
    public void An_ability_whose_cost_the_board_cannot_pay_gets_no_scenario()
    {
        // CR 601.2h: a counter to remove is a cost the board has no way to arrange, and an ability
        // that would be refused gets no scenario rather than one that reports the generator's gap.
        var file = File(
            "remove a counter",
            new ActivatedAbility([new RemoveCountersCost("charge")], new Draw(new Fixed(1))),
            CardType.Artifact);

        Assert.Empty(SmokeActivation.Of(file));
    }

    [Fact]
    [RuleRef("608.2d")]
    public void An_any_colour_mana_ability_makes_the_colour_choice_on_its_step()
    {
        // Five cards lost their activation scenario this way: the engine refuses an activation that
        // did not bring the colour with it, and a refusal reads as the card being broken. The step
        // carries the colour now (seam-requests/P12.md § 1), so the ability gets its scenario back.
        var any = File("any colour", new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(AnyColor: true))), CardType.Creature);
        var fixedMana = File("green", new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{G}"))), CardType.Land);

        Assert.Equal("W", Assert.IsType<ActivateStep>(Assert.Single(SmokeActivation.Of(any)).Steps[0]).ManaColors);
        Assert.Null(Assert.IsType<ActivateStep>(Assert.Single(SmokeActivation.Of(fixedMana)).Steps[0]).ManaColors);
    }

    /// <summary>The generated scenario has to pass in the real engine, or it is not a scenario, it is a claim.</summary>
    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageFillerDefinitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile File(string name, Ability ability, CardType types) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: "{1}", Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageFillerDefinitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
