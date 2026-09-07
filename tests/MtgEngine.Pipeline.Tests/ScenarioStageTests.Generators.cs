using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The shapes the smoke generator used to have nothing to say about, so their cards were counted
/// as "without a scenario" and never verified: board wipes, each-player sacrifices, modal spells,
/// ramp that searches onto the battlefield, rituals, continuous effects with no zone change,
/// counted draws, and Auras, which landed attached to nothing and were buried by CR 704.5m.
/// Every case reads a made-up definition, so nothing here names a real card.
/// </summary>
public sealed class ScenarioStageTestsGenerators
{
    [Fact]
    [RuleRef("701.7")]
    public void A_board_wipe_expects_both_battlefields_empty()
    {
        var scenario = Generate("wipe", new Destroy(new Each(ObjectFilter.Creature)), CardType.Sorcery);

        var empty = scenario.Expect.OfType<ZoneCount>().Where(z => z.Zone == Zone.Battlefield).ToList();
        Assert.Equal(2, empty.Count);
        Assert.All(empty, z => Assert.Equal(0, z.Count));
        // Both players' creatures went to their own graveyards; the spell is in the caster's too.
        Assert.Contains(scenario.Setup.Players[0].Battlefield!, c => c.Name == ScenarioFiller.Creature);
        Assert.Contains(scenario.Setup.Players[1].Battlefield!, c => c.Name == ScenarioFiller.Creature);
        Assert.Equal(2, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Graveyard && z.Player == SmokeScenario.Opponent).Count);
    }

    [Fact]
    [RuleRef("701.16")]
    public void Each_player_sacrificing_leaves_one_permanent_each_in_a_graveyard()
    {
        var scenario = Generate(
            "each sacrifices",
            new Sacrifice(Choice: ObjectFilter.Creature, N: new Fixed(1), Who: new EachPlayer()),
            CardType.Sorcery);

        Assert.Single(scenario.Setup.Players[1].Battlefield!);
        Assert.All(scenario.Expect.OfType<ZoneCount>().Where(z => z.Zone == Zone.Battlefield), z => Assert.Equal(0, z.Count));
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_modal_spell_takes_the_first_mode_and_asserts_what_it_does()
    {
        var scenario = Generate(
            "choose one",
            new ChooseMode([new Mode("Draw a card.", new Draw(new Fixed(1))), new Mode("You gain 3 life.", new GainLife(new Fixed(3)))]),
            CardType.Instant);

        var cast = Assert.IsType<CastStep>(scenario.Steps[0]);
        Assert.Equal([0], cast.Modes);
        Assert.Equal(SmokeScenario.HandSize + 1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Hand).Count);
        // Only the first mode is asserted: the second one never happens.
        Assert.DoesNotContain(scenario.Expect, e => e is Life);
    }

    [Fact]
    [RuleRef("701.23")]
    public void Ramp_leaves_the_library_one_smaller_and_the_battlefield_one_larger()
    {
        var scenario = Generate("ramp", new Search(new ObjectFilter(Types: CardType.Land), Zone.Battlefield), CardType.Sorcery);

        Assert.Equal(SmokeScenario.LibrarySize - 1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Library).Count);
        Assert.Equal(1, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Battlefield).Count);
    }

    [Fact]
    [RuleRef("106.4")]
    public void A_ritual_asserts_the_mana_it_left_in_the_pool()
    {
        var scenario = Generate("ritual", new AddMana(new ManaProduction("{R}{R}{R}")), CardType.Instant);

        // The pool is stocked at exactly the printed cost, so the cast empties it and what is left
        // is the card's own production — the event alone is logged just as loudly by a ritual that
        // produced the wrong colour.
        Assert.Equal("1", scenario.Setup.Players[0].ManaPool);
        Assert.Equal("{R}{R}{R}", Assert.Single(scenario.Expect.OfType<ManaPoolIs>()).Mana);
        Assert.Empty(scenario.Expect.OfType<EventLogged>());
    }

    [Fact]
    [RuleRef("106.4")]
    public void A_ritual_whose_cost_is_a_choice_keeps_the_event_and_the_deep_pool()
    {
        // A hybrid symbol is a choice, and which half of it the engine spends is not a generated
        // scenario's to predict, so the pool cannot be stocked exactly and a total means nothing.
        var scenario = Generate("hybrid ritual", new AddMana(new ManaProduction("{R}{R}{R}")), CardType.Instant, manaCost: "{B/R}");

        Assert.Equal(nameof(ManaChanged), Assert.Single(scenario.Expect.OfType<EventLogged>()).EventKind);
        Assert.Empty(scenario.Expect.OfType<ManaPoolIs>());
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_generated_scenario_asks_for_its_own_questions_to_be_answered()
    {
        // The opt-in is the scenario's and not the runner's, so a card file's hand-written
        // scenario keeps its own answers beside a generated one that has never seen the objects.
        Assert.True(Generate("draw", new Draw(new Fixed(1)), CardType.Sorcery).Setup.AutoAnswer);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_printed_sacrifice_cost_places_a_body_and_the_step_names_it()
    {
        var scenario = Generate("rites", new Draw(new Fixed(2)), CardType.Instant, costs: [new SacrificeCost(ObjectFilter.Creature)]);

        var eaten = Assert.Single(Assert.IsType<CastStep>(scenario.Steps[0]).CostObjects!);
        var body = Assert.Single(scenario.Setup.Players[0].Battlefield!, c => c.Name == ScenarioFiller.Creature);
        // Under an alias of its own: a filler creature another generator placed may be the
        // opponent's, and the step has to name one particular body (CR 601.2c).
        Assert.Equal(body.Alias, eaten);
        // The sacrificed body joins the spell in the caster's graveyard.
        Assert.Equal(2, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Graveyard).Count);
    }

    [Fact]
    [RuleRef("701.8")]
    public void A_printed_discard_cost_names_the_one_hand_card_the_setup_aliased()
    {
        var scenario = Generate("thirst", new Draw(new Fixed(1)), CardType.Instant, costs: [new DiscardCost()]);

        var discarded = Assert.Single(Assert.IsType<CastStep>(scenario.Steps[0]).CostObjects!);
        Assert.Contains(scenario.Setup.Players[0].Hand!, c => c.Alias == discarded);
        Assert.Equal(2, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Graveyard).Count);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_sacrifice_cost_the_board_cannot_place_a_body_for_gets_no_scenario()
    {
        // The board has no card to sacrifice a land, and the engine would refuse the cast for want
        // of one (CR 601.2h) — a failure that would read as the card's rather than the board's.
        var file = File("land rites", new SpellAbility(new Draw(new Fixed(1))), CardType.Instant, costs: [new SacrificeCost(new ObjectFilter(Types: CardType.Land))]);

        Assert.Null(SmokeScenario.Of(file));
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_printed_cost_that_names_no_object_is_left_to_the_engine()
    {
        // Paying life needs nothing placed and nothing named, so the scenario is what it always was.
        var scenario = Generate("deluge", new Draw(new Fixed(1)), CardType.Sorcery, costs: [new PayLife(new Fixed(3))]);

        Assert.Null(Assert.IsType<CastStep>(scenario.Steps[0]).CostObjects);
    }

    [Fact]
    [RuleRef("611.1")]
    public void A_continuous_effect_with_no_zone_change_asserts_the_effect_the_game_created()
    {
        var scenario = Generate(
            "hexproof until end of turn",
            new Apply(new GrantKeyword(KeywordKind.Hexproof), new Each(ObjectFilter.Creature), new UntilEndOfTurn()),
            CardType.Instant);

        Assert.Equal(nameof(MtgEngine.Rules.Interpreter.ContinuousEffectCreated), Assert.Single(scenario.Expect.OfType<EventLogged>()).EventKind);
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_counted_draw_draws_as_many_as_the_board_gives_it()
    {
        var scenario = Generate(
            "draw for each creature",
            new Draw(new CountOf(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()))),
            CardType.Sorcery);

        Assert.Equal(2, scenario.Setup.Players[0].Battlefield!.Count);
        Assert.Equal(SmokeScenario.HandSize + 2, Assert.Single(scenario.Expect.OfType<ZoneCount>(), z => z.Zone == Zone.Hand).Count);
    }

    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_is_cast_at_a_creature_its_enchant_filter_describes()
    {
        var scenario = Generate("enchant creature", new KeywordAbility(KeywordKind.Enchant, Of: ObjectFilter.Creature), CardType.Enchantment);

        var cast = Assert.IsType<CastStep>(scenario.Steps[0]);
        Assert.Equal([ScenarioFiller.Creature], cast.Targets);
        Assert.Contains(scenario.Setup.Players[0].Battlefield!, c => c.Name == ScenarioFiller.Creature);
        // The attachment is the assertion and not the zone: CR 303.4f puts the Aura onto the
        // battlefield attached to what it was cast at, and CR 704.5m buries it when it is not.
        Assert.Equal(ScenarioFiller.Creature, Assert.Single(scenario.Expect.OfType<AttachedToIs>()).To);
        Assert.DoesNotContain(scenario.Expect, e => e is InZone { Zone: Zone.Battlefield });
    }

    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_that_enchants_a_land_is_cast_at_a_land()
    {
        var scenario = Generate("enchant land", new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Land)), CardType.Enchantment);

        Assert.Equal([ScenarioFiller.Land], Assert.IsType<CastStep>(scenario.Steps[0]).Targets);
    }

    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_whose_enchant_filter_the_board_cannot_place_gets_no_scenario()
    {
        // "Enchant player" has no object to place at all, and guessing one would invent a card.
        var file = File("enchant player", new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Planeswalker)), CardType.Enchantment);

        Assert.Null(SmokeScenario.Of(file));
    }

    private static Scenario Generate(string name, Effect effect, CardType types, IReadOnlyList<Cost>? costs = null, string manaCost = "{1}") =>
        Generate(name, new SpellAbility(effect), types, costs, manaCost);

    private static Scenario Generate(string name, Ability ability, CardType types, IReadOnlyList<Cost>? costs = null, string manaCost = "{1}")
    {
        var scenario = SmokeScenario.Of(File(name, ability, types, costs, manaCost));
        Assert.NotNull(scenario);
        return scenario!;
    }

    private static CardFile File(string name, Ability ability, CardType types, IReadOnlyList<Cost>? costs = null, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability], AdditionalCosts: costs)]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));
}
