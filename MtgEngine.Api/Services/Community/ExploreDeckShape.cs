using MtgEngine.Api.Dtos;
namespace MtgEngine.Api.Services.Community;

internal static class ExploreDeckShape
{
    public static ExploreChart[] Read(ExploreSection[] sections, bool deck)
    {
        var charts = new List<ExploreChart>();
        var entries = sections.SelectMany(s => s.Cards).Where(e => e.Card is not null).DistinctBy(e => e.Card!.OracleId).ToArray();
        var bands = entries.GroupBy(e => e.Card!.Prices?.Usd switch { null => "Unpriced", < 1 => "Under $1", < 5 => "$1–5", < 20 => "$5–20", _ => "$20+" }).Select(g => new ExploreChartPoint(g.Key, g.Count())).ToArray();
        if (bands.Length > 0)
            charts.Add(new("Card price bands", bands));
        if (deck)
        {
            var pips = new Dictionary<string, int>();
            var curve = new Dictionary<string, int>();
            foreach (var entry in entries)
            {
                var card = entry.Card!;
                var quantity = entry.Quantity;
                if (!card.CardTypes.Contains(CardTypeDto.Land))
                { var bucket = card.ManaValue.ToString(System.Globalization.CultureInfo.InvariantCulture); curve[bucket] = curve.GetValueOrDefault(bucket) + quantity; }
                var cost = card.ManaCost ?? "";
                var start = -1;
                for (int i = 0; i < cost.Length; i++)
                {
                    if (cost[i] == '{')
                    { start = i + 1; continue; }
                    if (cost[i] != '}' || start < 0)
                        continue;
                    foreach (var color in cost[start..i].Where(c => "WUBRG".Contains(c)).Distinct())
                    { var key = color.ToString(); pips[key] = pips.GetValueOrDefault(key) + quantity; }
                    start = -1;
                }
            }
            if (pips.Count > 0)
                charts.Add(new("Average deck mana pips", [.. pips.Select(p => new ExploreChartPoint(p.Key, p.Value))]));
            if (curve.Count > 0)
                charts.Add(new("Average deck mana curve", [.. curve.OrderBy(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture)).Select(p => new ExploreChartPoint(p.Key, p.Value))]));
        }
        return [.. charts];
    }
}
