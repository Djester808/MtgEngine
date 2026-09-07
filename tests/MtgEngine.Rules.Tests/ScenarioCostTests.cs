using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The three things a scenario could not say before: which object a printed cost eats
/// (CR 601.2c), what is in a mana pool (CR 106.4), and what an Aura is attached to (CR 303.4f).
/// The first two cards in the pilot to need them were refused by the engine and asserted the
/// event rather than the position (seam-requests/P9.md §§ 2 and 4).
/// </summary>
public sealed class ScenarioCostTests
{
    private const string Rite = "costs-rite";
    private const string Ritual = "costs-ritual";
    private const string Aura = "costs-aura";
    private const string Bear = "costs-bear";

    [Fact]
    [RuleRef("601.2c")]
    public void A_cost_object_named_by_its_alias_is_sacrificed_and_the_spell_is_cast()
    {
        var scenario = Cast(Rite, new CardRef(Bear, Alias: "body"), ["body"], [
            new InZone("body", Zone.Graveyard),
            new ZoneCount("A", Zone.Hand, 1),
            new StackEmpty(),
        ]);

        var result = Runner().Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, Why(result));
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_cost_object_named_by_its_card_name_is_sacrificed_too()
    {
        // Names and aliases are one table, as they are for targets: a board with one body of a
        // kind need not invent a name for it.
        var scenario = Cast(Rite, new CardRef(Bear), [Bear], [
            new InZone(Bear, Zone.Graveyard),
            new StackEmpty(),
        ]);

        var result = Runner().Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, Why(result));
    }

    [Fact]
    [RuleRef("601.2h")]
    public void Without_a_cost_object_the_engine_refuses_the_cast()
    {
        // The refusal the two sacrifice-cost pilot cards died of, kept as the reason the field
        // exists: the only body on the board is the opponent's, so there is nothing to eat.
        var scenario = Cast(Rite, new CardRef(Bear), null, [new StackEmpty()], mine: false);

        var result = Runner().Run(scenario);

        Assert.NotNull(result.Error);
        Assert.Contains("sacrifice", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [RuleRef("106.4")]
    public void A_mana_pool_expectation_reads_the_pool_as_a_bag_of_symbols()
    {
        // The pool is stocked with exactly the cost, so what is left is what the card produced.
        var scenario = Ritual_scenario([new ManaPoolIs("A", "{B}{B}{B}"), new StackEmpty()]);

        var result = Runner().Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, Why(result));
    }

    [Fact]
    [RuleRef("106.4")]
    public void A_failing_mana_pool_expectation_reports_the_pool_that_is_actually_there()
    {
        var scenario = Ritual_scenario([new ManaPoolIs("A", "{G}")]);

        var result = Runner().Run(scenario);

        Assert.Equal("{B}{B}{B}", Assert.Single(result.Results).Actual);
    }

    [Fact]
    [RuleRef("106.4")]
    public void An_empty_pool_is_the_empty_string_and_says_so()
    {
        var scenario = Ritual_scenario([new ManaPoolIs("B", "")]);

        var result = Runner().Run(scenario);

        var opponent = Assert.Single(result.Results);
        Assert.True(opponent.Passed, opponent.Actual);
        Assert.Equal("an empty pool", opponent.Actual);
    }

    [Fact]
    [RuleRef("301.5")]
    public void An_attached_aura_is_reported_with_what_it_is_attached_to()
    {
        // The setup attaches it (CR 301.5), so this is the arm reading a real attachment rather
        // than a guess about the casting system.
        var scenario = new Scenario(
            "costs-attached",
            "an Aura the setup attached is attached",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Battlefield: [new CardRef(Bear), new CardRef(Aura, AttachedTo: Bear)]),
                    new PlayerSetup("B"),
                ],
                "A"),
            [],
            [new AttachedToIs(Aura, Bear)]);

        var result = Runner().Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, Why(result));
    }

    [Fact]
    [RuleRef("303.4f")]
    public void An_aura_cast_at_a_creature_resolves_attached_to_it()
    {
        // CR 303.4f: an Aura cast at a legal object enters attached to it, and the expectation
        // reads the attachment back from the permanent the resolution made.
        var scenario = new Scenario(
            "costs-attached",
            "an Aura cast at a creature resolves attached to it",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Battlefield: [new CardRef(Bear)], Hand: [new CardRef(Aura)], ManaPool: "W"),
                    new PlayerSetup("B"),
                ],
                "A",
                AutoAnswer: true),
            [new CastStep("A", Aura, [Bear]), new ResolveAllStep()],
            [new AttachedToIs(Aura, Bear)]);

        var result = Runner().Run(scenario);

        Assert.Null(result.Error);
        var attachment = Assert.Single(result.Results);
        Assert.True(attachment.Passed, attachment.Actual);
        Assert.StartsWith("attached to", attachment.Actual, StringComparison.Ordinal);
    }

    private static Scenario Cast(string card, CardRef body, IReadOnlyList<string>? costObjects, Expectation[] expect, bool mine = true) =>
        new(
            "costs-" + card,
            "a printed cost eats the object the step names",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Battlefield: mine ? [body] : [], Hand: [new CardRef(card)], Library: [new CardRef(ScenarioFiller.Land)], ManaPool: "B"),
                    new PlayerSetup("B", Battlefield: mine ? [] : [body]),
                ],
                "A",
                AutoAnswer: true),
            [new CastStep("A", card, CostObjects: costObjects), new ResolveAllStep()],
            expect);

    private static Scenario Ritual_scenario(Expectation[] expect) =>
        new(
            "costs-ritual",
            "the mana a ritual produced is in the pool",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [new PlayerSetup("A", Hand: [new CardRef(Ritual)], ManaPool: "B"), new PlayerSetup("B")],
                "A",
                AutoAnswer: true),
            [new CastStep("A", Ritual), new ResolveAllStep()],
            expect);

    private static string Why(ScenarioResult result) =>
        string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    private static ScenarioRunner Runner() =>
        new(new Game(Subsystems.Wave2(), new ScenarioCostDefinitions(RiteCard(), RitualCard(), AuraCard(), BearCard())));

    private static CardDefinition RiteCard() =>
        new(Rite, Rite, Layout.Normal,
            [new Face(Rite, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{B}",
                Abilities: [new SpellAbility(new Draw(new Fixed(1)))],
                AdditionalCosts: [new SacrificeCost(new ObjectFilter(Types: CardType.Creature))])]);

    private static CardDefinition RitualCard() =>
        new(Ritual, Ritual, Layout.Normal,
            [new Face(Ritual, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{B}",
                Abilities: [new SpellAbility(new AddMana(new ManaProduction("{B}{B}{B}")))])]);

    private static CardDefinition AuraCard() =>
        new(Aura, Aura, Layout.Normal,
            [new Face(Aura, FaceRole.Main, new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]), ManaCost: "{W}",
                Abilities: [new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature))])]);

    private static CardDefinition BearCard() =>
        new(Bear, Bear, Layout.Normal,
            [new Face(Bear, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{1}{G}", Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);

    private sealed class ScenarioCostDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
