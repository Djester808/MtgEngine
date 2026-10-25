namespace MtgEngine.Rules.State;

/// <summary>Living-seat succession shared by turn progression and player departure.</summary>
internal static class PlayerTurnOrder
{
    public static PlayerId? NextLiving(GameState state, PlayerId from)
    {
        for (var offset = 1; offset <= state.Players.Count; offset++)
        {
            var candidate = new PlayerId((from.Value + offset) % state.Players.Count);
            if (!state.Player(candidate).HasLost)
                return candidate;
        }
        return null;
    }
}
