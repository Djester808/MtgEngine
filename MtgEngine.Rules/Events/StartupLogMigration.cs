using System.Text.Json;
using System.Text.Json.Nodes;

namespace MtgEngine.Rules.Events;

/// <summary>One-way migration of legacy Oracle-only startup lists to typed deck selections.</summary>
internal static class StartupLogMigration
{
    public static string Upgrade(string json)
    {
        if (!json.Contains("\"deckOracleIds\"", StringComparison.Ordinal)
            && !json.Contains("\"commandZoneOracleIds\"", StringComparison.Ordinal))
            return json;
        if (JsonNode.Parse(json) is not JsonArray log)
            throw new JsonException("A game log must be an array.");
        foreach (var entry in log.OfType<JsonObject>())
        {
            if (entry["event"]?.GetValue<string>() != nameof(GameStarted) || entry["players"] is not JsonArray players)
                continue;
            foreach (var player in players.OfType<JsonObject>())
            {
                UpgradeCards(player, "deckOracleIds", "deckCards");
                UpgradeCards(player, "commandZoneOracleIds", "commandCards");
            }
        }
        return log.ToJsonString();
    }

    private static void UpgradeCards(JsonObject player, string previous, string current)
    {
        if (!player.TryGetPropertyValue(previous, out var value))
            return;
        if (player.ContainsKey(current) || value is not JsonArray ids)
            throw new JsonException("A startup card list is ambiguous or malformed.");
        var cards = new JsonArray();
        foreach (var id in ids)
            cards.Add(new JsonObject { ["oracleId"] = id?.GetValue<string>() ?? throw new JsonException("A card id is required.") });
        player.Remove(previous);
        player[current] = cards;
    }
}
