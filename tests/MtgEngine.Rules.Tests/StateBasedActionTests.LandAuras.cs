using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 704.5m for an Aura that enchants a land, played through the front door: a real game on
/// <see cref="Subsystems.Wave2"/>, driven by the scenario runner, so the casting system, the
/// query layer and the state-based actions all answer for themselves. The unit boards in
/// <c>StateBasedActionTests.Auras.cs</c> ask 704.5m with a fake query; these ask it with the
/// real one, which is where a land filter that the rule reads correctly can still lose its Aura.
/// </summary>
public sealed partial class StateBasedActionTests
{
    /// <summary>An Aura whose enchant ability names lands: "enchant land", CR 702.5b.</summary>
    private const string LandAura = "Sba Land Binding";

    /// <summary>The other half of the pair: an Aura that may only be on a creature.</summary>
    private const string CreatureAura = "Sba Creature Binding";

    /// <summary>
    /// CR 303.4f with 704.5m: an Aura that enchants a land, cast at a land, is attached to that
    /// land and stays there through the settle. This is the whole of E5g's card in miniature —
    /// the enchant filter is <c>{types:[Land]}</c> and the host is a plain land — and it passes,
    /// which is what says the rule and the query read a land filter correctly.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_land_stays_on_the_land_it_was_cast_at()
    {
        var result = LandAuraRun(new Scenario(
            "sba-land-aura-cast",
            "an Aura that enchants a land is cast at one",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Hand: [new CardRef(LandAura)], Battlefield: [new CardRef(ScenarioFiller.Land)], ManaPool: "GG"),
                    new PlayerSetup("B"),
                ],
                "A"),
            [new CastStep("A", LandAura, [ScenarioFiller.Land]), new ResolveAllStep()],
            [new AttachedToIs(LandAura, ScenarioFiller.Land), new InZone(LandAura, Zone.Battlefield), new StackEmpty()]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Failures(result));
    }

    /// <summary>
    /// CR 704.5m through the front door with no casting in the way: the Aura is already on the
    /// battlefield attached to the land (CR 301.5, what the runner's <c>attachedTo</c> places),
    /// and a round of priority — which performs 704.3's check — leaves it there.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_land_survives_the_settle_while_attached_to_one()
    {
        var result = LandAuraRun(Placed(LandAura, [new AttachedToIs(LandAura, ScenarioFiller.Land), new InZone(LandAura, Zone.Battlefield)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Failures(result));
    }

    /// <summary>
    /// The other direction, and the half that must keep refusing: an Aura whose enchant ability
    /// names a creature is not legally attached to a land, so CR 704.5m puts it into its owner's
    /// graveyard on the first check. A land filter that was read as "anything" would leave it.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_creature_is_buried_while_attached_to_a_land()
    {
        var result = LandAuraRun(Placed(CreatureAura, [new InZone(CreatureAura, Zone.Graveyard)]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Failures(result));
    }

    /// <summary>
    /// The shape the smoke generator writes, and the gap E5g found: the same cast, but with more
    /// cards of the host's name in hand. The runner names a placed card by its own name and the
    /// first placement wins (<c>ScenarioRun.Name</c>), and the hand is placed before the
    /// battlefield, so <c>"Scenario Filler Land"</c> in a step means the copy in <em>hand</em>.
    /// The Aura is then announced at a card that is not on the battlefield and CR 608.2b counters
    /// it as it resolves, which is the graveyard the card file's verify reported.
    /// <para>
    /// Two things are wrong and neither is in this folder, so both are asserted as they are today
    /// and named in <c>seam-requests/E5g.md</c>: the runner's naming (the target ought to be the
    /// battlefield copy) and CR 601.2c (an Aura announced at an object its enchant ability does
    /// not admit ought to be refused at announcement, as the player half already is in
    /// <c>CastingSystem.RequireLegalAuraTarget</c>). Flip this test when either lands.
    /// </para>
    /// </summary>
    [Fact]
    [RuleRef("608.2b")]
    public void An_aura_cast_at_a_name_the_hand_also_holds_is_countered_rather_than_attached()
    {
        var result = LandAuraRun(new Scenario(
            "sba-land-aura-smoke-shape",
            "the smoke scenario's board: filler lands in hand as well as on the battlefield",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup(
                        "A",
                        Hand: [new CardRef(LandAura), .. Enumerable.Repeat(new CardRef(ScenarioFiller.Land), 3)],
                        Battlefield: [new CardRef(ScenarioFiller.Land)],
                        ManaPool: "GG"),
                    new PlayerSetup("B"),
                ],
                "A"),
            [new CastStep("A", LandAura, [ScenarioFiller.Land]), new ResolveAllStep()],
            [new InZone(LandAura, Zone.Graveyard), new StackEmpty()]));

        // The gap: the cast is allowed at a card in hand and the Aura never enters the
        // battlefield. Asserted as it is today so the seam request has a witness.
        Assert.Null(result.Error);
        Assert.True(result.Passed, Failures(result));
    }

    /// <summary>An Aura already attached to a land on the battlefield, and one round of priority to settle it.</summary>
    private static Scenario Placed(string aura, IReadOnlyList<Expectation> expect) => new(
        $"sba-placed-{aura}",
        "an Aura placed attached to a land",
        new GeneratedSource("test"),
        new ScenarioSetup(
            [
                new PlayerSetup("A", Battlefield: [new CardRef(ScenarioFiller.Land), new CardRef(aura, AttachedTo: ScenarioFiller.Land)]),
                new PlayerSetup("B"),
            ],
            "A"),
        [new PassStep("A")],
        expect);

    private static ScenarioResult LandAuraRun(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new LandAuraDefinitions())).Run(scenario);

    private static string Failures(ScenarioResult result) =>
        string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    /// <summary>The two made-up Auras this file plays with; every other card it needs is filler.</summary>
    private sealed class LandAuraDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => name switch
        {
            LandAura => Aura(LandAura, new ObjectFilter(Types: CardType.Land)),
            CreatureAura => Aura(CreatureAura, new ObjectFilter(Types: CardType.Creature)),
            _ => null,
        };

        public CardDefinition? ByOracleId(string oracleId) => ByName(oracleId);

        private static CardDefinition Aura(string name, ObjectFilter enchants) => new(
            name,
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    new TypeLine(CardType.Enchantment, Supertype.None, ["Aura"]),
                    ManaCost: "{G}",
                    Abilities: [new KeywordAbility(KeywordKind.Enchant, Of: enchants)]),
            ]);
    }
}
