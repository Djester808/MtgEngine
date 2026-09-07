using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Who holds priority once an answer has finished what the question interrupted, CR 117.3b with
/// 608.2. A resolution that stopped for a question (CR 101.4) is still a resolution, so the answer
/// that finishes it ends where an unhalted one ends: the active player receives priority in the
/// same step and the game walks on. Before this the stack emptied and nobody could act — the road
/// EINT4 § 3 found on the may-pay, the same shape E4e closed for a halted combat damage batch.
/// <para>
/// The first test is a real game through <see cref="Game.Apply"/> on <see cref="Subsystems.Wave2"/>;
/// the others are the choice system against fakes, where what can be seen is that the turn system
/// was asked and what stood when it was. That the turn system opens nothing while a question still
/// stands is <c>TurnStructureTests</c>'s, and the two together are why a resolution that stops
/// twice opens the step once.
/// </para>
/// </summary>
public sealed partial class ChoiceResumptionTests
{
    /// <summary>
    /// CR 118.12 answered yes, then CR 117.3b: the triggered ability's resolution stopped to ask
    /// whether {2} would be paid, the answer paid it and drew — and the active player then has
    /// priority, so a round of passes ends the step. This is EINT4 § 3's reproduction with the
    /// gap closed.
    /// </summary>
    [Fact]
    [RuleRef("117.3b")]
    public void A_resolution_finished_by_an_answer_leaves_the_active_player_with_priority()
    {
        var table = new WaveNineIntegrationTable(ScholarDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(First);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(First);
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(First);

        // Three mana: one casts the creature, two are what the question is about.
        table.TapForMana(First, 3);
        table.Do(new CastSpellCommand(First, table.InHand(First, WaveNineIntegrationTable.ScholarName)));
        var asked = table.PassUntilQuestion();
        Assert.Equal("may-pay", asked.Kind);

        // CR 608.2: while the resolution is stopped nobody holds priority — that is the state the
        // answer has to leave behind it.
        Assert.Null(table.State.Priority.Holder);

        table.Answer("yes");

        Assert.Null(table.State.Pending);
        Assert.Equal(First, table.State.Priority.Holder);
        Assert.Equal(Step.PrecombatMain, table.State.Turn.Step);
        Assert.Equal(0, table.State.Player(First).Mana.Total);

        // CR 117.4: a round of passes on the open step ends it. The game used to stop dead here.
        table.PassRound();
        Assert.Equal(Step.BeginningOfCombat, table.State.Turn.Step);
    }

    /// <summary>
    /// CR 117.3b: the choice system hands every card's answer back to the turn system to open the
    /// step, whatever the question was about — this one is a resolution the interpreter says is
    /// finished (CR 608.2m).
    /// </summary>
    [Fact]
    [RuleRef("117.3b")]
    public void An_answer_that_finished_a_resolution_asks_the_turn_system_to_open_the_step()
    {
        var board = new ChoiceResumptionBoard();
        var choice = board.Ask(ChoiceKinds.May, [], min: 0, max: 0);

        choices.Answer(board.State, new AnswerChoice(First, choice.Id, []));

        Assert.Null(Assert.Single(turn.Opens));
    }

    /// <summary>
    /// CR 101.4 with 117.3b: a resolution that stops a second time is asked about twice, and only
    /// the last of those answers finds nothing standing — the only one the turn system opens on,
    /// so however many times one resolution stops, the step opens once. The same shape through a
    /// real game is <c>CombatReplacementTests</c>'s twice-halted batch.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_resolution_that_stops_again_leaves_the_step_shut_until_the_last_answer()
    {
        var board = new ChoiceResumptionBoard();
        var first = board.Ask(ChoiceKinds.May, [], min: 0, max: 0);
        interpreter.Steps.Add(Again);

        var stopped = choices.Answer(board.State, new AnswerChoice(First, first.Id, []));
        var second = stopped.State.Pending!;
        choices.Answer(stopped.State, new AnswerChoice(First, second.Id, []));

        Assert.Equal(2, turn.Opens.Count);
        Assert.Equal(second, turn.Opens[0]);
        Assert.Null(turn.Opens[1]);
    }

    /// <summary>A resumption that stops the resolution again, CR 101.4: the same player, a second question.</summary>
    private static Outcome Again(GameState state)
    {
        var again = new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            First,
            ChoiceKinds.May,
            "and again?",
            [],
            0,
            0,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty)));
        return new Outcome(again.Apply(state), [again]);
    }

    /// <summary>Lands and the creature whose arrival offers a payment, CR 118.12.</summary>
    private static ImmutableList<string> ScholarDeck() =>
    [
        .. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveNineIntegrationTable.ScholarId, 10),
    ];

    /// <summary>The other seat plays lands and nothing else, so nothing it does is what a test is about.</summary>
    private static ImmutableList<string> LandDeck() => [.. Enumerable.Repeat(WaveNineIntegrationTable.GroveId, 24)];
}
