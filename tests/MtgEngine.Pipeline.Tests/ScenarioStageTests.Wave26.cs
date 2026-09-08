using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 26's generator roads: the keyed retarget, now that a zoneless spell filter reads the stack
/// (seam-requests/E9t.md), and the counterspell scenarios asserting the countered spell's fate
/// rather than the graveyard a resolving spell reaches too (CR 608.2m). Every definition here is
/// invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave26
{
    /// <summary>
    /// CR 115.7 with CR 608.2h and CR 202.3: the keyed retarget's whole shape — the bolt announced
    /// at its own controller, the card under test moving it, and "you lose life equal to that
    /// spell's mana value" reading the bolt's printed cost. The caster takes the bolt's one damage
    /// and loses the bolt's one mana value, so the life total the scenario asserts is 18: a play
    /// that changed no target would leave it at 19, and one that read no number at 19 as well.
    /// </summary>
    [Fact]
    [RuleRef("115.7")]
    public void A_keyed_retarget_asserts_both_the_moved_damage_and_the_remembered_mana_value()
    {
        var file = Spell(
            "wave26 mischief",
            new SpellAbility(new Sequence(
            [
                new ChangeTarget(new Target(new ObjectFilter(Spell: true), Key: "k1")),
                new LoseLife(new ManaValueOf(new Remembered("k1"))),
            ])),
            CardType.Instant,
            "{1}{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(new Life("A", 18), scenario.Expect);
    }

    /// <summary>
    /// CR 701.5 with CR 608.2m: a counter puts the spell it reached into its owner's graveyard —
    /// and so does the game when that spell simply resolves, which is why the graveyard alone says
    /// nothing. The spell on the stack is the bolt, and the opponent's life total standing at
    /// twenty is what says the counter reached it (CR 119.3).
    /// </summary>
    [Fact]
    [RuleRef("701.5")]
    public void A_counter_asserts_that_the_countered_spell_never_did_anything()
    {
        var file = Spell(
            "wave26 negate",
            new SpellAbility(new Counter(new Target(new ObjectFilter(NotTypes: CardType.Creature, Spell: true)))),
            CardType.Instant,
            "{1}{U}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(new Life("B", 20), scenario.Expect);
        Assert.Contains(scenario.Steps.OfType<CastStep>(), step =>
            string.Equals(step.Player, "B", StringComparison.Ordinal)
            && string.Equals(step.Card, ScenarioFiller.Bolt, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same for "counter unless its controller pays": the opponent's pool holds exactly the
    /// bolt's own cost and is empty when they are asked (CR 118.6), so the counter is what the play
    /// shows — and the life total that never moved is what says so.
    /// </summary>
    [Fact]
    [RuleRef("118.6")]
    public void A_counter_unless_paid_asserts_the_same_untouched_life_total()
    {
        var file = Spell(
            "wave26 muddle",
            new SpellAbility(new Unless(
                new Counter(new Target(new ObjectFilter(AnyTypes: CardType.Instant | CardType.Sorcery, Spell: true), Key: "k1")),
                new ManaCost("{1}"),
                new ControllerOf(new Remembered("k1")))),
            CardType.Instant,
            "{U}{U}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(new Life("B", 20), scenario.Expect);

        // CR 602.2a: the pool holds exactly the bolt's own cost, so there is nothing left to pay with.
        Assert.Equal("R", scenario.Setup.Players[1].ManaPool);
    }

    /// <summary>
    /// The generator's rule: a counter whose filter names a spell whose resolution the board cannot
    /// see — a sorcery, and the only filler sorcery does nothing at all — gets no scenario, rather
    /// than one asserting a graveyard that a spell which resolved would be in too (CR 608.2m). This
    /// is the half E9t warned about: before wave 26 the sorcery filler answered this filter and the
    /// card was badged Verified by a check that could not fail.
    /// </summary>
    [Fact]
    [RuleRef("608.2m")]
    public void A_counter_of_a_spell_whose_resolution_shows_nothing_gets_no_scenario()
    {
        var file = Spell(
            "wave26 sorcery hate",
            new SpellAbility(new Counter(new Target(new ObjectFilter(Types: CardType.Sorcery, Spell: true)))),
            CardType.Instant,
            "{1}{U}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 119.4: a counterspell that also drains asserts one life total for the opponent and not
    /// two — the bolt's damage that never happened and the card's own two life are the same number.
    /// </summary>
    [Fact]
    [RuleRef("119.4")]
    public void A_counter_that_also_drains_asserts_one_life_total()
    {
        var file = Spell(
            "wave26 draining counter",
            new SpellAbility(new Sequence(
            [
                new Counter(new Target(new ObjectFilter(NotTypes: CardType.Creature, Spell: true))),
                new LoseLife(new Fixed(2), new EachOpponent()),
            ])),
            CardType.Instant,
            "{1}{U}{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal([new Life("B", 18)], [.. scenario.Expect.OfType<Life>().Where(life => string.Equals(life.Player, "B", StringComparison.Ordinal))]);
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
}
