using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// The single shared battlefield at an immutable game snapshot. Membership includes
/// phased-out objects; ActiveMembers excludes them without moving or recreating them.
/// Enumeration is storage order, not layout or a top/bottom ordering permission.
/// Server-side access does not grant disclosure: transport callers use ViewProjector.
/// Movement and identity remain the responsibility of IZoneSystem.
/// </summary>
[Cards.RuleRef("403")]
public sealed class Battlefield
{
    private readonly GameState _state;

    public Battlefield(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public ZoneKey Key => ZoneKey.Shared(Cards.Zone.Battlefield);
    public ImmutableList<ObjectId> Members => _state.In(Key);
    public int Count => Members.Count;
    public ImmutableList<ObjectId> ActiveMembers => Find(obj => !obj.PhasedOut);

    public bool Contains(ObjectId id) => Members.Contains(id);
    public GameObject? Get(ObjectId id) => Contains(id) ? _state.Objects[id] : null;

    /// <summary>Search stored members, including phased-out objects; no rules-filter or inspection permission is implied.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Members.Where(id => matches(_state.Objects[id]))];
    }

    public ImmutableList<ObjectId> OwnedBy(PlayerId owner) => Find(obj => obj.Owner == owner);

    /// <summary>Group stored members by evaluated control, never initial control, owner or layout.</summary>
    public ImmutableList<ObjectId> ControlledBy(PlayerId controller, ICharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);
        return Find(obj => characteristics.ControllerOf(_state, obj.Id) == controller);
    }
}
