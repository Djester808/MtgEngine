using System.Text.Json;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services.Community;

internal static class ExploreCharts
{
    public static ExploreChart[] Read(JsonElement root)
    {
        var charts = new List<ExploreChart>();
        Map(root, "bracket_counts", "Deck brackets", charts);
        Map(root, "budget_counts", "Deck budgets", charts);
        Timeline(root, "savedate_counts", "Decks over time", charts, false);
        if (root.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Object)
        {
            Map(panels, "mana_curve", "Mana curve", charts);
            Timeline(panels, "rank_over_time", "Rank over time", charts, true);
            if (panels.TryGetProperty("combocounts", out var combos) && combos.ValueKind == JsonValueKind.Array)
                charts.Add(new("Combos", [new("Listed combos", combos.GetArrayLength())]));
            if (panels.TryGetProperty("piechart", out var pie) && pie.ValueKind == JsonValueKind.Object
                && pie.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var points = new List<ExploreChartPoint>();
                foreach (var item in content.EnumerateArray().Take(50))
                    if (EdhrecPages.Number(item, "value") is { } number && double.IsFinite(number) && number >= 0)
                        points.Add(new(EdhrecPages.Text(item, "label"), number));
                if (points.Count > 0)
                    charts.Add(new("Card types", [.. points]));
            }
        }
        return [.. charts];
    }
    private static void Timeline(JsonElement root, string key, string title, List<ExploreChart> charts, bool rank)
    {
        if (!root.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Object)
            return;
        var dated = new List<(DateTimeOffset Date, double Value)>();
        foreach (var item in values.EnumerateObject())
        {
            if (!DateTimeOffset.TryParse(item.Name, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                continue;
            var value = rank && item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("rank", out var recorded) ? recorded : item.Value;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0)
                dated.Add((at, n));
        }
        var points = dated.OrderBy(p => p.Date).TakeLast(120).Select(p => new ExploreChartPoint(p.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), p.Value)).ToArray();
        if (points.Length == 0)
            return;
        charts.Add(new(title, points));
        if (!rank)
            charts.Add(new("Decks by month", [.. dated.GroupBy(p => p.Date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)).OrderBy(g => g.Key).TakeLast(120).Select(g => new ExploreChartPoint(g.Key, g.Sum(p => p.Value)))]));
    }
    private static void Map(JsonElement root, string key, string title, List<ExploreChart> charts)
    {
        if (!root.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Object)
            return;
        var points = values.EnumerateObject().Take(50)
            .Where(v => v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0)
            .Select(v => new ExploreChartPoint(v.Name, v.Value.GetDouble())).ToArray();
        if (points.Length > 0)
            charts.Add(new(title, points));
    }
}
