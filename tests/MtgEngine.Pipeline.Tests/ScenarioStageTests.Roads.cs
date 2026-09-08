using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The shapes wave 15 found without a scenario: damage to "any target", a card returned from the
/// graveyard, a fight, an amount that is the announced X, a draw for the greatest power among
/// creatures, and a search whose filter names a subtype. Each reads a made-up definition, and the
/// ones that can be played are played through the real engine, so a generated scenario that cannot
/// pass is caught here rather than in a verify run.
/// </summary>
public sealed class ScenarioStageTestsRoads
{
    [Fact]
    [RuleRef("115.4")]
    public void Any_target_damage_aims_at_the_opponent_and_asserts_their_life()
    {
        var file = File("bolt anything", new SpellAbility(new DealDamage(new Fixed(3), To: new AnyTarget())), CardType.Instant, "{R}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        // CR 601.2c: the player is announced with the spell, and the runner reads the name as a player.
        Assert.Equal([SmokeScenario.Opponent], Assert.IsType<CastStep>(scenario!.Steps[0]).Targets);
        Assert.Equal(17, Assert.Single(scenario.Expect.OfType<Life>()).Value);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("404.1")]
    public void Returning_a_card_from_the_graveyard_buries_one_names_it_and_expects_it_in_hand()
    {
        var file = File(
            "dig it up",
            new SpellAbility(new Move(new Target(new ObjectFilter(In: Zone.Graveyard, OwnedBy: new You())), Zone.Hand)),
            CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var buried = Assert.Single(scenario!.Setup.Players[0].Graveyard!);
        Assert.NotNull(buried.Alias);
        Assert.Equal([buried.Alias], Assert.IsType<CastStep>(scenario.Steps[0]).Targets);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Hand } back && back.Card == buried.Alias);
        // The hand grew by the card it got back; the graveyard is left holding the spell alone (CR 608.2m).
        Assert.Equal(SmokeScenario.HandSize + 1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Hand).Count);
        Assert.Equal(1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Graveyard).Count);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("701.14")]
    public void A_fight_names_both_creatures_and_expects_the_smaller_one_dead()
    {
        var file = File("brawl", new SpellAbility(Brawl(keyed: true)), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        // Two targets on one step, in the order the record names them (CR 601.2c).
        var targets = Assert.IsType<CastStep>(scenario!.Steps[0]).Targets!;
        Assert.Equal(2, targets.Count);
        var mine = Assert.Single(scenario.Setup.Players[0].Battlefield!);
        var theirs = Assert.Single(scenario.Setup.Players[1].Battlefield!);
        Assert.Equal([mine.Alias!, theirs.Alias!], targets);
        // A 6/6 against a 2/2: exactly one of them has lethal damage marked (CR 704.5g).
        Assert.Equal(ScenarioFiller.CostlyCreature, mine.Name);
        Assert.Equal(ScenarioFiller.Creature, theirs.Name);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard, Owner: SmokeScenario.Opponent } dead && dead.Card == theirs.Alias);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Battlefield } alive && alive.Card == mine.Alias);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_fight_between_unkeyed_targets_plays_each_creature_from_its_own_slot()
    {
        // CR 601.2c with 608.2b: an unkeyed target reads the slot it announced (E9n, wave 17), so
        // the pair needs no keys and the scenario plays the way the keyed one does.
        var file = File("unkeyed brawl", new SpellAbility(Brawl(keyed: false)), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Passes(scenario, file);
    }

    /// <summary>"Target creature you control fights target creature you don't control", with or without keys on the pair.</summary>
    private static Fight Brawl(bool keyed) => new(
        new Target(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()), Key: keyed ? "a" : null),
        new Target(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent()), Key: keyed ? "b" : null));

    [Fact]
    [RuleRef("601.2b")]
    public void A_drain_for_X_reads_the_x_the_cast_announced()
    {
        var drain = new SpellAbility(new LoseLife(new X(), new EachOpponent()));

        var scenario = SmokeScenario.Of(File("drain X", drain, CardType.Sorcery, "{X}{B}"));

        Assert.NotNull(scenario);
        Assert.Equal(SmokeScenario.AnnouncedX, Assert.IsType<CastStep>(scenario!.Steps[0]).X);
        Assert.Equal(20 - SmokeScenario.AnnouncedX, Assert.Single(scenario.Expect.OfType<Life>()).Value);
        // With no X in the cost there is no number to read, and nothing is asserted (CR 601.2b).
        Assert.Null(SmokeScenario.Of(File("drain nothing", drain, CardType.Sorcery, "{B}")));
    }

    [Fact]
    [RuleRef("208.1")]
    public void A_draw_for_the_greatest_power_places_one_creature_and_draws_its_power()
    {
        var file = File(
            "read the biggest",
            new SpellAbility(new Draw(new GreatestAmong(
                new ObjectFilter(Types: CardType.Creature, NotSubtypes: ["Human"], ControlledBy: new You()),
                new PowerOf(new Self())))),
            CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var creature = Assert.Single(scenario!.Setup.Players[0].Battlefield!);
        Assert.Equal(ScenarioFiller.Creature, creature.Name);
        var power = ScenarioFiller.For(ScenarioFiller.Creature)!.Faces[0].Power!.Number!.Value;
        Assert.Equal(SmokeScenario.HandSize + power, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Hand).Count);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("205.3")]
    public void A_search_for_a_non_human_creature_card_finds_the_plain_creature()
    {
        // The filler creature has no subtype printed, so it is not a Human (CR 205.3) and the search finds it.
        var file = File(
            "find someone",
            new SpellAbility(new Search(new ObjectFilter(Types: CardType.Creature, NotSubtypes: ["Human"]), Zone.Hand)),
            CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var stocked = Assert.Single(scenario!.Setup.Players[0].Library!, c => c.Alias is not null);
        Assert.Equal(ScenarioFiller.Creature, stocked.Name);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("205.3")]
    public void A_search_for_a_subtype_no_filler_carries_gets_no_scenario()
    {
        // No filler is a Goblin, and a library stocked with something else would leave the search
        // finding nothing. A basic land of one of the basic land types is a filler the runner has
        // yet to gain (seam-requests/P16.md), so a "Forest card" search waits on it the same way.
        var file = File(
            "find a goblin",
            new SpellAbility(new Search(new ObjectFilter(Types: CardType.Creature, Subtypes: ["Goblin"]), Zone.Hand)),
            CardType.Sorcery);

        Assert.Null(SmokeScenario.Of(file));
    }

    [Fact]
    [RuleRef("115.7")]
    public void Changing_the_target_of_a_spell_moves_its_damage_to_the_caster()
    {
        // Withheld until the runner had a filler spell that targets and the engine could offer a
        // player as a new target (E9r): the bolt is announced at the opponent, the card under test
        // retargets it, and the caster's own life total is what says the target moved.
        var file = File("bend it", new SpellAbility(new ChangeTarget(new Target(new ObjectFilter(In: Zone.Stack)))), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Contains(scenario!.Expect, e => e is Life { Player: "A", Value: 19 });
        Passes(scenario, file);
    }

    /// <summary>The generated scenario has to pass in the real engine, or it is not a scenario, it is a claim.</summary>
    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageRoadDefinitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile File(string name, Ability ability, CardType types, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageRoadDefinitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
