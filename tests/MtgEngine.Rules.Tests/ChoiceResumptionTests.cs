using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 101.4: the game stops for a choice and nothing else happens until it is answered. Each
/// kind is answered here and refused here, because a resumption that trusts its answer is the
/// place a bad client would otherwise reach the board.
/// </summary>
public sealed class ChoiceResumptionTests
{
    private static readonly PlayerId First = new(0);
    private static readonly PlayerId Second = new(1);

    private readonly ChoiceResumptionFakeZones zones = new();
    private readonly ChoiceResumptionFakeTurn turn = new();
    private readonly ChoiceResumptionFakeResumable interpreter = new();
    private readonly ChoiceSystem choices;

    public ChoiceResumptionTests() => choices = new ChoiceSystem(zones, turn, interpreter);

    [Fact]
    [RuleRef("101.4")]
    public void An_answer_with_nothing_pending_is_refused()
    {
        var board = new ChoiceResumptionBoard();

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, new ChoiceId(1), [])));

        Assert.Equal("101.4", refused.Rule);
    }

    [Fact]
    [RuleRef("101.4")]
    public void An_answer_to_a_question_that_is_not_open_is_refused()
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a");
        board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(card));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, new ChoiceId(99), ChoiceResumptionBoard.Answer(card))));

        Assert.Equal("101.4", refused.Rule);
        Assert.Empty(zones.Moves);
    }

    [Fact]
    [RuleRef("101.4")]
    public void Another_players_answer_is_refused()
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a");
        var choice = board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(card));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(Second, choice.Id, ChoiceResumptionBoard.Answer(card))));

        Assert.Equal("101.4", refused.Rule);
    }

    [Fact]
    [RuleRef("101.4")]
    public void The_same_option_cannot_be_chosen_twice()
    {
        var board = new ChoiceResumptionBoard();
        var a = board.Put("a");
        board.Put("b");
        var choice = board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(a), min: 2, max: 2);

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(a, a))));

        Assert.Equal("514.1", refused.Rule);
        Assert.Empty(zones.Moves);
    }

    [Fact]
    [RuleRef("514.1")]
    public void Discarding_moves_the_chosen_cards_and_finishes_the_step()
    {
        var board = new ChoiceResumptionBoard();
        var a = board.Put("a");
        var b = board.Put("b");
        var c = board.Put("c");
        var choice = board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(a, b, c), min: 2, max: 2);

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(a, c)));

        Assert.Single(outcome.Events.OfType<ChoiceAnswered>());
        Assert.Null(outcome.State.Pending);
        Assert.Equal(new[] { a, c }, zones.Moves.Select(m => m.What).ToArray());
        Assert.All(zones.Moves, m => Assert.Equal(ZoneKey.Of(Zone.Graveyard, First), m.To));
        Assert.Equal(1, turn.Advanced);
        Assert.Equal(b, Assert.Single(outcome.State.In(ZoneKey.Of(Zone.Hand, First))));
    }

    [Fact]
    [RuleRef("514.1")]
    public void Discarding_the_wrong_number_is_refused()
    {
        var board = new ChoiceResumptionBoard();
        var a = board.Put("a");
        var b = board.Put("b");
        var choice = board.Ask(ChoiceKinds.Discard, ChoiceResumptionBoard.Options(a, b), min: 2, max: 2);

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(a))));

        Assert.Equal("514.1", refused.Rule);
        Assert.Empty(zones.Moves);
        Assert.Equal(0, turn.Advanced);
    }

    [Fact]
    [RuleRef("704.5j")]
    public void The_legend_rule_buries_the_duplicates_that_were_not_kept()
    {
        var board = new ChoiceResumptionBoard();
        var first = board.Put("legend", Zone.Battlefield, supertypes: Supertype.Legendary);
        var second = board.Put("legend", Zone.Battlefield, supertypes: Supertype.Legendary);
        var third = board.Put("legend", Zone.Battlefield, supertypes: Supertype.Legendary);
        var choice = board.Ask(ChoiceKinds.LegendRule, ChoiceResumptionBoard.Options(first, second, third));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(second)));

        Assert.Null(outcome.State.Pending);
        Assert.Equal(new[] { first, third }, zones.Moves.Select(m => m.What).ToArray());
        Assert.All(zones.Moves, m => Assert.Equal(ZoneKey.Of(Zone.Graveyard, First), m.To));

        // The kept one is untouched, and nothing advanced the turn: the next state-based
        // action check is what looks at the board again, CR 704.3.
        Assert.Contains(second, outcome.State.Objects.Keys);
        Assert.Equal(0, turn.Advanced);
    }

    [Fact]
    [RuleRef("704.5j")]
    public void Keeping_two_legends_is_refused()
    {
        var board = new ChoiceResumptionBoard();
        var first = board.Put("legend", Zone.Battlefield, supertypes: Supertype.Legendary);
        var second = board.Put("legend", Zone.Battlefield, supertypes: Supertype.Legendary);
        var choice = board.Ask(ChoiceKinds.LegendRule, ChoiceResumptionBoard.Options(first, second));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(first, second))));

        Assert.Equal("704.5j", refused.Rule);
        Assert.Empty(zones.Moves);
    }

    [Fact]
    [RuleRef("103.5")]
    public void A_mulligan_shuffles_the_hand_back_redraws_and_asks_again()
    {
        var board = Opening(handSize: 3, inLibrary: 5);
        var hand = board.In(Zone.Hand);
        var choice = board.Ask(ChoiceKinds.Mulligan, Declaration(), context: ChoiceSystem.MulliganContext(0, 3));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, [ChoiceKinds.Mulligan]));

        Assert.Equal(hand.ToArray(), zones.Moves.Select(m => m.What).ToArray());
        Assert.All(zones.Moves, m => Assert.Equal(ZoneKey.Of(Zone.Library, First), m.To));
        Assert.Equal(First, Assert.Single(zones.Shuffles));
        Assert.Equal((First, 3), Assert.Single(zones.Draws));

        var next = Assert.IsType<PendingChoice>(outcome.State.Pending);
        Assert.Equal(ChoiceKinds.Mulligan, next.Kind);
        Assert.Equal("1", next.Context.Extra["mulligans"]);
        Assert.Equal(3, outcome.State.In(ZoneKey.Of(Zone.Hand, First)).Count);
    }

    [Fact]
    [RuleRef("103.5")]
    public void Keeping_after_a_mulligan_asks_which_card_goes_on_the_bottom()
    {
        var board = Opening(handSize: 3, inLibrary: 2);
        var choice = board.Ask(ChoiceKinds.Mulligan, Declaration(), context: ChoiceSystem.MulliganContext(1, 3));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, [ChoiceKinds.Keep]));

        var next = Assert.IsType<PendingChoice>(outcome.State.Pending);
        Assert.Equal(ChoiceKinds.MulliganBottom, next.Kind);
        Assert.Equal(1, next.Min);
        Assert.Equal(1, next.Max);
        Assert.Equal(3, next.Options.Count);
        Assert.Empty(zones.Moves);
    }

    [Fact]
    [RuleRef("103.5")]
    public void Keeping_the_first_hand_asks_nothing_more()
    {
        var board = Opening(handSize: 3, inLibrary: 2);
        var choice = board.Ask(ChoiceKinds.Mulligan, Declaration(), context: ChoiceSystem.MulliganContext(0, 3));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, [ChoiceKinds.Keep]));

        Assert.Null(outcome.State.Pending);
        Assert.Empty(zones.Moves);
        Assert.Empty(zones.Draws);
    }

    [Fact]
    [RuleRef("103.5")]
    public void Mulligans_stop_when_the_opening_hand_would_be_empty()
    {
        // CR 103.5: a player may take mulligans until their opening hand would be zero cards.
        var board = Opening(handSize: 1, inLibrary: 3);
        var choice = board.Ask(ChoiceKinds.Mulligan, Declaration(), context: ChoiceSystem.MulliganContext(0, 1));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, [ChoiceKinds.Mulligan]));

        var next = Assert.IsType<PendingChoice>(outcome.State.Pending);
        Assert.Equal(ChoiceKinds.MulliganBottom, next.Kind);
        Assert.Equal(1, next.Min);
    }

    [Fact]
    [RuleRef("103.5")]
    public void A_mulligan_declaration_that_is_neither_option_is_refused()
    {
        var board = Opening(handSize: 3, inLibrary: 2);
        var choice = board.Ask(ChoiceKinds.Mulligan, Declaration(), context: ChoiceSystem.MulliganContext(0, 3));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ["shuffle-and-see"])));

        Assert.Equal("103.5", refused.Rule);
        Assert.Empty(zones.Draws);
    }

    [Fact]
    [RuleRef("103.5")]
    public void Bottoming_puts_the_chosen_cards_under_the_library()
    {
        var board = Opening(handSize: 3, inLibrary: 2);
        var hand = board.In(Zone.Hand);
        var choice = board.Ask(
            ChoiceKinds.MulliganBottom,
            ChoiceResumptionBoard.Options([.. hand]),
            min: 2,
            max: 2,
            context: ChoiceSystem.MulliganContext(2, 3));

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(hand[2], hand[0])));

        Assert.Null(outcome.State.Pending);
        Assert.Equal(new[] { hand[2], hand[0] }, zones.Moves.Select(m => m.What).ToArray());
        Assert.All(zones.Moves, m =>
        {
            Assert.Equal(ZoneKey.Of(Zone.Library, First), m.To);
            Assert.Equal(Placement.Bottom, m.Where);
        });
        Assert.Single(outcome.State.In(ZoneKey.Of(Zone.Hand, First)));
    }

    [Fact]
    [RuleRef("103.5")]
    public void Bottoming_fewer_cards_than_the_mulligans_taken_is_refused()
    {
        var board = Opening(handSize: 3, inLibrary: 2);
        var hand = board.In(Zone.Hand);
        var choice = board.Ask(ChoiceKinds.MulliganBottom, ChoiceResumptionBoard.Options([.. hand]), min: 2, max: 2);

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(hand[0]))));

        Assert.Equal("103.5", refused.Rule);
        Assert.Empty(zones.Moves);
    }

    [Theory]
    [InlineData(ChoiceKinds.TriggerOrder)]
    [InlineData(ChoiceKinds.ReplacementOrder)]
    [InlineData(ChoiceKinds.May)]
    [InlineData(ChoiceKinds.Value)]
    [InlineData(ChoiceKinds.Search)]
    [InlineData(ChoiceKinds.Scry)]
    [InlineData(ChoiceKinds.Targets)]
    [RuleRef("608.2")]
    public void A_question_a_card_asked_goes_back_to_the_interpreter(string kind)
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a", Zone.Battlefield);
        var context = new ChoiceContext(card, 1, "sequence/2", ImmutableDictionary<string, string>.Empty.Add("mode", "second"));
        var choice = board.Ask(kind, ChoiceResumptionBoard.Options(card), context: context);

        var outcome = choices.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(card)));

        // The answer is recorded and the question closed before anything resumes: replaying the
        // log reaches the same board without replaying whatever the effect was doing, CR 101.4.
        Assert.Single(outcome.Events.OfType<ChoiceAnswered>());
        Assert.Null(outcome.State.Pending);

        var resumed = Assert.Single(interpreter.Resumed);
        Assert.Equal(kind, resumed.Kind);
        Assert.Equal(First, resumed.Player);
        Assert.Equal(card, Assert.Single(resumed.Chosen).Object!.Value);
        Assert.Equal(context, resumed.Context);
        Assert.Empty(zones.Moves);
    }

    [Theory]
    [InlineData(ChoiceKinds.TriggerOrder, "603.3b")]
    [InlineData(ChoiceKinds.ReplacementOrder, "616.1")]
    [InlineData(ChoiceKinds.May, "608.2")]
    [InlineData(ChoiceKinds.Value, "601.2b")]
    [InlineData(ChoiceKinds.Search, "701.23")]
    [InlineData(ChoiceKinds.Scry, "701.22")]
    [InlineData(ChoiceKinds.Targets, "601.2c")]
    [RuleRef("101.4")]
    public void An_option_that_was_not_offered_is_refused_with_the_rule_the_kind_cites(string kind, string rule)
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a", Zone.Battlefield);
        var choice = board.Ask(kind, ChoiceResumptionBoard.Options(card));

        var refused = Assert.Throws<RefusedException>(() =>
            choices.Answer(board.State, new AnswerChoice(First, choice.Id, ["not-an-option"])));

        Assert.Equal(rule, refused.Rule);
        Assert.Empty(interpreter.Resumed);
    }

    [Fact]
    [RuleRef("101.4")]
    public void An_unbuilt_interpreter_names_the_agent_that_owes_the_resumption()
    {
        var board = new ChoiceResumptionBoard();
        var card = board.Put("a", Zone.Battlefield);
        var choice = board.Ask(ChoiceKinds.Targets, ChoiceResumptionBoard.Options(card));
        var alone = new ChoiceSystem(zones, turn);

        var missing = Assert.Throws<NotImplementedException>(() =>
            alone.Answer(board.State, new AnswerChoice(First, choice.Id, ChoiceResumptionBoard.Answer(card))));

        Assert.Contains("E9", missing.Message, StringComparison.Ordinal);
    }

    private static ImmutableList<ChoiceOption> Declaration() =>
        [new ChoiceOption(ChoiceKinds.Keep, "Keep"), new ChoiceOption(ChoiceKinds.Mulligan, "Mulligan")];

    private static ChoiceResumptionBoard Opening(int handSize, int inLibrary)
    {
        var board = new ChoiceResumptionBoard();
        for (var i = 0; i < handSize; i++)
            board.Put($"hand {i}");
        for (var i = 0; i < inLibrary; i++)
            board.Put($"library {i}", Zone.Library);
        return board;
    }
}
