using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// A viewer's disclosure snapshot, not a rules zone. Historical cards grant no current object access.
/// Active references come solely from existing library inspections and current zone membership.
/// </summary>
public sealed class RevealArea
{
    public RevealArea(GameState state, PlayerId viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        Viewer = viewer;
        PublicReveal = state.LastReveal;
        PrivateLook = state.LastLooks.GetValueOrDefault(viewer);
        Inspections = state.LibraryInspections.Where(pair => pair.Value.Viewer == viewer).ToImmutableDictionary();
        ActiveCards = [.. Inspections.Values.SelectMany(inspection => inspection.Cards.Where(id =>
            state.Objects.TryGetValue(id, out var card)
            && card.Zone == ZoneKey.Of(Zone.Library, inspection.Owner)
            && state.In(card.Zone).Contains(id))).Distinct()];
    }

    public PlayerId Viewer { get; }
    public CardReveal? PublicReveal { get; }
    public CardReveal? PrivateLook { get; }
    /// <summary>Existing scope keys and provenance; entries may outlive individual card references.</summary>
    public ImmutableDictionary<string, LibraryInspection> Inspections { get; }
    public ImmutableList<ObjectId> ActiveCards { get; }
    public int Count => ActiveCards.Count;
    public bool Contains(ObjectId id) => ActiveCards.Contains(id);
}
