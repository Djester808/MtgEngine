using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 25, the two halves of <c>seam-requests/E9s.md</c> asks 3 and 4: a question does not show
/// one player the number another player chose in secret (CR 101.4b), and a question whose answer is
/// a number is answered by its bounds instead of by an option per number (CR 601.2b).
/// </summary>
public sealed partial class ChoiceResumptionTests
{
    /// <summary>
    /// The prefix an answer already given rides under on a later question of the same walk. It is
    /// the interpreter's own (<c>EffectInterpreter.AnsweredPrefix</c>, private there), spelled here
    /// because this test stands in for the interpreter: the rule under test reads the secret key
    /// inside it and never this prefix, so the two do not have to agree for the fix to hold.
    /// </summary>
    private const string Answered = "answered:";

    private const string SecretChoice = "wheel";

    /// <summary>
    /// CR 101.4b with 700.2: a player ordinarily knows the choices made before theirs, and a
    /// secret choice is the exception. The resolution carries the first player's number forward on
    /// the second player's question so the resumed walk does not ask the first player again — and
    /// that put the number in the state the second player's own view renders. No view of the
    /// question shows it any more.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_second_players_secret_question_does_not_show_the_first_players_number()
    {
        var board = new ChoiceResumptionBoard();
        var first = SecretKey(First);
        var asked = board.Ask(ChoiceKinds.Value, [new ChoiceOption("3", "3")], context: Empty() with { Extra = SecretsSoFar() });
        interpreter.Steps.Add(s => AskSecretly(s, Second, Carrying(first, "3")));

        var after = choices.Answer(board.State, new AnswerChoice(First, asked.Id, ["3"]));
        var second = after.State.Pending!;

        Assert.Equal(Second, second.Player);
        Assert.DoesNotContain(ChoiceSecrets.Withheld(second)!.Context.Extra, e => e.Value == "3");
        Assert.DoesNotContain(ChoiceSecrets.Withheld(second)!.Context.Extra.Keys, ChoiceSecrets.IsSecret);
    }

    /// <summary>
    /// CR 101.4: the number is hidden, not lost. Taking it off the question in the state would make
    /// the resumed walk reach the first player's step again and ask a second time, so the walk keeps
    /// every answer it has been given and only the view withholds them.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void The_state_keeps_the_secret_answer_so_the_resumed_walk_still_has_it()
    {
        var board = new ChoiceResumptionBoard();
        var first = SecretKey(First);
        var asked = board.Ask(ChoiceKinds.Value, [new ChoiceOption("3", "3")], context: Empty() with { Extra = SecretsSoFar() });
        interpreter.Steps.Add(s => AskSecretly(s, Second, Carrying(first, "3")));

        var after = choices.Answer(board.State, new AnswerChoice(First, asked.Id, ["3"]));
        var second = after.State.Pending!;

        Assert.Equal("3", ChoiceSecrets.SecretsOf(second)[Answered + first]);

        // CR 608.2: and it is still there when the second answer hands the walk back.
        interpreter.Steps.Add(Outcome.Unchanged);
        choices.Answer(after.State, new AnswerChoice(Second, second.Id, ["5"]));
        Assert.Equal("3", interpreter.Resumed[1].Context.Extra[Answered + first]);
    }

    /// <summary>CR 101.4: a question carrying no secret is shown exactly as it was asked.</summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_question_that_carries_no_secret_is_shown_unchanged()
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a");
        var asked = board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(card), context: Empty() with { Extra = SecretsSoFar() });

        Assert.Same(asked, ChoiceSecrets.Withheld(asked));
        Assert.Null(ChoiceSecrets.Withheld(null));
    }

    /* ------------------------------------------------------------------------------------------
     * The numeric answer channel, CR 601.2b with 101.4.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// CR 601.2b: a question that carries a range is answered with a number, and the number comes
    /// back as the chosen option's id — the same string the arm that asked reads back, so a walk
    /// that stores its answer by key needs no second road for it.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void A_number_inside_the_questions_range_is_the_answer()
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [], context: ChoiceNumbers.Ranged(Empty(), 0, 20));
        interpreter.Steps.Add(Outcome.Unchanged);

        choices.Answer(board.State, new AnswerChoice(First, asked.Id, ["7"]));

        var chosen = Assert.Single(interpreter.Resumed[0].Chosen);
        Assert.Equal("7", chosen.Id);
    }

    /// <summary>
    /// CR 601.2b: an open high is a question with no ceiling — "a number 0 or greater" — which is
    /// the unbounded secret choice that could not be asked at all while every answer had to be one
    /// of a list.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void An_unbounded_question_takes_a_number_no_option_list_could_have_carried()
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [], context: ChoiceNumbers.Ranged(Empty(), 0, null));
        interpreter.Steps.Add(Outcome.Unchanged);

        choices.Answer(board.State, new AnswerChoice(First, asked.Id, ["1000000"]));

        Assert.Equal("1000000", interpreter.Resumed[0].Chosen[0].Id);
        Assert.Equal(new NumberRange(0, null), ChoiceNumbers.RangeOf(asked));
    }

    /// <summary>CR 601.2b: a number below the question's low or above its high is refused.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("21")]
    [RuleRef("601.2b")]
    public void A_number_outside_the_questions_range_is_refused(string answer)
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [], context: ChoiceNumbers.Ranged(Empty(), 0, 20));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, asked.Id, [answer])));

        Assert.Equal(ChoiceSystem.RuleFor(ChoiceKinds.Value), refused.Rule);
        Assert.Empty(interpreter.Resumed);
    }

    /// <summary>
    /// CR 101.4: an answer that is not a number, or more than one of them, is refused with the rule
    /// the kind cites rather than a number being invented for the client.
    /// </summary>
    [Theory]
    [InlineData("seven")]
    [InlineData("")]
    [InlineData("3.5")]
    [RuleRef("101.4")]
    public void An_answer_that_is_not_a_number_is_refused(string answer)
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [], context: ChoiceNumbers.Ranged(Empty(), null, null));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, asked.Id, [answer])));

        Assert.Equal(ChoiceSystem.RuleFor(ChoiceKinds.Value), refused.Rule);
    }

    /// <summary>CR 101.4: a numeric question is answered with one number — not none, and not two.</summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_numeric_question_answered_with_more_or_less_than_one_number_is_refused()
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [], context: ChoiceNumbers.Ranged(Empty(), null, null));

        foreach (var answer in new[] { ImmutableList<string>.Empty, ["1", "2"] })
        {
            var refused = Assert.Throws<RefusedException>(() =>
                choices.Answer(board.State, new AnswerChoice(First, asked.Id, answer)));
            Assert.Equal(ChoiceSystem.RuleFor(ChoiceKinds.Value), refused.Rule);
        }
    }

    /// <summary>
    /// CR 601.2b: a range with nothing in it is refused where it is built, not where it is
    /// answered — a question no number satisfies would stop the game for good under CR 101.4.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void A_range_whose_low_is_above_its_high_is_refused()
    {
        var refused = Assert.Throws<RefusedException>(() => ChoiceNumbers.Ranged(Empty(), 5, 4));

        Assert.Equal("601.2b", refused.Rule);
    }

    /// <summary>
    /// CR 601.2b with 101.4: a question with no range is still answered from its options, so the
    /// channel is opened by the mark and by nothing else.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void A_question_with_no_range_is_still_answered_from_its_options()
    {
        var board = new ChoiceResumptionBoard();
        var asked = board.Ask(ChoiceKinds.Value, [new ChoiceOption("3", "3")]);

        Assert.Null(ChoiceNumbers.RangeOf(asked));
        Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, asked.Id, ["4"])));
    }

    /// <summary>
    /// CR 601.2b: a whole numeric question for a raiser with no <c>Ask</c> of its own carries no
    /// options, one answer, and the bounds it was built with.
    /// </summary>
    [Fact]
    [RuleRef("601.2b")]
    public void A_numeric_question_carries_its_bounds_and_no_options()
    {
        var question = ChoiceNumbers.Question(new ChoiceId(1), Second, ChoiceKinds.Value, "Choose a number.", 2, null);

        Assert.Empty(question.Options);
        Assert.Equal(1, question.Min);
        Assert.Equal(1, question.Max);
        Assert.Equal(new NumberRange(2, null), ChoiceNumbers.RangeOf(question));
    }

    /// <summary>The key one player's secret number is stored under, CR 700.2 — the interpreter's own.</summary>
    private static string SecretKey(PlayerId player) =>
        EffectInterpreter.SecretNumberKey(SecretChoice, player);

    private static ImmutableDictionary<string, string> Carrying(string key, string number) =>
        ImmutableDictionary<string, string>.Empty
            .Add(Answered + key, number)
            .Add("storeAs", SecretKey(Second));

    private static ImmutableDictionary<string, string> SecretsSoFar() =>
        ImmutableDictionary<string, string>.Empty.Add("storeAs", SecretKey(First));

    private static ChoiceContext Empty() =>
        new(null, null, null, ImmutableDictionary<string, string>.Empty);

    /// <summary>
    /// The interpreter asking the next player, as <c>EffectInterpreter.Ask</c> does: the marks the
    /// resumed walk is carrying — every answer already given — are copied onto the new question.
    /// </summary>
    private static Outcome AskSecretly(GameState state, PlayerId player, ImmutableDictionary<string, string> carried)
    {
        var requested = new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            player,
            ChoiceKinds.Value,
            "Choose a number, in secret.",
            [new ChoiceOption("5", "5")],
            1,
            1,
            Empty() with { Extra = carried }));
        return new Outcome(requested.Apply(state), [requested]);
    }
}
