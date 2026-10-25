using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// The shared exile zone at one immutable game snapshot (CR 400.1, 406).
/// Members retain their individual owners. Enumeration order is storage order,
/// not a top/bottom or permission to rearrange cards. Movement belongs to IZoneSystem.
/// These are authoritative server objects; transport callers must use ViewProjector,
/// which withholds face-down identities even from their owners.
/// </summary>
[Cards.RuleRef("406")]
public sealed class ExileZone
{
    private readonly GameState _state;

    public ExileZone(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public ZoneKey Key => ZoneKey.Shared(Cards.Zone.Exile);
    public ImmutableList<ObjectId> Members => _state.In(Key);
    public int Count => Members.Count;

    public bool Contains(ObjectId id) => Members.Contains(id);

    /// <summary>Server-side lookup only; this does not grant a player permission to inspect a card.</summary>
    public GameObject? Get(ObjectId id) => Contains(id) ? _state.Objects[id] : null;

    /// <summary>Search current members without moving them or granting inspection permissions.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Members.Where(id => matches(_state.Objects[id]))];
    }
}
