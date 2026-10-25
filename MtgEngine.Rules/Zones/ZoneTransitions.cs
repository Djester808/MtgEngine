using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

public enum ZonePlacement { Default, Top, Bottom }

/// <summary>Trusted state operation, not a player command or authorization to execute a rules action.</summary>
public sealed record ZoneMove(ObjectId Object, ZoneKey Destination, ZonePlacement Placement = ZonePlacement.Default,
    PlayerId? Controller = null, bool FaceDown = false);

/// <summary>Server-only result. The old/new identity link must never be sent to viewers.</summary>
public sealed record ZoneMoveResult(GameState State, ObjectId Previous, ObjectId Current, bool Changed);

/// <summary>Basic physical-card movement. No replacements, triggers, casting or resolution.</summary>
public static class ZoneTransitions
{
    public static ZoneMoveResult Move(GameState state, ZoneMove move)
    {
        state.Validate();
        ZoneTopology.ValidateKey(state, move.Destination);
        if (!Enum.IsDefined(move.Placement))
            throw new ArgumentOutOfRangeException(nameof(move));
        if (!state.Objects.TryGetValue(move.Object, out var old))
            throw new ArgumentException("The object is no longer current.", nameof(move));
        if (old.CardInstanceId is null || old.IsToken || old.IsCopy)
            throw new NotSupportedException("Basic movement supports physical cards only.");
        var destination = ZoneTopology.IsPersonal(move.Destination.Zone)
            ? ZoneKey.Of(move.Destination.Zone, old.Owner) : move.Destination;
        var controlled = destination.Zone is Zone.Battlefield or Zone.Stack;
        if (move.Controller is { } requested)
        {
            _ = state.Player(requested);
            if (!controlled)
                throw new ArgumentException("Only battlefield and stack entries accept a controller.", nameof(move));
        }
        if (move.FaceDown && destination.Zone != Zone.Exile)
            throw new NotSupportedException("Basic movement only supports face-down entry into exile.");
        if (move.Placement != ZonePlacement.Default && destination.Zone is not (Zone.Library or Zone.Graveyard or Zone.Stack))
            throw new ArgumentException("Top and bottom require an ordered zone.", nameof(move));
        if (old.Zone == destination && destination.Zone is not (Zone.Exile or Zone.Command))
            throw new ArgumentException("Use a distinct destination; movement does not reorder a zone or change control.", nameof(move));
        // CR 400.4: these prohibited transitions leave the original object untouched.
        var types = old.Definition.GetFace(0).Types.Types;
        const CardType commandOnly = CardType.Conspiracy | CardType.Phenomenon | CardType.Plane | CardType.Scheme | CardType.Vanguard;
        if ((destination.Zone == Zone.Battlefield && (types & (CardType.Instant | CardType.Sorcery)) != 0)
            || (old.Zone.Zone == Zone.Command && destination.Zone != Zone.Command && (types & commandOnly) != 0))
            return new(state, old.Id, old.Id, false);

        var (id, timestamp) = Allocate(state);
        var controller = controlled ? move.Controller
            ?? (old.Zone.Zone == Zone.Stack && destination.Zone == Zone.Battlefield ? old.Controller : old.Owner) : old.Owner;
        var card = new GameObject(id, old.Definition, 0, old.Owner, controller, destination, timestamp)
        {
            CardInstanceId = old.CardInstanceId,
            DeckSelection = old.DeckSelection,
            IsCommander = old.IsCommander,
            FaceDown = move.FaceDown,
        };
        var zones = state.Zones.SetItem(old.Zone, state.In(old.Zone).Remove(old.Id));
        var members = zones.TryGetValue(destination, out var current) ? current : [];
        var top = move.Placement != ZonePlacement.Bottom;
        var first = destination.Zone switch
        {
            Zone.Library or Zone.Graveyard => top,
            Zone.Stack => !top,
            _ => false,
        };
        zones = zones.SetItem(destination, first ? members.Insert(0, id) : members.Add(id));
        var objects = state.Objects.Remove(old.Id).Add(id, card);
        // Attachments do not follow a new rules object. Any resulting state-based action is future work.
        foreach (var attached in objects.Values.Where(obj => obj.AttachedTo == old.Id))
            objects = objects.SetItem(attached.Id, attached with { AttachedTo = null });
        var result = state with
        {
            Objects = objects,
            Zones = zones,
            LastObjectId = id.Value,
            LastTimestamp = timestamp,
            ExileLookPermissions = state.ExileLookPermissions.Remove(old.Id),
            // Legacy inspection snapshots are not authority in this milestone.
            LibraryInspections = state.LibraryInspections.Clear(),
        };
        result.Validate();
        return new(result, old.Id, id, true);
    }

    /// <summary>Changes stored battlefield control without moving or recreating the object.</summary>
    public static GameState ChangeController(GameState state, ObjectId id, PlayerId controller)
    {
        state.Validate();
        _ = state.Player(controller);
        if (!state.Objects.TryGetValue(id, out var obj) || obj.Zone.Zone != Zone.Battlefield)
            throw new ArgumentException("Expected a current battlefield object.", nameof(id));
        if (obj.Controller == controller)
            return state;
        return state with { Objects = state.Objects.SetItem(id, obj with { Controller = controller, SummoningSick = true }) };
    }

    /// <summary>Records an externally authorized look under CR 406.3; this is not a client permission request.</summary>
    public static GameState AllowExileLook(GameState state, ObjectId id, PlayerId viewer)
    {
        state.Validate();
        _ = state.Player(viewer);
        if (!state.Objects.TryGetValue(id, out var obj) || obj.Zone.Zone != Zone.Exile || !obj.FaceDown)
            throw new ArgumentException("Expected a current face-down exiled object.", nameof(id));
        var viewers = state.ExileLookPermissions.GetValueOrDefault(id, ImmutableHashSet<PlayerId>.Empty);
        return state with { ExileLookPermissions = state.ExileLookPermissions.SetItem(id, viewers.Add(viewer)) };
    }

    internal static (ObjectId Id, int Timestamp) Allocate(GameState state)
    {
        var id = state.Objects.Values.Select(o => Math.Max(o.Id.Value, o.CardInstanceId?.Value ?? 0))
            .Append(state.LastObjectId).Max();
        var timestamp = state.Objects.Values.Select(o => o.Timestamp).Append(state.LastTimestamp).Max();
        return (new ObjectId(checked(id + 1)), checked(timestamp + 1));
    }
}
