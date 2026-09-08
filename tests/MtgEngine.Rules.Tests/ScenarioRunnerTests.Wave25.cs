using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 605.3: a permanent with more than one mana ability is tapped for the one the scenario names.
/// The wave-19 step took the first, which is the only one a one-ability land has and the wrong one
/// for a permanent that prints two, or whose second mana ability carries the rider the card is about
/// (seam-requests/P20.md § 4, P21.md § 3, P23.md § 4). The number counts mana abilities alone, so an
/// activated ability that is not one does not move it, and a step written without a number still
/// means the first.
/// </summary>
public sealed class ScenarioRunnerTestsWave25
{
    private const string TwoAbilities = "wave25-two-mana-abilities";
    private const string RiderSecond = "wave25-mana-ability-with-rider";
    private const string Interleaved = "wave25-mana-abilities-interleaved";
    private const string OneAbility = "wave25-one-mana-ability";

    /// <summary>
    /// The old spelling: no number, so the first mana ability, which is what every scenario written
    /// before the parameter existed says. The permanent has two, and this asserts which one ran.
    /// </summary>
    [Fact]
    [RuleRef("605.3")]
    public void A_step_without_a_number_taps_for_the_first_mana_ability()
    {
        var result = Run(new Scenario(
            "wave25-tap-default-index",
            "a permanent with two mana abilities, tapped without naming one",
            new GeneratedSource("test"),
            Board([new CardRef(TwoAbilities)]),
            [new TapForManaStep("A", TwoAbilities)],
            [new ManaPoolIs("A", "W"), new TappedIs(TwoAbilities, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// The point of the parameter: the second mana ability is the one the scenario means, and the
    /// mana in the pool afterwards is the only way to tell which one the runner activated.
    /// </summary>
    [Fact]
    [RuleRef("605.3")]
    public void The_named_mana_ability_is_the_one_activated()
    {
        var result = Run(new Scenario(
            "wave25-tap-second-ability",
            "a permanent with two mana abilities, tapped for the second",
            new GeneratedSource("test"),
            Board([new CardRef(TwoAbilities)]),
            [new TapForManaStep("A", TwoAbilities, AbilityIndex: 1)],
            [new ManaPoolIs("A", "U"), new TappedIs(TwoAbilities, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// CR 605.1a: an ability that could add mana is a mana ability whatever else it does, so a
    /// second one that also drains its controller is still tappable — and the rider is what the
    /// scenario is checking, which the first ability would never have shown.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void A_mana_ability_with_a_rider_runs_its_rider_when_it_is_the_one_named()
    {
        var result = Run(new Scenario(
            "wave25-tap-rider-ability",
            "a mana ability with a rider, named by the scenario",
            new GeneratedSource("test"),
            Board([new CardRef(RiderSecond)]),
            [new TapForManaStep("A", RiderSecond, AbilityIndex: 1), new ResolveAllStep()],
            [new ManaPoolIs("A", "B"), new Life("A", StartingLife - 1)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));

        var first = Run(new Scenario(
            "wave25-tap-rider-not-named",
            "the same permanent tapped for its riderless first ability",
            new GeneratedSource("test"),
            Board([new CardRef(RiderSecond)]),
            [new TapForManaStep("A", RiderSecond, AbilityIndex: 0), new ResolveAllStep()],
            [new ManaPoolIs("A", "C"), new Life("A", StartingLife)]));

        Assert.Null(first.Error);
        Assert.True(first.Passed, Explain(first));
    }

    /// <summary>
    /// The number counts mana abilities, not abilities: the permanent here prints a mana ability, a
    /// sacrifice ability that adds nothing, and a second mana ability, and number one is the second
    /// mana ability rather than the sacrifice. A generator reading a face can count what CR 605.1a
    /// calls a mana ability without also modelling the engine's ability list.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void The_number_skips_an_ability_that_is_not_a_mana_ability()
    {
        var result = Run(new Scenario(
            "wave25-tap-skips-non-mana",
            "a non-mana ability between two mana abilities does not move the number",
            new GeneratedSource("test"),
            Board([new CardRef(Interleaved)]),
            [new TapForManaStep("A", Interleaved, AbilityIndex: 1)],
            [new ManaPoolIs("A", "R"), new TappedIs(Interleaved, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// A scenario that names a mana ability the permanent does not have has written a step the game
    /// cannot take. The runner says how many there are with the rule, rather than falling back to
    /// the first and passing on an ability the scenario never meant.
    /// </summary>
    [Fact]
    [RuleRef("605.3")]
    public void A_number_past_the_last_mana_ability_is_refused_with_the_rule()
    {
        var result = Run(new Scenario(
            "wave25-tap-index-out-of-range",
            "a permanent with one mana ability, tapped for its second",
            new GeneratedSource("test"),
            Board([new CardRef(OneAbility)]),
            [new TapForManaStep("A", OneAbility, AbilityIndex: 1)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.3", result.Error!, StringComparison.Ordinal);
        Assert.Contains("1 mana abilities", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>Mana abilities are counted from zero, so a negative number names none of them.</summary>
    [Fact]
    [RuleRef("605.3")]
    public void A_negative_number_is_refused_with_the_rule()
    {
        var result = Run(new Scenario(
            "wave25-tap-negative-index",
            "a permanent tapped for mana ability minus one",
            new GeneratedSource("test"),
            Board([new CardRef(OneAbility)]),
            [new TapForManaStep("A", OneAbility, AbilityIndex: -1)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.3", result.Error!, StringComparison.Ordinal);
        Assert.Contains("counted from zero", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A permanent with no mana ability keeps the wave-20 message, which names CR 605.1a and not the
    /// range: nothing was miscounted, the step is one the permanent could never take.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void A_permanent_with_no_mana_ability_still_says_so()
    {
        var result = Run(new Scenario(
            "wave25-tap-no-mana-ability",
            "a vanilla creature named with a number",
            new GeneratedSource("test"),
            Board([new CardRef(ScenarioFiller.Creature)]),
            [new TapForManaStep("A", ScenarioFiller.Creature, AbilityIndex: 1)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.1a", result.Error!, StringComparison.Ordinal);
        Assert.Contains("no mana ability", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>CR 103.3: the life every scenario player starts with.</summary>
    private const int StartingLife = 20;

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(
            Subsystems.Wave2(),
            new ScenarioRunnerWave25Definitions(TwoAbilitiesCard(), RiderCard(), InterleavedCard(), OneAbilityCard()))).Run(scenario);

    private static ScenarioSetup Board(IReadOnlyList<CardRef> battlefield) =>
        new([new PlayerSetup("A", Battlefield: battlefield), new PlayerSetup("B")], "A");

    private static string Explain(ScenarioResult result) =>
        string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    /// <summary>Two mana abilities of different colours, so the pool says which one ran.</summary>
    private static CardDefinition TwoAbilitiesCard() =>
        new(TwoAbilities, TwoAbilities, Layout.Normal,
            [new Face(TwoAbilities, FaceRole.Main, new TypeLine(CardType.Land),
                Abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{W}"))),
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{U}"))),
                ])]);

    /// <summary>A riderless mana ability first, and a second that also costs its controller a life.</summary>
    private static CardDefinition RiderCard() =>
        new(RiderSecond, RiderSecond, Layout.Normal,
            [new Face(RiderSecond, FaceRole.Main, new TypeLine(CardType.Land),
                Abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{C}"))),
                    new ActivatedAbility([new TapSelf()], new Sequence(
                        [new AddMana(new ManaProduction("{B}")), new LoseLife(new Fixed(1))])),
                ])]);

    /// <summary>A mana ability, an ability that is not one, then a second mana ability.</summary>
    private static CardDefinition InterleavedCard() =>
        new(Interleaved, Interleaved, Layout.Normal,
            [new Face(Interleaved, FaceRole.Main, new TypeLine(CardType.Land),
                Abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{G}"))),
                    new ActivatedAbility([new TapSelf()], new GainLife(new Fixed(1))),
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{R}"))),
                ])]);

    /// <summary>One mana ability, so a scenario naming a second has named one that is not there.</summary>
    private static CardDefinition OneAbilityCard() =>
        new(OneAbility, OneAbility, Layout.Normal,
            [new Face(OneAbility, FaceRole.Main, new TypeLine(CardType.Land),
                Abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{G}")))])]);

    private sealed class ScenarioRunnerWave25Definitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
