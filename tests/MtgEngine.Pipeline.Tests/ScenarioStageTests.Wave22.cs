using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 22's generator roads: a land whose own arrival returns a land its controller owns, which
/// used to return itself, and the half of the reveal-then-choose family that says when there is no
/// scenario to write. The reveal road itself is played through the engine in
/// <see cref="ScenarioStageTestsRemembered"/>, beside the rest of the remembered family. Every
/// definition here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave22
{
    /// <summary>
    /// CR 603.3 with CR 400.7: the trigger's choice is made as it resolves and a generated scenario
    /// answers with the first legal option, so the board has to stand a land beside the one being
    /// played. Without it the only land its controller has is the card under test, which is
    /// returned to hand — and the play then reports the card failing to be on the battlefield.
    /// </summary>
    [Fact]
    [RuleRef("603.3")]
    public void A_land_that_returns_a_land_you_control_returns_the_one_standing_beside_it()
    {
        var file = File(
            "wave22 commons",
            new TriggeredAbility(
                new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
                new Move(new Choose(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), new You()), Zone.Hand)),
            CardType.Land);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var stood = Assert.Single(scenario!.Setup.Players[0].Battlefield!);
        Assert.Equal("stood-1", stood.Alias);
        Assert.Equal(ScenarioFiller.Land, stood.Name);

        // The card played stays where it was played, and the land that was standing there is the
        // one in hand — which is the whole of what the trigger says.
        Assert.Equal(Zone.Battlefield, Assert.Single(scenario.Expect.OfType<InZone>(), z => z.Card == file.Name).Zone);
        Assert.Equal(Zone.Hand, Assert.Single(scenario.Expect.OfType<InZone>(), z => z.Card == "stood-1").Zone);
        Assert.Equal(SmokeScenario.HandSize + 1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), c => c.Zone == Zone.Hand).Count);
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 109.2: the board stands a body only where the filter says it will be looked for. A choice
    /// among what an opponent controls is answered off their battlefield, and standing one on the
    /// caster's would assert a bounce of a card the trigger could never have taken.
    /// </summary>
    [Fact]
    [RuleRef("109.2")]
    public void A_land_that_returns_a_land_an_opponent_controls_stands_nothing_beside_it()
    {
        var file = File(
            "wave22 borrowed commons",
            new TriggeredAbility(
                new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
                new Move(new Choose(new ObjectFilter(Types: CardType.Land, ControlledBy: new EachOpponent()), new You()), Zone.Hand)),
            CardType.Land);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Empty(scenario!.Setup.Players[0].Battlefield!);
        Assert.DoesNotContain(scenario.Expect, e => e is InZone { Zone: Zone.Hand });
    }

    /// <summary>
    /// The generator's rule (P16): a choice among revealed cards whose filter names something the
    /// runner has no card for is a choice the board cannot stock, so the card gets no scenario at
    /// all rather than one that reveals two lands and asserts nothing about either of them.
    /// </summary>
    [Fact]
    [RuleRef("701.15a")]
    public void A_choice_among_revealed_cards_the_board_cannot_stock_gets_no_scenario()
    {
        // R3 (wave 26) gave the board a filler per colour, so a one-colour body is stockable now; a
        // two-colour body still is not (seam-requests/R3.md § 2), and that is what this test guards.
        var file = Spell(
            "wave22 coloured wave",
            new SpellAbility(new Sequence(
            [
                new Reveal(new Top(new You(), new X()), RememberAs: "k1"),
                new Move(new Choose(new ObjectFilter(Colors: Color.Blue | Color.Green), new You(), UpTo: true, Key: "k2", Among: new Remembered("k1")), Zone.Battlefield),
            ])),
            CardType.Sorcery,
            "{X}{U}");

        Assert.Null(SmokeScenario.Of(file));
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageWave22Definitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile File(string name, Ability ability, CardType types) => Build(name, ability, types, null);

    private static CardFile Spell(string name, Ability ability, CardType types, string manaCost) => Build(name, ability, types, manaCost);

    private static CardFile Build(string name, Ability ability, CardType types, string? manaCost) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageWave22Definitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
