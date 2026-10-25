using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

public sealed record LibraryInspectionChanged(string Scope, LibraryInspection? Inspection) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        LibraryInspections = Inspection is null ? state.LibraryInspections.Remove(Scope)
            : state.LibraryInspections.SetItem(Scope, Inspection),
    };
}

public sealed record CardsRevealed(CardReveal Reveal) : GameEvent
{
    /// <summary>At least one disclosed card was in a library when revealed (CR 733.1).</summary>
    public bool FromLibrary { get; init; }

    public override GameState Apply(GameState state) => state with { LastReveal = Reveal };
}

public sealed record CardsLookedAt(ImmutableList<PlayerId> Viewers, ImmutableList<RevealedCard> Cards) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        LastLooks = state.LastLooks.SetItems(Viewers.Select(viewer =>
            new KeyValuePair<PlayerId, CardReveal>(viewer, new CardReveal(viewer, Cards)))),
    };
}
