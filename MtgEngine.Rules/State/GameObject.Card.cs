using MtgEngine.Cards;

namespace MtgEngine.Rules.State;

public sealed partial record GameObject
{
    /// <summary>
    /// Creates one selected deck card. The game supplies its unique object id; that id also
    /// seeds the persistent card identity. Zone movement owns subsequent object identities.
    /// This is server state: callers must apply a disclosure policy before transport.
    /// </summary>
    public static GameObject CreateCard(ObjectId id, CardDefinition definition, StartingCard selection,
        PlayerId owner, ZoneKey zone, int timestamp)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(selection);
        definition.Validate();
        selection.Validate();
        if (!string.Equals(definition.OracleId, selection.OracleId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The definition must match the selected card.", nameof(definition));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id.Value);
        ArgumentOutOfRangeException.ThrowIfNegative(owner.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestamp);
        if (zone.Zone is not (Cards.Zone.Library or Cards.Zone.Command)
            || (zone.Zone == Cards.Zone.Library ? zone.Player != owner : zone.Player is not null))
            throw new ArgumentException("A starting card belongs in its owner's library or the shared command zone.", nameof(zone));

        return new GameObject(id, definition, 0, owner, owner, zone, timestamp)
        {
            CardInstanceId = new CardInstanceId(id.Value),
            DeckSelection = selection,
        };
    }
}
