using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A generated scenario cannot name an option id, because the ids belong to objects it has never
/// seen; so a tutor, a "you may" and a modal spell all stopped the runner halfway and were counted
/// as failures of the card rather than of the scenario (seam-requests/P8.md § 2). These check that
/// the runner, when the caller opts in, answers such a question with its first legal option and
/// plays on — and that it leaves a scenario which answers its own questions alone.
/// </summary>
public sealed class ScenarioAnswerTests
{
    private const string Tutor = "answers-tutor";
    private const string Maybe = "answers-may";
    private const string Modal = "answers-modal";

    [Fact]
    [RuleRef("701.23")]
    public void A_search_is_answered_with_the_first_card_it_may_take()
    {
        var scenario = Play(Tutor, [new ZoneCount("A", Zone.Hand, 1), new ZoneCount("A", Zone.Library, 2), new StackEmpty()], autoAnswer: true);

        var answered = Runner().Run(scenario);

        Assert.Null(answered.Error);
        Assert.True(answered.Passed, Why(answered));
        Assert.Contains(answered.Log!, line => line.StartsWith("ChoiceAnswered", StringComparison.Ordinal));
    }

    [Fact]
    [RuleRef("101.4")]
    public void Without_the_opt_in_the_search_leaves_the_spell_on_the_stack()
    {
        // The failure this whole file exists for: the spell resolves as far as the search, the
        // game stops to ask, and the card is reported as failing when the scenario is what fell short.
        var scenario = Play(Tutor, [new StackEmpty()], autoAnswer: false);

        var unanswered = Runner().Run(scenario);

        Assert.False(unanswered.Passed);
        Assert.Equal("1 on the stack", Assert.Single(unanswered.Results).Actual);
    }

    [Fact]
    [RuleRef("608.2")]
    public void A_may_is_answered_yes_so_the_optional_effect_happens()
    {
        var scenario = Play(Maybe, [new ZoneCount("A", Zone.Hand, 1), new StackEmpty()], autoAnswer: true);

        var answered = Runner().Run(scenario);

        Assert.Null(answered.Error);
        Assert.True(answered.Passed, Why(answered));
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_mode_is_answered_with_the_first_mode()
    {
        // Mode 0 draws and mode 1 gains life, so a hand of one says which mode was taken.
        var scenario = Play(Modal, [new ZoneCount("A", Zone.Hand, 1), new Life("A", 20), new StackEmpty()], autoAnswer: true);

        var answered = Runner().Run(scenario);

        Assert.Null(answered.Error);
        Assert.True(answered.Passed, Why(answered));
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_scenario_that_answers_its_own_questions_is_left_alone()
    {
        // Mode 1 is the scenario's own choice; the runner must not overrule it with mode 0.
        var scenario = Play(Modal, [new Life("A", 23), new StackEmpty()], autoAnswer: true);
        // The question is asked while the spell resolves, so the scenario's answer comes after
        // the first ResolveAll — which is where the runner would otherwise have answered it.
        scenario = scenario with { Steps = [.. scenario.Steps, new AnswerStep("A", ["1"]), new ResolveAllStep()] };

        var answered = Runner().Run(scenario);

        Assert.Null(answered.Error);
        Assert.True(answered.Passed, Why(answered));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_value_is_answered_with_the_smallest_number_offered()
    {
        var choice = Question(ChoiceKinds.Value, ["3", "1", "2"], min: 1, max: 1);

        Assert.Equal(["1"], ScenarioAnswers.FirstLegal(choice));
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_question_with_no_options_is_left_standing()
    {
        Assert.Empty(ScenarioAnswers.FirstLegal(Question(ChoiceKinds.Search, [], min: 0, max: 1)));
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_question_that_wants_two_gets_the_first_two()
    {
        var choice = Question(ChoiceKinds.Discard, ["a", "b", "c"], min: 2, max: 2);

        Assert.Equal(["a", "b"], ScenarioAnswers.FirstLegal(choice));
    }

    private static PendingChoice Question(string kind, string[] options, int min, int max) =>
        new(
            new ChoiceId(1),
            new PlayerId(0),
            kind,
            "a question",
            [.. options.Select(o => new ChoiceOption(o, o))],
            min,
            max,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));

    /// <summary>Cast the card with a library to search and nothing else in hand, then let it resolve.</summary>
    private static Scenario Play(string card, Expectation[] expect, bool autoAnswer = false) =>
        new(
            "answers-" + card,
            "the runner answers the question this card asks",
            new GeneratedSource("test"),
            new ScenarioSetup(
                [
                    new PlayerSetup("A", Hand: [new CardRef(card)], Library: [new CardRef(ScenarioFiller.Land), new CardRef(ScenarioFiller.Land), new CardRef(ScenarioFiller.Land)], ManaPool: "999"),
                    new PlayerSetup("B"),
                ],
                "A",
                AutoAnswer: autoAnswer),
            [new CastStep("A", card), new ResolveAllStep()],
            expect);

    private static string Why(ScenarioResult result) =>
        string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation.GetType().Name}: {r.Actual}"));

    private static ScenarioRunner Runner() =>
        new(new Game(Subsystems.Wave2(), new ScenarioAnswerDefinitions(TutorCard(), MayCard(), ModalCard())));

    private static CardDefinition TutorCard() =>
        new(Tutor, Tutor, Layout.Normal,
            [new Face(Tutor, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}",
                Abilities: [new SpellAbility(new Search(new ObjectFilter(Types: CardType.Land), Zone.Hand))])]);

    private static CardDefinition MayCard() =>
        new(Maybe, Maybe, Layout.Normal,
            [new Face(Maybe, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}",
                Abilities: [new SpellAbility(new May(new Draw(new Fixed(1))))])]);

    private static CardDefinition ModalCard() =>
        new(Modal, Modal, Layout.Normal,
            [new Face(Modal, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}",
                Abilities:
                [
                    new SpellAbility(new ChooseMode(
                    [
                        new Mode("Draw a card.", new Draw(new Fixed(1))),
                        new Mode("You gain 3 life.", new GainLife(new Fixed(3))),
                    ])),
                ])]);

    private sealed class ScenarioAnswerDefinitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
