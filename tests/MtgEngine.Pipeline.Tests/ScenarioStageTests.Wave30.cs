using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 30's generator road: a creature whose own arrival asks <em>every</em> player to sacrifice
/// is given the bodies each player will take, even when the record wraps the sacrifice in a
/// <see cref="ForEachPlayer"/> and an <see cref="If"/> rather than naming
/// <see cref="EachPlayer"/> on the sacrifice itself. Every definition here is invented; nothing
/// reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave30
{
    private const string Crafter = "wave30 crafter";

    /// <summary>
    /// CR 701.21 with CR 603.3: "when this enters, each player sacrifices a creature or
    /// planeswalker of their choice. Each player who can't discards a card" is a
    /// <see cref="ForEachPlayer"/> over an <see cref="If"/>, and the flat each-player arm cannot
    /// see it. Over an empty board the caster's only creature is the one that just arrived, so the
    /// trigger eats the card under test and the arrival the scenario asserts is false. The bodies
    /// are stood by the setup on both battlefields — older than the card (CR 400.7) — so the
    /// choice takes them and the play shows the edict instead of the board.
    /// </summary>
    [Fact]
    [RuleRef("701.21")]
    public void An_each_player_sacrifice_inside_a_conditional_stands_the_bodies_it_will_take()
    {
        var scenario = SmokeScenario.Of(Creature(Crafter, Edict(new ObjectFilter(AnyTypes: CardType.Creature | CardType.Planeswalker))));

        Assert.NotNull(scenario);
        var mine = Assert.Single(scenario.Setup.Players[0].Battlefield ?? []);
        Assert.Equal(ScenarioFiller.Creature, mine.Name);
        var theirs = Assert.Single(scenario.Setup.Players[1].Battlefield ?? []);
        Assert.Equal(ScenarioFiller.Creature, theirs.Name);

        // The stood body is named in the caster's graveyard, and the card under test is standing.
        Assert.Contains(new InZone(mine.Alias!, Zone.Graveyard, "A"), scenario.Expect);
        Assert.Contains(new InZone(Crafter, Zone.Battlefield), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 1), scenario.Expect);
    }

    /// <summary>
    /// The falsifier: the bodies are the kind the sacrifice's own filter admits, not "one of
    /// whatever the card is". An each-player edict over artifacts stands artifacts, and if the arm
    /// stood the card's own kind this would read a creature.
    /// </summary>
    [Fact]
    [RuleRef("701.21")]
    public void The_bodies_an_each_player_edict_stands_are_the_kind_its_filter_admits()
    {
        var scenario = SmokeScenario.Of(Creature(Crafter, Edict(new ObjectFilter(Types: CardType.Artifact))));

        Assert.NotNull(scenario);
        var mine = Assert.Single(scenario.Setup.Players[0].Battlefield ?? []);
        Assert.Equal(ScenarioFiller.Artifact, mine.Name);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 1), scenario.Expect);
    }

    /// <summary>
    /// CR 701.21 with CR 109.2: a filter naming who controls the permanent describes a sacrifice
    /// only one player could make, so the board stands nothing rather than a body the other
    /// player's half of the trigger cannot use. The cast still asserts the arrival, as it did
    /// before there was any arm for the trigger — what the arm must not do is half-stage it.
    /// </summary>
    [Fact]
    [RuleRef("701.21")]
    public void An_each_player_edict_whose_filter_names_a_controller_is_not_staged()
    {
        var scenario = SmokeScenario.Of(Creature(Crafter, Edict(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()))));

        Assert.NotNull(scenario);
        Assert.Empty(scenario.Setup.Players[0].Battlefield ?? []);
        Assert.Empty(scenario.Setup.Players[1].Battlefield ?? []);
        Assert.DoesNotContain(scenario.Expect, e => e is ZoneCount { Zone: Zone.Graveyard });
    }

    /// <summary>The trigger Plaguecrafter prints, with the sacrifice's filter left to the caller.</summary>
    private static TriggeredAbility Edict(ObjectFilter offered) => new(
        new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
        new ForEachPlayer(
            new EachPlayer(),
            new If(
                new Controls(new TriggeringPlayer(), offered, new Comparison(ComparisonOp.Ge, new Fixed(1))),
                new Sacrifice(Choice: offered, Who: new TriggeringPlayer()),
                new Discard(Who: new TriggeringPlayer()))));

    private static CardFile Creature(string name, params Ability[] abilities)
    {
        var oracleId = "oracle-" + name.Replace(' ', '-');
        return new CardFile(
            oracleId,
            name,
            OracleHashing.Of([name]),
            IrVersion.Current,
            new CardDefinition(oracleId, name, Layout.Normal, [
                new Face(name, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{2}{B}", Abilities: abilities),
            ]),
            ImplementationStatus.Translated,
            new TemplateProvenance("test"));
    }
}
