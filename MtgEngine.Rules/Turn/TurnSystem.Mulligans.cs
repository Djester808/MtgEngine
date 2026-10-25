using MtgEngine.Rules.Engine;
using MtgEngine.Rules.OpeningHands;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>Bridges the completed opening-hand procedure to the first turn.</summary>
public sealed partial class TurnSystem
{
    public Outcome OpenHands(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Turn.Number > 0 ? Outcome.Unchanged(state) : new OpeningHandCoordinator(zones).Start(state);
    }

    public Outcome AfterMulligans(GameState state, PlayerId finished)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Turn.Number > 0 || state.Outcome is not null || state.OpeningHands is null)
            return Outcome.Unchanged(state);
        return new OpeningHandCoordinator(zones).Advance(state)
            .Then(after => after.OpeningHands is null && after.Pending is null ? Begin(after) : Outcome.Unchanged(after));
    }
}
