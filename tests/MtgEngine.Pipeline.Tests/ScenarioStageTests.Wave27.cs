using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 27's generator roads: an activation whose own effect takes its source off the battlefield
/// asserts the zone it went to rather than a tap nothing can answer (CR 400.7), and an
/// <see cref="Unsupported"/> marker is refused per ability rather than per card (CR 602.2). Every
/// definition here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave27
{
    private const string Tool = "wave27 tool";

    /// <summary>
    /// CR 400.7 with CR 110.6: "{2}, {T}: You gain 5 life. Shuffle this and your graveyard into your
    /// library" leaves nothing on the battlefield to be tapped, so the tap is not what the play can
    /// show — the library is. The source names itself as one member of a union rather than on its
    /// own, which is the shape the old reading missed: it looked for a bare <c>Self</c> and asserted
    /// the tap of a card that had already gone.
    /// </summary>
    [Fact]
    [RuleRef("400.7")]
    public void An_activation_that_shuffles_its_source_away_asserts_the_zone_and_not_the_tap()
    {
        var file = Artifact(Tool, new ActivatedAbility(
            [new ManaCost("{2}"), new TapSelf()],
            new Sequence(
            [
                new GainLife(new Fixed(5)),
                new Shuffle(IntoLibrary: new Union([new Self(), new Each(new ObjectFilter(In: Zone.Graveyard, OwnedBy: new You()))])),
            ])));

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.Contains(new InZone(Tool, Zone.Library, "A"), scenario.Expect);
        Assert.Empty(scenario.Expect.OfType<TappedIs>());
        // The life is still asserted, so the scenario says the ability resolved and not only that
        // the card moved: a play that did nothing at all leaves the caster on twenty.
        Assert.Contains(new Life("A", 25), scenario.Expect);
    }

    /// <summary>
    /// CR 701.21: the source that stays where it is still has its tap asserted. Without this the
    /// change above would be a licence to drop the tap from every activation.
    /// </summary>
    [Fact]
    [RuleRef("701.21")]
    public void An_activation_that_leaves_its_source_standing_still_asserts_the_tap()
    {
        var file = Artifact(Tool, new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new GainLife(new Fixed(3))));

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.Contains(new TappedIs(Tool, true), scenario.Expect);
        Assert.DoesNotContain(scenario.Expect, expected => expected is InZone zone && string.Equals(zone.Card, Tool, StringComparison.Ordinal));
    }

    /// <summary>
    /// CR 602.2: two activated abilities are two objects. A marker on the second says the second is
    /// not written down; the first is, and it keeps its scenario. Emergence Zone lost its
    /// <c>{T}: Add {C}</c> to its flash permission for seven waves (seam-requests/P25.md § 4).
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_marker_on_one_activated_ability_leaves_its_neighbour_its_scenario()
    {
        var file = Artifact(
            Tool,
            new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new GainLife(new Fixed(3))),
            new ActivatedAbility([new ManaCost("{2}")], new Unsupported("no record for casting as though it had flash")));

        var scenarios = SmokeActivation.Of(file);

        var scenario = Assert.Single(scenarios);
        Assert.Equal(SmokeActivation.IdFor(file.OracleId, 0), scenario.Id);
    }

    /// <summary>
    /// CR 602.2 the other way round: a marker outside the activated abilities is part of what
    /// casting the card does, so the cast scenario is withheld — an arrival asserted over a
    /// resolution half of which is not expressed is a scenario that cannot fail — while the
    /// activation beside it is a separate object and still plays.
    /// </summary>
    [Fact]
    [RuleRef("602.2")]
    public void A_marker_outside_the_activated_abilities_withholds_the_cast_and_not_the_activation()
    {
        var file = Artifact(
            Tool,
            new ActivatedAbility([new ManaCost("{1}"), new TapSelf()], new GainLife(new Fixed(3))),
            new TriggeredAbility(new EntersBattlefield(ObjectFilter.Any), new Unsupported("no record for this arrival")));

        Assert.Null(SmokeScenario.Of(file));
        Assert.Single(SmokeActivation.Of(file));
    }

    private static CardFile Artifact(string name, params Ability[] abilities) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}", Abilities: abilities)]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
