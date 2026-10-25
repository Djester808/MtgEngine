using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Views;

/// <summary>Minimal disclosure boundary for state/zone operations, independent of legacy gameplay DTOs.</summary>
public sealed record ZoneStateView(PlayerId Viewer, ImmutableList<ZonePlayerView> Players, ImmutableList<ZoneView> Zones)
{
    public static ZoneStateView For(GameState state, PlayerId viewer)
    {
        state.Validate();
        _ = state.Player(viewer);
        var zones = ZoneTopology.Keys(state.Players.Select(p => p.Id)).Select(key =>
        {
            var members = state.In(key);
            // Neither placeholders, positions nor object identifiers escape a hidden pile.
            var hidden = key.Zone == Zone.Library || (key.Zone == Zone.Hand && key.Player != viewer);
            return new ZoneView(key, members.Count, hidden ? [] : members.Select(id => Project(state, state.Objects[id], viewer)).ToImmutableList());
        }).ToImmutableList();
        return new(viewer, state.Players.Select(p => new ZonePlayerView(p.Id, p.Name, p.Life)).ToImmutableList(), zones);
    }

    private static ZoneObjectView Project(GameState state, GameObject obj, PlayerId viewer)
    {
        var controlled = obj.Zone.Zone is Zone.Battlefield or Zone.Stack;
        var canSee = !obj.FaceDown || (controlled && obj.Controller == viewer)
            || (obj.Zone.Zone == Zone.Exile && state.ExileLookPermissions.TryGetValue(obj.Id, out var viewers) && viewers.Contains(viewer));
        var printed = canSee ? new ZonePrintedCard(obj.Definition.OracleId, obj.Face.Name,
            obj.DeckSelection?.PrintingId, obj.DeckSelection?.Finish, obj.Face with
            {
                Types = obj.Face.Types with { Subtypes = obj.Face.Types.SubtypeList.ToImmutableList() },
            }) : null;
        return new(obj.Id, obj.Owner, controlled ? obj.Controller : null, obj.FaceDown, obj.Tapped, obj.PhasedOut, printed);
    }
}

public sealed record ZonePlayerView(PlayerId Id, string Name, int Life);
/// <summary>Visible members preserve authoritative order. Hidden zones contain only a count.</summary>
public sealed record ZoneView(ZoneKey Key, int Count, ImmutableList<ZoneObjectView> Objects);
/// <summary>No physical identity, timestamps, old/new links, choices or arbitrary state dictionaries.</summary>
public sealed record ZoneObjectView(ObjectId Id, PlayerId Owner, PlayerId? Controller, bool FaceDown,
    bool Tapped, bool PhasedOut, ZonePrintedCard? Printed);
/// <summary>Printed face data, not evaluated characteristics or executable abilities.</summary>
public sealed record ZonePrintedCard(string OracleId, string Name, string? PrintingId, CardFinish? Finish, Face Face);
