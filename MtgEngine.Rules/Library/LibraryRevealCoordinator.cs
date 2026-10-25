using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Records continuous top-card disclosure transitions without treating them as zone moves.</summary>
public sealed class LibraryRevealCoordinator(ICharacteristics characteristics)
{
    public Outcome Reconcile(GameState state)
    {
        var events = ImmutableList<GameEvent>.Empty;
        var tops = ImmutableDictionary<PlayerId, ObjectId>.Empty;
        foreach (var player in state.Players)
        {
            var library = state.In(ZoneKey.Of(Zone.Library, player.Id));
            if (!library.IsEmpty && LibraryDisclosurePolicy.TopAvailable(state, player.Id)
                && characteristics.PlayerRestrictions(state, player.Id).TopCardRevealed)
                tops = tops.Add(player.Id, library[0]);
        }
        var hidden = state.PreviouslyRevealedLibraryCards
            .Union(state.RevealedLibraryTops.Values.Except(tops.Values))
            .Where(id => state.Objects.TryGetValue(id, out var card) && card.Zone.Zone == Zone.Library)
            .ToImmutableHashSet();
        foreach (var (player, id) in tops)
        {
            if (!hidden.Contains(id))
                continue;
            var changed = Reidentify(state, id);
            state = changed.Apply(state);
            events = events.Add(changed);
            tops = tops.SetItem(player, changed.Current.Id);
            hidden = hidden.Remove(id);
        }
        if (!state.RevealedLibraryTops.OrderBy(pair => pair.Key.Value).SequenceEqual(tops.OrderBy(pair => pair.Key.Value))
            || !state.PreviouslyRevealedLibraryCards.SetEquals(hidden))
        {
            var tracked = new LibraryRevealTrackingChanged(tops, hidden);
            state = tracked.Apply(state);
            events = events.Add(tracked);
        }
        return new Outcome(state, events);
    }

    internal static GameState EndReorderedDisclosures(GameState state, ZoneKey zone)
    {
        foreach (var id in state.RevealedLibraryTops.Values.Where(id => state.In(zone).Contains(id)).ToList())
            state = Reidentify(state, id).Apply(state);
        return state;
    }

    private static LibraryCardReidentified Reidentify(GameState state, ObjectId id)
    {
        var old = state.Objects[id];
        return new LibraryCardReidentified(id, new GameObject(new ObjectId(state.NextObjectId), old.Definition,
            old.FaceIndex, old.Owner, old.Owner, old.Zone, state.NextTimestamp)
        { CardInstanceId = old.CardInstanceId, DeckSelection = old.DeckSelection, IsCommander = old.IsCommander });
    }
}

public sealed record LibraryRevealTrackingChanged(ImmutableDictionary<PlayerId, ObjectId> Tops,
    ImmutableHashSet<ObjectId> Hidden) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    { RevealedLibraryTops = Tops, PreviouslyRevealedLibraryCards = Hidden };
}

public sealed record LibraryCardReidentified(ObjectId Previous, GameObject Current) : GameEvent
{
    public override GameState Apply(GameState state) => state with
    {
        Objects = state.Objects.Remove(Previous).SetItem(Current.Id, Current),
        Zones = state.Zones.SetItem(Current.Zone, state.In(Current.Zone).Replace(Previous, Current.Id)),
        NextObjectId = Math.Max(state.NextObjectId, Current.Id.Value + 1),
        NextTimestamp = Math.Max(state.NextTimestamp, Current.Timestamp + 1),
        RevealedLibraryTops = state.RevealedLibraryTops.RemoveRange(state.RevealedLibraryTops
            .Where(pair => pair.Value == Previous).Select(pair => pair.Key)),
        PreviouslyRevealedLibraryCards = state.PreviouslyRevealedLibraryCards.Remove(Previous),
    };
}
