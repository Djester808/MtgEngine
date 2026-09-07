using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>The seams cut after wave 10: a step that a turn system opens again after an out-of-band finish (E4e). The builders that read it are wave 11's.</summary>
public sealed class WaveElevenSeamTests
{
    [Fact]
    [RuleRef("117.3b")]
    public void A_turn_system_without_the_open_step_changes_nothing()
    {
        ITurnSystem turn = new Stubs.Turn();
        var board = new Board();

        var outcome = turn.OpenStep(board.State);

        Assert.Same(board.State, outcome.State);
        Assert.Empty(outcome.Events);
    }
}
