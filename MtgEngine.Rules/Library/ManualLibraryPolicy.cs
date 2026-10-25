using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Game-state eligibility shared by manual library commands and their projected controls.</summary>
public static class ManualLibraryPolicy
{
    public static bool IsAvailable(GameState state, PlayerId player) =>
        state.Turn.Number > 0 && state.Pending is null && state.Outcome is null && !state.Player(player).HasLost;
}
