using MtgEngine.Api.Dtos;
namespace MtgEngine.Api.Services;

public static class CollectionReadSummary
{
    public static (CollectionValuation, CollectionInsightDto[]) Build(CollectionCardDto[] rows)
    {
        var maps = Enumerable.Range(0, 10).Select(_ => new Dictionary<string, decimal>()).ToArray();
        decimal value = 0;
        int copies = 0, priced = 0;
        void Add(int index, string label, decimal amount)
        {
            if (amount > 0 || (index == 8 && amount != 0))
                maps[index][label] = maps[index].GetValueOrDefault(label) + amount;
        }
        foreach (var row in rows)
        {
            var count = CollectionReadService.Copies(row);
            if (count <= 0)
                continue;
            copies += count;
            var card = row.CardDetails;
            Add(0, card is null ? "Unknown" : card.ManaValue >= 14 ? "14+" : card.ManaValue.ToString(), count);
            var colors = card?.Colors ?? card?.ManaCost.Where(c => "WUBRG".Contains(c)).Distinct().Select(c => Enum.Parse<ManaColorDto>(c.ToString())).ToArray() ?? [];
            Add(1, colors.Length > 1 ? "Multicolor" : colors.Length == 0 ? "Colorless" : colors[0].ToString(), count);
            foreach (var type in card?.CardTypes.Select(t => t.ToString()).DefaultIfEmpty("Unknown") ?? ["Unknown"])
                Add(2, type, count);
            Add(3, card?.Rarity ?? "Unknown", count);
            Add(4, card?.SetCode?.ToUpperInvariant() ?? "Unknown", count);
            Add(6, card?.ColorIdentity.Length > 0 ? string.Concat(card.ColorIdentity) : "Colorless", count);
            foreach (var finish in CollectionFinishData.Finishes)
            {
                var qty = CollectionReadService.Quantity(row, finish);
                Add(5, finish == "nonfoil" ? "Nonfoil" : finish == "foil" ? "Foil" : "Etched", qty);
                var price = finish == "foil" ? card?.Prices?.UsdFoil : finish == "etched" ? card?.Prices?.UsdEtched : card?.Prices?.Usd;
                row.FinishData.TryGetValue(finish, out var data);
                var original = data is not null ? data.PriceAtAdd : finish == "foil" ? row.PriceUsdFoilAtAdd : finish == "etched" ? row.PriceUsdEtchedAtAdd : row.PriceUsdAtAdd;
                var name = card?.Name ?? row.OracleId;
                if (price is { } market)
                {
                    value += qty * market;
                    priced += qty;
                    Add(7, name, qty * market);
                    if (original is { } baseline)
                        Add(8, name, qty * (market - baseline));
                }
                Add(9, (data?.AddedAt ?? row.AddedAt).ToString("yyyy-MM"), qty);
            }
        }
        string[] titles = ["Mana curve", "Colors", "Types", "Rarity", "Sets", "Finish", "Color identity", "Most valuable", "Movers since added", "Acquisitions"];
        var sections = maps.Select((map, i) =>
        {
            var points = map.Select(p => new CollectionInsightPoint(p.Key, p.Value)).ToArray();
            points = i == 0 ? points.OrderBy(p => int.TryParse(p.Label.TrimEnd('+'), out var n) ? n : int.MaxValue).ToArray()
                : i == 9 ? points.OrderBy(p => p.Label).TakeLast(12).ToArray()
                : points.OrderByDescending(p => Math.Abs(p.Value)).ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            if (i != 0 && i != 9 && points.Length > 10)
            {
                var tail = points.Skip(10).Sum(p => p.Value);
                points = points.Take(10).ToArray();
                if (i < 7)
                    points = [.. points, new("Other", tail)];
            }
            return new CollectionInsightDto(titles[i], i is 7 or 8, points, Math.Max(1, points.Select(p => Math.Abs(p.Value)).DefaultIfEmpty().Max()));
        }).ToArray();
        return (new(value, priced, copies), sections);
    }
}
