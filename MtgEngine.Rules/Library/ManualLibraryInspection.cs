using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Owns the lifetime of an explicitly authorized manual look at one's own library.</summary>
public static class ManualLibraryInspection
{
    private static string Scope(PlayerId player) => $"manual-library:{player.Value}";

    public static bool IsActive(GameState state, PlayerId player) => state.LibraryInspections.ContainsKey(Scope(player));

    public static bool Contains(GameState state, PlayerId player, ObjectId card) =>
        state.LibraryInspections.TryGetValue(Scope(player), out var inspection)
        && inspection.Cards.Contains(card)
        && state.In(ZoneKey.Of(Cards.Zone.Library, player)).Contains(card);

    public static bool CanArrange(GameState state, PlayerId player,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ImmutableList<ObjectId>? order)
    {
        if (order is null || !state.LibraryInspections.TryGetValue(Scope(player), out var inspection))
            return false;
        var current = state.In(ZoneKey.Of(Cards.Zone.Library, player));
        var inspected = inspection.Cards.ToHashSet();
        return order.Count == current.Count && order.ToHashSet().SetEquals(current)
            && order.All(inspected.Contains);
    }

    public static Outcome Set(GameState state, PlayerId player, bool active)
    {
        var changed = new LibraryInspectionChanged(Scope(player), active
            ? new LibraryInspection(player, player, state.In(ZoneKey.Of(Cards.Zone.Library, player))) : null);
        return new Outcome(changed.Apply(state), [changed]);
    }

    public static GameState EndAfterReorder(GameState state, ZoneKey zone) =>
        zone.Zone == Cards.Zone.Library && zone.Player is { } owner
            ? state with { LibraryInspections = state.LibraryInspections.Remove(Scope(owner)) } : state;
}
