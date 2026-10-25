using MtgEngine.Cards;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.State;

/// <summary>Trusted setup primitives. No shuffle, opening hands, deck legality or turn progression.</summary>
public static class GameSetup
{
    public static GameState Create(IEnumerable<PlayerState> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        var seats = players.ToImmutableList();
        var state = new GameState
        {
            Players = seats,
            Objects = ImmutableDictionary<ObjectId, GameObject>.Empty,
            Zones = ZoneTopology.Keys(seats.Select(p => p.Id)).ToImmutableDictionary(k => k, _ => ImmutableList<ObjectId>.Empty),
            Turn = new TurnState(0, new PlayerId(0), Step.Untap, 0),
        };
        state.Validate();
        return state;
    }

    /// <summary>Appends one physical card to its owner's library or the shared command zone.</summary>
    public static GameState AddCard(GameState state, PlayerId owner, CardDefinition definition,
        StartingCard selection, bool commander = false)
    {
        state.Validate();
        _ = state.Player(owner);
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        var (id, timestamp) = ZoneTransitions.Allocate(state);
        var zone = commander ? ZoneKey.Shared(Zone.Command) : ZoneKey.Of(Zone.Library, owner);
        // Printed catalog records expose IReadOnlyList; take immutable copies at the state boundary.
        definition = definition with
        {
            Faces = definition.Faces.Select(face => face with
            {
                Types = face.Types with { Subtypes = face.Types.SubtypeList.ToImmutableList() },
            }).ToImmutableList(),
        };
        var card = GameObject.CreateCard(id, definition, selection, owner, zone, timestamp) with { IsCommander = commander };
        var result = state with
        {
            Objects = state.Objects.Add(id, card),
            Zones = state.Zones.SetItem(zone, state.In(zone).Add(id)),
            LastObjectId = id.Value,
            LastTimestamp = timestamp,
        };
        result.Validate();
        return result;
    }
}
