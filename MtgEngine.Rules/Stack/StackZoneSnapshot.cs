using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Stack;

/// <summary>
/// Read-only membership of the shared stack at one immutable game state. Order is
/// bottom-first; Top is the last-added object. Members include spells, abilities and
/// copies, without requiring physical card identity. Transport callers apply a future disclosure policy
/// to apply disclosure rules; these authoritative objects are not a public view.
/// </summary>
[Cards.RuleRef("405")]
public sealed class StackZoneSnapshot
{
    private readonly GameState _state;

    public StackZoneSnapshot(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public ZoneKey Key => ZoneKey.Shared(Cards.Zone.Stack);
    public ImmutableList<ObjectId> Order => _state.In(Key);
    public int Count => Order.Count;
    public GameObject? Top => Order.IsEmpty ? null : _state.Objects[Order[^1]];

    public bool Contains(ObjectId id) => Order.Contains(id);

    /// <summary>Returns only a current stack member, never an object in another zone.</summary>
    public GameObject? Lookup(ObjectId id) => Contains(id) ? _state.Objects[id] : null;

    /// <summary>Matches in bottom-first order without moving or resolving anything.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Order.Where(id => matches(_state.Objects[id]))];
    }
}
