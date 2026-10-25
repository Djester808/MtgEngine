using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// The shared command zone at one immutable game snapshot. Objects retain individual
/// owners; membership does not imply commander designation or that an object is a card.
/// Enumeration is storage order, not a top/bottom contract. Movement belongs to IZoneSystem.
/// These are authoritative objects; transport callers still use ViewProjector.
/// </summary>
[Cards.RuleRef("408")]
public sealed class CommandZone
{
    private readonly GameState _state;

    public CommandZone(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public ZoneKey Key => ZoneKey.Shared(Cards.Zone.Command);
    public ImmutableList<ObjectId> Members => _state.In(Key);
    public int Count => Members.Count;

    public bool Contains(ObjectId id) => Members.Contains(id);

    public GameObject? Get(ObjectId id) => Contains(id) ? _state.Objects[id] : null;

    /// <summary>Search current members without moving them or changing commander designation.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Members.Where(id => matches(_state.Objects[id]))];
    }
}
