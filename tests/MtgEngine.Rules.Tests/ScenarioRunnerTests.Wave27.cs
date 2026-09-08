using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Two honesty defects the runner carried into wave 27. CR 605.1a names <em>four</em> criteria for a
/// mana ability and the runner's private copy checked two, so an ability that adds mana and then
/// touches a library counted as one — the number a generated scenario is written against was
/// computed by a predicate that disagreed with every other reading of the rule
/// (seam-requests/E5k.md § 4). CR 110.5d says only permanents have status, so an expectation that
/// asks whether a card in a library is tapped was being answered "untapped" — which fails a
/// scenario that asserted the tap and, worse, would have passed one that asserted the absence of it.
/// </summary>
public sealed class ScenarioRunnerTestsWave27
{
    private const string Fount = "runner-w27-fount";
    private const string Cistern = "runner-w27-cistern";
    private const string Draught = "runner-w27-draught";

    /// <summary>
    /// CR 605.1a's fourth criterion: an activated ability whose cost or effect moves a card to or
    /// from a library is not a mana ability however much mana it adds. The Fount prints that ability
    /// first and a plain "{T}: Add {U}" second, so mana ability number zero is the <em>second</em> of
    /// its two abilities. The colour in the pool is the whole test — the runner's own copy of the
    /// rule reached the first one, which adds {G} and draws, and the hand and library counts say
    /// whether that happened.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void An_ability_that_adds_mana_and_draws_is_not_the_mana_ability_the_step_counts()
    {
        var result = Run(new Scenario(
            "wave27-fourth-criterion",
            "a permanent whose first ability adds mana and draws is tapped for the second",
            new GeneratedSource("test"),
            Board([new CardRef(Fount)]),
            [new TapForManaStep("A", Fount, AbilityIndex: 0)],
            [
                new ManaPoolIs("A", "U"),
                new TappedIs(Fount, true),
                new ZoneCount("A", Zone.Hand, 0),
                new ZoneCount("A", Zone.Library, LibrarySize),
            ]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, Explain(result));
    }

    /// <summary>
    /// The same criterion where there is nothing else to reach: a permanent whose only mana-adding
    /// ability draws a card has no mana ability at all, and the step is refused with the rule rather
    /// than activating it and reporting a pool the scenario never meant.
    /// </summary>
    [Fact]
    [RuleRef("605.1a")]
    public void A_permanent_whose_only_mana_adding_ability_draws_has_no_mana_ability()
    {
        var result = Run(new Scenario(
            "wave27-fourth-criterion-only",
            "a permanent whose only mana-adding ability draws a card",
            new GeneratedSource("test"),
            Board([new CardRef(Cistern)]),
            [new TapForManaStep("A", Cistern)],
            [new StackEmpty()]));

        Assert.NotNull(result.Error);
        Assert.Contains("605.1a", result.Error!, StringComparison.Ordinal);
        Assert.Contains("no mana ability", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CR 110.5d with CR 400.7: the Draught's ability shuffles its own source into its owner's
    /// library, so the object the scenario's name follows is a library card by the time the
    /// expectations are checked — and a library card is neither tapped nor untapped. The runner has
    /// to say so with the zone and the rule; answering "untapped" reports a status the card does not
    /// have and blames the tap cost that was in fact paid.
    /// </summary>
    [Fact]
    [RuleRef("110.5d")]
    public void A_source_that_shuffled_itself_away_is_neither_tapped_nor_untapped()
    {
        var result = Run(Shuffles(true));

        Assert.Null(result.Error);
        Assert.False(result.Passed, Explain(result));
        Assert.True(Result<InZone>(result).Passed, Explain(result));
        var tapped = Result<TappedIs>(result);
        Assert.False(tapped.Passed);
        Assert.Contains("110.5d", tapped.Actual!, StringComparison.Ordinal);
        Assert.Contains(nameof(Zone.Library), tapped.Actual!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that was silently wrong rather than loudly wrong: asking for "untapped" about the
    /// same library card used to pass, because the field on a card outside the battlefield reads
    /// false. CR 110.5d makes both spellings the same mistake, so both fail with the zone.
    /// </summary>
    [Fact]
    [RuleRef("110.5d")]
    public void The_untapped_spelling_of_the_same_expectation_does_not_pass_either()
    {
        var result = Run(Shuffles(false));

        Assert.Null(result.Error);
        Assert.False(result.Passed, Explain(result));
        var tapped = Result<TappedIs>(result);
        Assert.False(tapped.Passed);
        Assert.Contains("110.5d", tapped.Actual!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scenario both CR 110.5d tests run, differing only in the status they claim: activate the
    /// Draught, let it resolve, then ask about a card that is no longer on the battlefield. The
    /// <see cref="InZone"/> expectation beside it is the guard against a vacuous pass — it says the
    /// shuffle really happened and the name really followed the object (CR 400.7).
    /// </summary>
    private static Scenario Shuffles(bool tapped) =>
        new(
            $"wave27-status-off-the-battlefield-{tapped}",
            "a permanent that shuffles itself into its owner's library, then asked about its status",
            new GeneratedSource("test"),
            Board([new CardRef(Draught)]),
            [new ActivateStep("A", Draught, 0), new ResolveAllStep()],
            [new InZone(Draught, Zone.Library), new TappedIs(Draught, tapped), new StackEmpty()]);

    /// <summary>The filler lands player A's library is stocked with, so a draw and a shuffle both have somewhere to go.</summary>
    private const int LibrarySize = 2;

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioRunnerWave27Definitions(FountCard(), CisternCard(), DraughtCard()))).Run(scenario);

    private static ScenarioSetup Board(IReadOnlyList<CardRef> battlefield) =>
        new(
            [
                new PlayerSetup("A", Battlefield: battlefield, Library: [.. Enumerable.Repeat(new CardRef(ScenarioFiller.Land), LibrarySize)]),
                new PlayerSetup("B"),
            ],
            "A");

    private static ExpectationResult Result<T>(ScenarioResult result)
        where T : Expectation =>
        result.Results.Single(r => r.Expectation is T);

    private static string Explain(ScenarioResult result) =>
        string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    /// <summary>
    /// Two abilities in the order that makes the fourth criterion visible: "{T}: Add {G}. Draw a
    /// card." is not a mana ability (CR 605.1a), and "{T}: Add {U}." is, so the step's mana ability
    /// number zero is the one at ability index 1.
    /// </summary>
    private static CardDefinition FountCard() =>
        new(Fount, Fount, Layout.Normal,
            [new Face(Fount, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}",
                Abilities:
                [
                    new ActivatedAbility([new TapSelf()], new Sequence([new AddMana(new ManaProduction("{G}")), new Draw(new Fixed(1))])),
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction("{U}"))),
                ])]);

    /// <summary>The same non-mana ability with nothing behind it, so the runner has none to find.</summary>
    private static CardDefinition CisternCard() =>
        new(Cistern, Cistern, Layout.Normal,
            [new Face(Cistern, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], new Sequence([new AddMana(new ManaProduction("{G}")), new Draw(new Fixed(1))]))])]);

    /// <summary>An artifact that taps to shuffle itself into its owner's library (CR 701.20 with 400.7).</summary>
    private static CardDefinition DraughtCard() =>
        new(Draught, Draught, Layout.Normal,
            [new Face(Draught, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{1}",
                Abilities: [new ActivatedAbility([new TapSelf()], new Shuffle(IntoLibrary: new Self()))])]);

    private sealed class ScenarioRunnerWave27Definitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
