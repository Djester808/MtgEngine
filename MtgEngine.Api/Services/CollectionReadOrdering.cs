using MtgEngine.Api.Dtos;
namespace MtgEngine.Api.Services;

/// <summary>Section order is global, before page boundaries, rather than restarting a
/// name-sorted collection in each page's CMC/type columns.</summary>
public static class CollectionReadOrdering
{
    public static IEnumerable<CollectionCardDto> Group(IEnumerable<CollectionCardDto> rows, string group)
    {
        if (group == "none")
            return rows;
        string Name(CollectionCardDto c) => c.CardDetails?.Name ?? c.OracleId;
        bool Land(CollectionCardDto c) => c.CardDetails?.CardTypes.Contains(CardTypeDto.Land) is true;
        int Mana(CollectionCardDto c) => c.CardDetails?.ManaValue ?? 0;
        int TypeRank(CollectionCardDto c)
        {
            CardTypeDto[] order = [CardTypeDto.Creature, CardTypeDto.Planeswalker, CardTypeDto.Instant, CardTypeDto.Sorcery, CardTypeDto.Enchantment, CardTypeDto.Artifact, CardTypeDto.Land];
            var index = Array.FindIndex(order, t => c.CardDetails?.CardTypes.Contains(t) is true);
            return index < 0 ? order.Length : index;
        }
        string Color(CollectionCardDto c, bool identity)
        {
            var colors = identity ? c.CardDetails?.ColorIdentity ?? [] : (c.CardDetails?.ManaCost ?? "").Where(ch => "WUBRG".Contains(ch)).Distinct().Select(ch => Enum.Parse<ManaColorDto>(ch.ToString())).ToArray();
            return colors.Length > 1 ? "5" : colors.Length == 0 ? "6" : Array.IndexOf(new[] { ManaColorDto.W, ManaColorDto.U, ManaColorDto.B, ManaColorDto.R, ManaColorDto.G }, colors[0]).ToString();
        }
        var byName = rows.OrderBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id);
        return group switch
        {
            "name" => byName,
            "cmc" => rows.OrderBy(Land).ThenBy(Mana).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "type" => rows.OrderBy(TypeRank).ThenBy(Mana).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "creature-split" => rows.OrderBy(c => Land(c) ? 2 : c.CardDetails?.CardTypes.Contains(CardTypeDto.Creature) is true ? 0 : 1).ThenBy(Mana).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "color" or "color-identity" => rows.OrderBy(c => Color(c, group == "color-identity")).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "rarity" => rows.OrderBy(c => Array.IndexOf(new[] { "mythic", "rare", "uncommon", "common", "special", "bonus" }, c.CardDetails?.Rarity)).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "artist" => rows.OrderBy(c => c.CardDetails?.Artist ?? "Unknown", StringComparer.OrdinalIgnoreCase).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "set" => rows.OrderBy(c => c.CardDetails?.SetCode ?? "unknown", StringComparer.OrdinalIgnoreCase).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "subtype" => rows.OrderBy(c => c.CardDetails?.Subtypes.FirstOrDefault() ?? "Other", StringComparer.OrdinalIgnoreCase).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "month" => rows.OrderBy(c => c.AddedAt.ToString("yyyy-MM")).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            "price" => rows.OrderBy(PriceBucket).ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id),
            _ => byName
        };
    }
    private static string PriceBucket(CollectionCardDto c)
    {
        var price = c.Quantity > 0 ? c.CardDetails?.Prices?.Usd : c.CardDetails?.Prices?.UsdFoil;
        return price is null ? "Unpriced" : price < 1 ? "Under $1" : price < 5 ? "$1–$4.99" : price < 10 ? "$5–$9.99" : price < 25 ? "$10–$24.99" : "$25+";
    }
}
