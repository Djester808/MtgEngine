using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

public sealed record OpeningHandsChanged(OpeningHandsState? OpeningHands) : GameEvent
{
    public override GameState Apply(GameState state) => state with { OpeningHands = OpeningHands };
}
