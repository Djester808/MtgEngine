using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>Priority bookkeeping and its availability, separate from turn ownership and choices.</summary>
public sealed class PrioritySnapshot
{
    public PrioritySnapshot(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Holder = state.Priority.Holder;
        PassedInSuccession = state.Priority.PassedInSuccession;
        if (Holder is { } holder)
            _ = state.Player(holder);
        foreach (var player in PassedInSuccession)
            _ = state.Player(player);
        PendingChooser = state.Pending?.Player;
        if (PendingChooser is { } chooser)
            _ = state.Player(chooser);
        IsAvailable = Holder is { } who && !state.Player(who).HasLost
            && state.Pending is null && state.Outcome is null;
    }

    /// <summary>Stored holder; a pending choice or finished game may suspend its use.</summary>
    public PlayerId? Holder { get; }
    public ImmutableList<PlayerId> PassedInSuccession { get; }
    public PlayerId? PendingChooser { get; }
    /// <summary>Whether the stored holder may use priority, not a general action-legality check.</summary>
    public bool IsAvailable { get; }
    public bool CanPass(PlayerId player) => IsAvailable && Holder == player;
}
