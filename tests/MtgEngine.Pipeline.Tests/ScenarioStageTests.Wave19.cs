using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 19's generator roads: a source that moves itself off the battlefield is asserted by the zone
/// it went to and never by the tap its activation paid (CR 110.6), a card put from a graveyard on top
/// of a library, and a counter the controller cannot pay to stop. Two families are declined here on
/// purpose, each with the engine reading that has to change first (seam-requests/P20.md). Every
/// definition is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave19
{
    /// <summary>
    /// CR 110.6: a card in a library is neither tapped nor untapped, so an activation that taps its
    /// source and then puts that source on top of its owner's library has no tap to assert — the
    /// zone is what the play shows. Sensei's Divining Top failed on the tap before this road.
    /// </summary>
    [Fact]
    [RuleRef("110.6")]
    public void An_activation_that_puts_its_own_source_into_the_library_expects_the_zone_and_not_a_tap()
    {
        var file = Artifact(
            "wave19 spinning top",
            new ActivatedAbility([new TapSelf()], new Sequence([new Draw(new Fixed(1)), new Move(new Self(), Zone.Library, Placement.Top)])));

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.Empty(scenario.Expect.OfType<TappedIs>());
        Assert.Equal(Zone.Library, Assert.Single(scenario.Expect.OfType<InZone>(), z => z.Card == file.Name).Zone);
        // It really left: the battlefield the setup put it on is empty afterwards (CR 400.7).
        Assert.Equal(0, Assert.Single(scenario.Expect.OfType<ZoneCount>(), c => c.Zone == Zone.Battlefield).Count);
        Passes(scenario, file);
    }

    /// <summary>An activation that taps and keeps its source still asserts the tap (CR 701.21a).</summary>
    [Fact]
    [RuleRef("701.21a")]
    public void An_activation_that_keeps_its_source_still_expects_it_tapped()
    {
        var file = Artifact("wave19 steady top", new ActivatedAbility([new TapSelf()], new Draw(new Fixed(1))));

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.True(Assert.Single(scenario.Expect.OfType<TappedIs>()).Tapped);
    }

    /// <summary>
    /// CR 110.4c: a permanent card in a graveyard is a card with a permanent card type, so a land
    /// card there answers "permanent" and the board could stock one. The engine's query reads the
    /// field as being on the battlefield instead (CR 110.1), so the spell has no legal target and a
    /// staged scenario would report the engine's reading as the card failing. Drop this test with
    /// the seam (seam-requests/P20.md § 1).
    /// </summary>
    [Fact]
    [RuleRef("110.4c")]
    public void A_permanent_card_in_a_graveyard_gets_no_scenario_until_the_query_reads_a_permanent_card()
    {
        var file = Spell(
            "wave19 reclaim it",
            new SpellAbility(new Move(
                new Target(new ObjectFilter(In: Zone.Graveyard, OwnedBy: new You(), Permanent: true, ManaValue: new Comparison(ComparisonOp.Le, new Fixed(3)))),
                Zone.Battlefield)),
            CardType.Sorcery);

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 306.5b: a planeswalker enters with loyalty counters equal to its printed loyalty. The
    /// engine puts none on it, so CR 704.5i buries it as it arrives and the battlefield every
    /// permanent's scenario asserts is right about the card and wrong about the play. Drop this test
    /// with the seam (seam-requests/P20.md § 2).
    /// </summary>
    [Fact]
    [RuleRef("306.5b")]
    public void A_planeswalker_gets_no_scenario_until_it_enters_with_its_loyalty()
    {
        var file = new CardFile(
            "oracle-wave19-walker",
            "wave19 walker",
            OracleHashing.Of(["wave19 walker"]),
            IrVersion.Current,
            new CardDefinition("oracle-wave19-walker", "wave19 walker", Layout.Normal,
                [new Face("wave19 walker", FaceRole.Main, new TypeLine(CardType.Planeswalker), ManaCost: "{3}{W}{W}", Loyalty: "5")]),
            ImplementationStatus.Translated,
            new TemplateProvenance("test"));

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 701.19: "put target card from a graveyard on top of its owner's library" — the graveyard is
    /// stocked with a card the filter admits and afterwards that card is in the library. The
    /// placement is not asserted: a library's order is not a zone a scenario can read.
    /// </summary>
    [Fact]
    [RuleRef("701.19")]
    public void A_card_put_from_a_graveyard_on_top_of_a_library_is_expected_in_the_library()
    {
        var file = Spell(
            "wave19 revive it",
            new SpellAbility(new Move(new Target(new ObjectFilter(In: Zone.Graveyard)), Zone.Library, Placement.Top)),
            CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var buried = Assert.Single(scenario!.Setup.Players[0].Graveyard!);
        Assert.Equal([buried.Alias!], Assert.IsType<CastStep>(scenario.Steps[0]).Targets);
        Assert.Equal(Zone.Library, Assert.Single(scenario.Expect.OfType<InZone>(), z => z.Card == buried.Alias).Zone);
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 107.4f: a Phyrexian symbol may be paid with two life instead of the mana, and the engine's
    /// casting system refuses such a cost outright, so the cast the scenario would stage never
    /// happens. Drop this test with the seam (seam-requests/P20.md § 3).
    /// </summary>
    [Fact]
    [RuleRef("107.4f")]
    public void A_phyrexian_cost_gets_no_scenario_until_the_casting_system_can_pay_one()
    {
        var file = Spell(
            "wave19 phyrexian revive",
            new SpellAbility(new Move(new Target(new ObjectFilter(In: Zone.Graveyard)), Zone.Library, Placement.Top)),
            CardType.Instant,
            "{G/P}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 118.6: "counter target spell unless its controller pays {1}". The opponent's pool holds
    /// exactly what their own spell cost, so it is empty when they are asked and the spell is
    /// countered into their graveyard (CR 701.5).
    /// </summary>
    [Fact]
    [RuleRef("118.6")]
    public void A_counter_unless_its_controller_pays_expects_the_spell_countered_when_they_cannot_pay()
    {
        var file = Spell(
            "wave19 tithe it",
            new SpellAbility(new Unless(
                new Counter(new Target(new ObjectFilter(Spell: true), Key: "k1")),
                new ManaCost("{1}"),
                new ControllerOf(new Remembered("k1")))),
            CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        // The opponent cast the filler spell out of a pool that held only its cost (CR 602.2a).
        Assert.Equal("1", scenario!.Setup.Players[1].ManaPool);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Opponent });
        Passes(scenario, file);
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageWave19Definitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile Artifact(string name, Ability ability) => Spell(name, ability, CardType.Artifact);

    private static CardFile Spell(string name, Ability ability, CardType types, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageWave19Definitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
