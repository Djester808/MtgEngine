using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

public static class ZoneTopology
{
    public static bool IsPersonal(Zone zone) => zone is Zone.Library or Zone.Hand or Zone.Graveyard;

    public static IEnumerable<ZoneKey> Keys(IEnumerable<PlayerId> players) =>
        players.SelectMany(player => new[] { Zone.Library, Zone.Hand, Zone.Graveyard }.Select(zone => ZoneKey.Of(zone, player)))
            .Concat(new[] { Zone.Battlefield, Zone.Stack, Zone.Exile, Zone.Command }.Select(ZoneKey.Shared));

    internal static void ValidateKey(GameState state, ZoneKey key)
    {
        if (!Enum.IsDefined(key.Zone) || key.Zone == Zone.OutsideTheGame || IsPersonal(key.Zone) != key.Player.HasValue)
            throw new ArgumentException("Expected a personal or shared game zone with matching topology.", nameof(key));
        if (key.Player is { } player)
            _ = state.Player(player);
    }
}
