using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>A validated, immutable view of turn position; it does not advance the game.</summary>
public sealed class TurnSnapshot
{
    public TurnSnapshot(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var turn = state.Turn;
        ArgumentOutOfRangeException.ThrowIfNegative(turn.Number);
        _ = state.Player(turn.ActivePlayer);
        _ = state.Player(new PlayerId(turn.TurnOrderIndex));
        if (!Enum.IsDefined(turn.Step) || turn.Step == Step.Main)
            throw new InvalidOperationException("The turn must identify a concrete step or main phase.");
        foreach (var player in turn.ExtraTurns)
            _ = state.Player(player);
        Number = turn.Number;
        ActivePlayer = turn.ActivePlayer;
        Step = turn.Step;
        TurnOrderIndex = turn.TurnOrderIndex;
        IsExtraTurn = turn.IsExtraTurn;
        ExtraTurns = turn.ExtraTurns;
    }

    /// <summary>Zero denotes setup, before the first turn begins.</summary>
    public int Number { get; }
    public PlayerId ActivePlayer { get; }
    public Step Step { get; }
    public int TurnOrderIndex { get; }
    public bool IsExtraTurn { get; }
    public ImmutableList<PlayerId> ExtraTurns { get; }
}
