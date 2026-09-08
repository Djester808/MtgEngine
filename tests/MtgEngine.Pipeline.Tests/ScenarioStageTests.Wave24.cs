using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 24's generator roads for the card files that had no scenario: a counter whose filter names a
/// kind of spell rather than "a spell", a colour a filter excludes, a retarget that remembers the
/// spell it moved, and a bounce to an owner's hand. Every definition here is invented; nothing reads
/// a real card.
/// </summary>
public sealed class ScenarioStageTestsWave24
{
    /// <summary>
    /// CR 601.2c: "counter target noncreature spell" is read against the filler spells' printed type
    /// lines, so the board can put one on the stack. Before wave 24 the filter had to say nothing at
    /// all but "a spell", and a "non" clause on it got no scenario.
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void A_counter_aimed_at_a_noncreature_spell_puts_one_on_the_stack()
    {
        var file = Spell(
            "wave24 negation",
            new SpellAbility(new Counter(new Target(new ObjectFilter(NotTypes: CardType.Creature, Spell: true)))),
            CardType.Instant,
            "{1}{U}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: "B" });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 601.2c: the same reading refuses a spell the board cannot cast on purpose. A filter naming
    /// a land spell describes nothing the runner has a card for, so the card gets no scenario rather
    /// than one aimed at nothing.
    /// </summary>
    [Fact]
    [RuleRef("601.2c")]
    public void A_counter_aimed_at_a_kind_of_spell_the_board_has_no_card_for_gets_no_scenario()
    {
        var file = Spell(
            "wave24 stone counter",
            new SpellAbility(new Counter(new Target(new ObjectFilter(Types: CardType.Land, Spell: true)))),
            CardType.Instant,
            "{1}{U}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 202.2b: "destroy target nonblack creature" is a colour a filter excludes, and a filler that
    /// prints no colour at all is not black — so the board can stand one and point the spell at it.
    /// </summary>
    [Fact]
    [RuleRef("202.2b")]
    public void A_destroy_aimed_at_a_creature_of_no_particular_colour_places_a_colourless_filler()
    {
        var file = Spell(
            "wave24 snuffing",
            new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, NotColors: Color.Black)), CantBeRegenerated: true)),
            CardType.Instant,
            "{3}{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var body = Assert.Single(scenario!.Setup.Players[1].Battlefield!);
        Assert.Equal(ScenarioFiller.Creature, body.Name);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: "B" });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 202.2b again, the other way: a filler that prints the colour the filter excludes is not
    /// admitted, and a filter that excludes every filler's colour leaves nothing to aim at.
    /// </summary>
    [Fact]
    [RuleRef("202.2b")]
    public void A_destroy_that_excludes_every_body_the_board_has_gets_no_scenario()
    {
        var file = Spell(
            "wave24 nothing left",
            new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, Colors: Color.Green, NotColors: Color.Green)))),
            CardType.Instant,
            "{1}{B}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 115.7 with CR 608.2h: a retarget whose own target carries a key is a play since wave 26.
    /// It was withheld while the same shape changed nothing in the engine (seam-requests/P23.md
    /// § 2); E9t measured that the gap was the query's zoneless spell filter and not the
    /// interpreter's (CR 112.1), and with that filter reading the stack the announcement is
    /// remembered and the keyed "that spell's mana value" reads the bolt's own printed cost.
    /// </summary>
    [Fact]
    [RuleRef("115.7")]
    public void A_retarget_that_keys_its_target_plays()
    {
        var file = Spell(
            "wave24 mischief",
            new SpellAbility(new Sequence(
            [
                new ChangeTarget(new Target(new ObjectFilter(Spell: true), Key: "k1")),
                new LoseLife(new ManaValueOf(new Remembered("k1"))),
            ])),
            CardType.Instant,
            "{1}{B}");

        Assert.NotNull(SmokeScenario.Of(file));
    }

    /// <summary>
    /// The generator's rule (P16): a retarget whose filter names a spell the board cannot put on the
    /// stack — a creature spell, which no filler is — gets no scenario at all, because a change of
    /// target with nothing on the stack to change would assert nothing.
    /// </summary>
    [Fact]
    [RuleRef("115.7")]
    public void A_retarget_aimed_at_a_spell_the_board_cannot_cast_gets_no_scenario()
    {
        var file = Spell(
            "wave24 bent creature",
            new SpellAbility(new ChangeTarget(new Target(new ObjectFilter(Types: CardType.Creature, Spell: true)))),
            CardType.Instant,
            "{1}{R}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 400.7: "return target nonland permanent you don't control to its owner's hand" takes the
    /// body off the opponent's battlefield and puts it in the opponent's hand, not the caster's.
    /// </summary>
    [Fact]
    [RuleRef("400.7")]
    public void A_bounce_puts_the_permanent_in_its_own_owners_hand()
    {
        var file = Spell(
            "wave24 rift",
            new SpellAbility(new Move(
                new Target(new ObjectFilter(NotTypes: CardType.Land, ControlledBy: new EachOpponent(), Permanent: true)),
                Zone.Hand)),
            CardType.Instant,
            "{1}{U}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is InZone { Zone: Zone.Hand, Owner: "B" });
        Assert.Equal(SmokeScenario.HandSize + 1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), c => c.Player == "B" && c.Zone == Zone.Hand).Count);
        Passes(scenario, file);
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageWave24Definitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile Spell(string name, Ability ability, CardType types, string manaCost) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageWave24Definitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
