using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// An owner's public, top-first discard pile at one immutable game snapshot.
/// Membership and order come only from GameState; movement belongs to IZoneSystem.
/// These are authoritative server objects. Transport callers still use ViewProjector.
/// </summary>
[Cards.RuleRef("404")]
public sealed class Graveyard
{
    private readonly GameState _state;

    public Graveyard(GameState state, PlayerId owner)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(owner.Value);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(owner.Value, state.Players.Count);
        _state = state;
        Owner = owner;
    }

    public PlayerId Owner { get; }
    public ZoneKey Key => ZoneKey.Of(Cards.Zone.Graveyard, Owner);
    public ImmutableList<ObjectId> Order => _state.In(Key);
    public int Count => Order.Count;
    public GameObject? Top => Order.IsEmpty ? null : _state.Objects[Order[0]];

    public bool Contains(ObjectId id) => Order.Contains(id);

    /// <summary>Finds current members without moving them or changing the pile's order.</summary>
    public ImmutableList<ObjectId> Find(Func<GameObject, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. Order.Where(id => matches(_state.Objects[id]))];
    }
}
