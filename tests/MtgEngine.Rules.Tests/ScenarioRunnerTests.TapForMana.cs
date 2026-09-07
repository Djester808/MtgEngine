using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 605.3a with CR 106.4: a land is tapped for mana by activating its mana ability, which
/// resolves as it is activated, taps the land and puts the mana in a pool. <c>AddManaStep</c> only
/// fills the pool — nothing was tapped, so a "whenever enchanted land is tapped for mana" trigger
/// never fired and no scenario could assert one (seam-requests/P20.md § 4). These tests are about
/// <see cref="TapForManaStep"/> being the real activation and not a second way to fill a pool.
/// </summary>
public sealed class ScenarioRunnerTestsTapForMana
{
    private const string Forest = "runner-tap-forest";
    private const string Sprawl = "runner-tap-aura";
    private const string Prism = "runner-tap-any-colour";

    [Fact]
    [RuleRef("106.4")]
    public void A_land_tapped_for_mana_taps_and_its_colour_is_in_the_pool()
    {
        var result = Run(new Scenario(
            "runner-tap-for-mana",
            "a land tapped for mana",
            new GeneratedSource("test"),
            Board([new CardRef(Forest)]),
            [new TapForManaStep("A", Forest)],
            [new ManaPoolIs("A", "G"), new TappedIs(Forest, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// The pool an <c>AddManaStep</c> fills is the same pool, which is exactly why the two steps are
    /// not interchangeable: this one also taps the permanent, and that is what a trigger watches.
    /// </summary>
    [Fact]
    [RuleRef("605.3a")]
    public void The_step_taps_the_land_where_adding_mana_leaves_it_untapped()
    {
        var added = Run(new Scenario(
            "runner-add-mana-taps-nothing",
            "mana added by the scenario taps nothing",
            new GeneratedSource("test"),
            Board([new CardRef(Forest)]),
            [new AddManaStep("A", "G")],
            [new ManaPoolIs("A", "G"), new TappedIs(Forest, false)]));

        Assert.Null(added.Error);
        Assert.True(added.Passed, Explain(added));
    }

    /// <summary>
    /// CR 605.1a: the runner finds the ability, so a permanent that has none has been asked for a
    /// step the game cannot take. It says so with the rule rather than activating whatever ability
    /// happened to be first, which would report some other refusal — or worse, quietly pass.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void A_permanent_with_no_mana_ability_is_refused_with_the_rule()
    {
        var result = Run(new Scenario(
            "runner-tap-no-mana-ability",
            "a vanilla creature cannot be tapped for mana",
            new GeneratedSource("test"),
            Board([new CardRef(ScenarioFiller.Creature)]),
            [new TapForManaStep("A", ScenarioFiller.Creature)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.1a", result.Error!, StringComparison.Ordinal);
        Assert.Contains("no mana ability", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CR 608.2d: the colour of "add one mana of any color" is announced with the activation, and
    /// this step carries it exactly as <c>ActivateStep</c> does — the runner never has to know which
    /// index the ability sits at.
    /// </summary>
    [Fact]
    [RuleRef("608.2d")]
    public void The_colour_on_the_step_is_the_colour_that_reaches_the_pool()
    {
        var result = Run(new Scenario(
            "runner-tap-for-any-colour",
            "a source tapped for one mana of a chosen colour",
            new GeneratedSource("test"),
            Board([new CardRef(Prism)]),
            [new TapForManaStep("A", Prism, ManaColors: "U")],
            [new ManaPoolIs("A", "U"), new TappedIs(Prism, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// The point of the step, played through <see cref="Subsystems.Wave2"/>: an Aura on the land
    /// whose "whenever enchanted land is tapped for mana" trigger gains its controller a life. The
    /// trigger fires only because something was tapped for the mana (CR 106.12a), so the life total
    /// is the assertion that <c>AddManaStep</c> could never have made.
    /// </summary>
    [Fact]
    [RuleRef("106.12a")]
    public void An_Auras_tapped_for_mana_trigger_fires_on_the_step()
    {
        var result = Run(new Scenario(
            "runner-tap-for-mana-trigger",
            "an Aura's tapped-for-mana trigger fires",
            new GeneratedSource("test"),
            Board([new CardRef(Forest), new CardRef(Sprawl, AttachedTo: Forest)]),
            [new TapForManaStep("A", Forest), new ResolveAllStep()],
            [new Life("A", ScenarioRunnerTapForManaLife), new AttachedToIs(Sprawl, Forest), new TappedIs(Forest, true)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>One life gained on top of the twenty every scenario player starts with, CR 103.3.</summary>
    private const int ScenarioRunnerTapForManaLife = 21;

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioRunnerTapForManaDefinitions(ForestCard(), SprawlCard(), PrismCard()))).Run(scenario);

    private static ScenarioSetup Board(IReadOnlyList<CardRef> battlefield) =>
        new([new PlayerSetup("A", Battlefield: battlefield), new PlayerSetup("B")], "A");

    private static string Explain(ScenarioResult result) =>
        string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    /// <summary>A basic land that taps for one green mana, CR 305.6: the printed ability, not a real card.</summary>
    private static CardDefinition ForestCard() =>
        new(Forest, Forest, Layout.Normal,
            [new Face(Forest, FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic, ["Forest"]),
                Abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{G}")))])]);

    /// <summary>An Aura with no enchant ability (CR 704.5m leaves such an attachment alone) that watches the land it is on.</summary>
    private static CardDefinition SprawlCard() =>
        new(Sprawl, Sprawl, Layout.Normal,
            [new Face(Sprawl, FaceRole.Main, new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]), ManaCost: "{G}",
                Abilities: [new TriggeredAbility(
                    new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You())),
                    new GainLife(new Fixed(1)))])]);

    /// <summary>A permanent that taps for one mana of any colour, so the step's colour has something to answer.</summary>
    private static CardDefinition PrismCard() =>
        new(Prism, Prism, Layout.Normal,
            [new Face(Prism, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(AnyColor: true)))])]);

    private sealed class ScenarioRunnerTapForManaDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
