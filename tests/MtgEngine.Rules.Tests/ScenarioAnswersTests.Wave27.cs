using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Scenarios;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>The runner's first legal answer, CR 601.2b: E8e asked that "you may pay" be answered by paying, as "you may" is.</summary>
public sealed class ScenarioAnswersTests
{
    [Fact]
    [RuleRef("601.2b")]
    public void A_may_pay_question_is_answered_by_paying()
    {
        var choice = new PendingChoice(
            new ChoiceId(1),
            new PlayerId(0),
            ChoiceKinds.MayPay,
            "Pay the cost?",
            [new ChoiceOption("no", "No"), new ChoiceOption("yes", "Yes")],
            1,
            1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));

        Assert.Equal(["yes"], ScenarioAnswers.FirstLegal(choice));
    }
}
