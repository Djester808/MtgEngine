using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 726.2, the monarch's twin.
///
/// <c>GameState.Initiative</c> was read by <c>Query.Conditions</c> and written by nothing in the
/// whole solution — the only assignment was in a test fixture. So every card asking "if you have
/// the initiative" answered false for every player for the entire game, and the false branch ran
/// silently. The vocabulary had carried <see cref="TakeInitiative"/> and
/// <see cref="TakesInitiative"/> since wave 23; the event, the interpreter arm and the trigger
/// matcher were the missing half.
/// </summary>
public sealed class InitiativeTests
{
    private static readonly PlayerId Alice = new(0);
    private static readonly PlayerId Bob = new(1);

    [Fact]
    [RuleRef("726.2")]
    public void Nobody_has_the_initiative_to_begin_with()
    {
        Assert.Null(new Board().State.Initiative);
    }

    [Fact]
    [RuleRef("726.2")]
    public void Taking_the_initiative_is_recorded()
    {
        var board = new Board();

        board.Fold(new InitiativeChanged(Bob));

        Assert.Equal(Bob, board.State.Initiative);
    }

    /// <summary>CR 726.2: it leaves whoever held it, exactly as the monarch does.</summary>
    [Fact]
    [RuleRef("726.2")]
    public void It_leaves_whoever_held_it()
    {
        var board = new Board();
        board.Fold(new InitiativeChanged(Alice));

        board.Fold(new InitiativeChanged(Bob));

        Assert.Equal(Bob, board.State.Initiative);
    }

    /// <summary>
    /// The whole point: a replay of the log restores it. A designation the log could not carry
    /// would be lost on the first reload, which is why it needed an event rather than a field
    /// somebody set.
    /// </summary>
    [Fact]
    [RuleRef("726.2")]
    public void It_survives_a_replay_of_the_log()
    {
        var board = new Board();
        board.Fold(new InitiativeChanged(Bob));

        var replayed = new InitiativeChanged(Bob).Apply(new Board().State);

        Assert.Equal(board.State.Initiative, replayed.Initiative);
    }
}
