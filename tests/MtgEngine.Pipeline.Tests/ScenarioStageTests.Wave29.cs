using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 29's generator roads: cards put back from a hand on top of a library are counted out of the
/// hand the draw before them filled (CR 401.1), and a cost that offers a choice between costs stocks
/// what its first option needs, because that is the one the payment takes (CR 601.2b). Every
/// definition here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave29
{
    private const string Eddy = "wave29 eddy";
    private const string Levy = "wave29 levy";

    /// <summary>
    /// CR 401.1 with CR 121.1: "draw three cards, then put two cards from your hand on top of your
    /// library". The hand the generator counts has to lose the two that went back — it was counting
    /// only the draw, which is the <c>ZoneCount was 4</c> the card failed on — and the library has to
    /// gain them.
    /// </summary>
    [Fact]
    [RuleRef("401.1")]
    public void Cards_put_back_on_top_of_the_library_leave_the_hand_they_came_from()
    {
        var scenario = SmokeScenario.Of(Instant(Eddy, Restocking(3, 2)));

        Assert.NotNull(scenario);
        // Three drawn and two put back out of a hand of three filler: four in hand, seven in library.
        Assert.Contains(new ZoneCount("A", Zone.Hand, SmokeScenario.HandSize + 1), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Library, SmokeScenario.LibrarySize - 1), scenario.Expect);

        // Which cards went back is the player's choice (CR 601.2c is the cast's; this is the
        // resolution's) and a library's order is not a zone a scenario can read, so nothing names them.
        Assert.DoesNotContain(scenario.Expect, e => e is InZone zone && zone.Zone == Zone.Library);
    }

    /// <summary>
    /// The falsifier: the same card without the put-back counts the whole draw, which is the
    /// expectation that was failing. If the road stopped reading the <see cref="Move"/> the two
    /// scenarios would be identical.
    /// </summary>
    [Fact]
    [RuleRef("121.1")]
    public void A_draw_with_nothing_put_back_still_counts_the_whole_draw()
    {
        var scenario = SmokeScenario.Of(Instant(Eddy, new SpellAbility(new Draw(new Fixed(3)))));

        Assert.NotNull(scenario);
        Assert.Contains(new ZoneCount("A", Zone.Hand, SmokeScenario.HandSize + 3), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Library, SmokeScenario.LibrarySize - 3), scenario.Expect);
    }

    /// <summary>
    /// The two moves can cancel — draw two, put two back — and the hand is still the number that
    /// says the play happened. A scenario silent about the hand there is the one that passed over
    /// a put-back that never occurred, which is why the board is told the hand holds.
    /// </summary>
    [Fact]
    [RuleRef("401.1")]
    public void A_draw_and_a_put_back_that_cancel_are_still_counted()
    {
        var scenario = SmokeScenario.Of(Instant(Eddy, Restocking(2, 2)));

        Assert.NotNull(scenario);
        Assert.Contains(new ZoneCount("A", Zone.Hand, SmokeScenario.HandSize), scenario.Expect);
    }

    /// <summary>
    /// CR 601.2b: "as an additional cost to cast this spell, sacrifice an artifact or discard a
    /// card". The choice is made at announcement and the casting system pays the first option it
    /// can, in printed order, so the board stands the artifact that option eats. With nothing stood
    /// the cast is refused whole — "None of the costs offered can be paid (601.2h)" — which is what
    /// the card was failing on.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void A_choice_between_costs_stocks_what_its_first_option_needs()
    {
        var scenario = SmokeScenario.Of(Instant(Levy, new SpellAbility(new Draw(new Fixed(2))), Choosing(
            new SacrificeCost(new ObjectFilter(Types: CardType.Artifact)),
            new DiscardCost())));

        Assert.NotNull(scenario);
        var stood = Assert.Single(scenario.Setup.Players[0].Battlefield ?? []);
        Assert.Equal(ScenarioFiller.Artifact, stood.Name);
        Assert.NotNull(stood.Alias);

        // The sacrificed artifact and the spell are the graveyard; the hand only gained the draw,
        // because the discard behind the first option is never reached.
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 2), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Hand, SmokeScenario.HandSize + 2), scenario.Expect);
    }

    /// <summary>
    /// The falsifier: the same two options the other way round stock the hand and stand nothing, so
    /// the road reads the printed order rather than preferring a kind of cost. A generator that
    /// stocked both would leave the play free to take either and the counts could not say which.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void The_option_a_choice_stocks_is_the_first_one_printed()
    {
        var scenario = SmokeScenario.Of(Instant(Levy, new SpellAbility(new Draw(new Fixed(2))), Choosing(
            new DiscardCost(),
            new SacrificeCost(new ObjectFilter(Types: CardType.Artifact)))));

        Assert.NotNull(scenario);
        Assert.Empty(scenario.Setup.Players[0].Battlefield ?? []);
        // One card discarded out of the hand and two drawn into it, and the spell beside it.
        Assert.Contains(new ZoneCount("A", Zone.Hand, SmokeScenario.HandSize + 1), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 2), scenario.Expect);
    }

    /// <summary>
    /// A choice whose first option eats something the board has no card for is refused whole, rather
    /// than answered by an option behind it: the payment would take the first payable one, and a
    /// scenario that assumed otherwise would assert a play the engine never made.
    /// </summary>
    [Fact]
    [RuleRef("601.2h")]
    public void A_choice_whose_first_option_cannot_be_stocked_gets_no_scenario()
    {
        var scenario = SmokeScenario.Of(Instant(Levy, new SpellAbility(new Draw(new Fixed(2))), Choosing(
            new SacrificeCost(new ObjectFilter(Types: CardType.Artifact), Count: new Fixed(3)),
            new DiscardCost())));

        Assert.Null(scenario);
    }

    /// <summary>CR 401.1: draw, then put cards from hand back on top — the shape the road reads.</summary>
    private static SpellAbility Restocking(int drawn, int back) => new(new Sequence(
    [
        new Draw(new Fixed(drawn)),
        new Move(
            new Choose(new ObjectFilter(In: Zone.Hand, OwnedBy: new You()), new You(), new Fixed(back)),
            Zone.Library,
            Placement.Top),
    ]));

    private static IReadOnlyList<Cost> Choosing(params Cost[] options) => [new ChooseCost(options)];

    private static CardFile Instant(string name, SpellAbility ability, IReadOnlyList<Cost>? costs = null)
    {
        var oracleId = "oracle-" + name.Replace(' ', '-');
        return new CardFile(
            oracleId,
            name,
            OracleHashing.Of([name]),
            IrVersion.Current,
            new CardDefinition(
                oracleId,
                name,
                Layout.Normal,
                [new Face(name, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{U}", Abilities: [ability], AdditionalCosts: costs)]),
            ImplementationStatus.Translated,
            new TemplateProvenance("test"));
    }
}
