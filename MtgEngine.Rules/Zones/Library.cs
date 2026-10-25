using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>Read-only, top-first library membership. Does not grant disclosure or implement draw/search/shuffle.</summary>
public sealed class Library
{
    private readonly GameState _state;
    public Library(GameState state, PlayerId owner)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = state.Player(owner);
        _state = state;
        Owner = owner;
    }
    public PlayerId Owner { get; }
    public ZoneKey Key => ZoneKey.Of(Cards.Zone.Library, Owner);
    public ImmutableList<ObjectId> Order => _state.In(Key);
    public int Count => Order.Count;
    public GameObject? Top => Order.IsEmpty ? null : _state.Objects[Order[0]];
    public bool Contains(ObjectId id) => Order.Contains(id);
}
