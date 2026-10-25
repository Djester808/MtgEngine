using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// An owner's hand at one immutable game snapshot. Enumeration preserves stored membership
/// order for stable presentation; it has no top, bottom, or rules-significant ordering.
/// These are authoritative server objects, not a disclosure permission. Transport callers
/// must use ViewProjector, which exposes only the viewer's hand and other hands' counts.
/// Drawing and movement remain responsibilities of the existing library and zone services.
/// </summary>
[Cards.RuleRef("402")]
public sealed class Hand
{
    private readonly GameState _state;

    public Hand(GameState state, PlayerId owner)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(owner.Value);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(owner.Value, state.Players.Count);
        _state = state;
        Owner = owner;
    }

    public PlayerId Owner { get; }
    public ZoneKey Key => ZoneKey.Of(Cards.Zone.Hand, Owner);
    public ImmutableList<ObjectId> Members => _state.In(Key);
    public int Count => Members.Count;

    public bool Contains(ObjectId id) => Members.Contains(id);

    /// <summary>Finds members without changing authoritative state or presentation order.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Members.Where(id => matches(_state.Objects[id]))];
    }
}
