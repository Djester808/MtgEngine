using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// How a generated scenario answers a search. Two questions used to be answered wrongly: a search
/// that may take several took one, and a search with nothing to take was read as a question with no
/// legal answer, which left it standing and stalled the resolution behind it (CR 701.23b).
/// </summary>
public sealed class ScenarioRunnerTestsSearching
{
    private const string Tutor = "runner-tutor";
    private const string Fetched = "runner-fetched";

    [Fact]
    [RuleRef("701.23b")]
    public void A_search_that_finds_nothing_is_answered_with_none_and_the_spell_still_resolves()
    {
        // The library holds nothing the search describes, so the question offers no options at all.
        // Finding nothing is legal; leaving the question standing would keep the spell on the stack.
        var result = Run(Tutoring(library: [], expect: [new InZone(Tutor, Zone.Graveyard), new StackEmpty()]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
    }

    [Fact]
    [RuleRef("701.23a")]
    public void A_search_for_up_to_two_takes_both_of_them()
    {
        var result = Run(Tutoring(
            library: [new CardRef(Fetched, Alias: "one"), new CardRef(Fetched, Alias: "two")],
            expect: [new InZone("one", Zone.Hand), new InZone("two", Zone.Hand), new StackEmpty()]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
    }

    [Fact]
    [RuleRef("701.20")]
    public void The_shuffle_a_search_ends_with_is_a_shuffle_and_not_any_reorder()
    {
        var result = Run(Tutoring(
            library: [new CardRef(Fetched, Alias: "one")],
            expect: [new Shuffled("A"), new StackEmpty()]));

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}")));
    }

    /// <summary>A sorcery that searches for up to two artifact cards and puts them in hand, cast over the given library.</summary>
    private static Scenario Tutoring(IReadOnlyList<CardRef> library, IReadOnlyList<Expectation> expect) => new(
        "runner-tutoring",
        "a search takes what it may and shuffles",
        new GeneratedSource("test"),
        new ScenarioSetup(
            [
                new PlayerSetup("A", Hand: [new CardRef(Tutor)], Library: library, ManaPool: "GG"),
                new PlayerSetup("B"),
            ],
            "A",
            AutoAnswer: true),
        [new CastStep("A", Tutor), new ResolveAllStep()],
        expect);

    private static ScenarioResult Run(Scenario scenario) =>
        new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioRunnerSearchDefinitions(TutorCard(), FetchedCard()))).Run(scenario);

    private static CardDefinition TutorCard() =>
        new(Tutor, Tutor, Layout.Normal,
            [new Face(Tutor, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}{G}",
                Abilities: [new SpellAbility(new Search(new ObjectFilter(Types: CardType.Artifact), Zone.Hand, UpTo: new Fixed(2)))])]);

    private static CardDefinition FetchedCard() =>
        new(Fetched, Fetched, Layout.Normal,
            [new Face(Fetched, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{2}")]);

    private sealed class ScenarioRunnerSearchDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
