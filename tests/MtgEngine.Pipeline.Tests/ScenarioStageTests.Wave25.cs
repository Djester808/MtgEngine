using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 25's generator roads: the two wave-24 failures (a continuous effect over a set the fillers
/// cannot fill, and an arrival trigger whose edict ate the card that had just arrived), damage that
/// is lethal to what it was aimed at, the greatest toughness among creatures, a halved library and a
/// halved life total, and an "up to any number" choice over a hand that holds none of them. Every
/// definition here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave25
{
    /// <summary>
    /// CR 611.2c: a continuous effect over "each white creature you control" is never created when
    /// no white creature is there, and the fillers have no white one — so the card gets no scenario
    /// instead of one asserting an event the game will not log. The wave-24 failure.
    /// </summary>
    [Fact]
    [RuleRef("611.2c")]
    public void A_continuous_effect_over_a_colour_the_fillers_have_not_got_gets_no_scenario()
    {
        var file = Spell(
            "wave25 pale ward",
            new SpellAbility(new Apply(
                new GrantKeyword(KeywordKind.Flying),
                new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You(), Colors: Color.White)),
                new UntilEndOfTurn())),
            CardType.Instant,
            "{W}");

        Assert.Null(SmokeScenario.Of(file));
    }

    /// <summary>
    /// CR 611.1: the same reading stands the body the filter actually describes, so an effect over
    /// "each creature you control" still gets its scenario and its event.
    /// </summary>
    [Fact]
    [RuleRef("611.1")]
    public void A_continuous_effect_over_a_body_the_fillers_have_stands_one()
    {
        var file = Spell(
            "wave25 plain ward",
            new SpellAbility(new Apply(
                new GrantKeyword(KeywordKind.Flying),
                new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())),
                new UntilEndOfTurn())),
            CardType.Instant,
            "{W}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is EventLogged { EventKind: nameof(ContinuousEffectCreated) });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 701.16 with CR 603.3: "when this enters, each player sacrifices a nontoken creature of
    /// their choice" is answered with the first legal option, so the caster's battlefield needs a
    /// body older than the card under test — without one the trigger ate the creature that had just
    /// arrived and the play reported the graveyard the scenario itself had built. The wave-24
    /// failure. Only the caster's half is asserted: see seam-requests/P24.md § 1.
    /// </summary>
    [Fact]
    [RuleRef("701.16")]
    public void An_arrival_edict_stands_a_body_older_than_the_card_it_is_on()
    {
        var file = Spell(
            "wave25 grave herald",
            new TriggeredAbility(
                new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
                new Sacrifice(Choice: new ObjectFilter(Types: CardType.Creature, Token: false), Who: new EachPlayer()),
                Where: Zone.Battlefield),
            CardType.Creature,
            "{1}{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is InZone { Zone: Zone.Battlefield, Card: "wave25 grave herald" });
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Caster });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 704.5g: three damage marked on a two-toughness body is lethal, so the play says the
    /// creature it was aimed at is in its owner's graveyard rather than only that damage was dealt.
    /// </summary>
    [Fact]
    [RuleRef("704.5g")]
    public void Lethal_damage_to_a_creature_puts_it_in_its_owners_graveyard()
    {
        var file = Spell(
            "wave25 searing cut",
            new SpellAbility(new DealDamage(new Fixed(3), new Target(new ObjectFilter(Types: CardType.Creature)))),
            CardType.Instant,
            "{1}{R}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Opponent });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 704.5g: damage that *equals* the printed toughness is lethal too, not only damage that
    /// exceeds it, so two on a two-toughness body promises the same graveyard three does. Damage the
    /// toughness survives promises none, and the card is read by the arms after this one.
    /// </summary>
    [Fact]
    [RuleRef("704.5g")]
    public void Damage_equal_to_the_printed_toughness_is_lethal_and_less_is_not()
    {
        var lethal = Spell(
            "wave25 charring",
            new SpellAbility(new DealDamage(new Fixed(SmokeFillerToughness), new Target(new ObjectFilter(Types: CardType.Creature)))),
            CardType.Instant,
            "{R}");
        var survived = Spell(
            "wave25 singe",
            new SpellAbility(new DealDamage(new Fixed(SmokeFillerToughness - 1), new Target(new ObjectFilter(Types: CardType.Creature)))),
            CardType.Instant,
            "{R}");

        var scenario = SmokeScenario.Of(lethal);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Opponent });
        Passes(scenario, lethal);
        Assert.Null(SmokeScenario.Of(survived));
    }

    /// <summary>
    /// CR 208.2 with CR 121.1: "draw cards equal to the greatest toughness among creatures you
    /// control" reads the printed toughness the way the power road reads its power.
    /// </summary>
    [Fact]
    [RuleRef("208.2")]
    public void A_draw_for_the_greatest_toughness_reads_the_printed_one()
    {
        var file = Spell(
            "wave25 long muster",
            new SpellAbility(new Draw(new GreatestAmong(
                new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()),
                new ToughnessOf(new Self())))),
            CardType.Sorcery,
            "{4}{G}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(
            SmokeScenario.HandSize + SmokeFillerToughness,
            Assert.Single(scenario!.Expect.OfType<ZoneCount>(), c => c.Player == SmokeScenario.Caster && c.Zone == Zone.Hand).Count);
        Passes(scenario, file);
    }

    /// <summary>The filler creature the board stands is a 2/2, so the greatest toughness among them is two.</summary>
    private const int SmokeFillerToughness = 2;

    /// <summary>
    /// CR 121.1 with CR 119.4: "target player draws cards equal to half the number of cards in their
    /// library and loses half their life, rounded up each time" — both halves are numbers the board
    /// itself stocked, so the play names the hand, the library and the life.
    /// </summary>
    [Fact]
    [RuleRef("119.4")]
    public void A_halved_library_and_a_halved_life_are_numbers_the_board_knows()
    {
        var aimed = new TargetPlayer(new PlayerFilter(), Key: "k1");
        var file = Spell(
            "wave25 deep gaze",
            new SpellAbility(new Sequence(
            [
                new Draw(new MtgEngine.Cards.Half(new CardsInZoneOf(new RememberedPlayer("k1"), Zone.Library), RoundUp: true), Who: aimed),
                new LoseLife(new MtgEngine.Cards.Half(new LifeOf(new RememberedPlayer("k1")), RoundUp: true), Who: new RememberedPlayer("k1")),
            ])),
            CardType.Sorcery,
            "{6}{U}{U}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(
            SmokeScenario.HandSize + (SmokeScenario.LibrarySize / 2),
            Assert.Single(scenario!.Expect.OfType<ZoneCount>(), c => c.Player == SmokeScenario.Opponent && c.Zone == Zone.Hand).Count);
        Assert.Contains(scenario.Expect, e => e is Life { Player: SmokeScenario.Opponent, Value: 10 });
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 700.1: "put any number of creature cards from your hand onto the battlefield" over a hand
    /// the board filled with lands puts none, so the rest of the card still gets its scenario.
    /// </summary>
    [Fact]
    [RuleRef("700.1")]
    public void An_up_to_choice_over_a_hand_that_holds_none_of_them_asserts_nothing()
    {
        var file = Spell(
            "wave25 last muster",
            new SpellAbility(new Sequence(
            [
                new Draw(new Fixed(2)),
                new Move(
                    new Choose(new ObjectFilter(In: Zone.Hand, Types: CardType.Creature, OwnedBy: new You()), new You(), UpTo: true),
                    Zone.Battlefield),
            ])),
            CardType.Sorcery,
            "{5}{G}{G}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(
            SmokeScenario.HandSize + 2,
            Assert.Single(scenario!.Expect.OfType<ZoneCount>(), c => c.Player == SmokeScenario.Caster && c.Zone == Zone.Hand).Count);
        Passes(scenario, file);
    }

    /// <summary>
    /// CR 111.1: every filler is a card and never a token, so "nontoken creature" is answered by
    /// one. Before wave 25 a token clause of either sense was a field the reading refused outright,
    /// which is why the arrival edict's "sacrifice a nontoken creature" had nothing to stand. The
    /// other sense still describes a permanent the setup has no way to put on a battlefield.
    /// </summary>
    [Fact]
    [RuleRef("111.1")]
    public void A_nontoken_filter_is_answered_by_a_filler_and_a_token_one_by_none()
    {
        var nontoken = Spell(
            "wave25 culling",
            new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, Token: false)))),
            CardType.Instant,
            "{1}{B}");
        var token = Spell(
            "wave25 token cull",
            new SpellAbility(new Destroy(new Target(new ObjectFilter(Types: CardType.Creature, Token: true)))),
            CardType.Instant,
            "{1}{B}");

        var scenario = SmokeScenario.Of(nontoken);

        Assert.NotNull(scenario);
        Assert.Equal(ScenarioFiller.Creature, Assert.Single(scenario!.Setup.Players[1].Battlefield!).Name);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Opponent });
        Passes(scenario, nontoken);
        Assert.Null(SmokeScenario.Of(token));
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageWave25Definitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile Spell(string name, Ability ability, CardType types, string manaCost) => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [
                types == CardType.Creature
                    ? new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Power: new PrintedStat("2"), Toughness: new PrintedStat("1"), Abilities: [ability])
                    : new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability]),
            ]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageWave25Definitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
