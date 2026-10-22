using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Services;

public static class CollectionFinishData
{
    public static readonly string[] Finishes = ["nonfoil", "foil", "etched"];
    public static string Normalize(string value)
    {
        var finish = value.Trim().ToLowerInvariant();
        return Finishes.Contains(finish) ? finish : throw new InvalidRequestException("Choose nonfoil, foil or etched.");
    }
    public static Dictionary<string, CollectionFinishMetadata> All(CollectionCard row)
    {
        try
        { return (JsonSerializer.Deserialize<Dictionary<string, CollectionFinishMetadata>>(row.FinishMetadataJson) ?? []).Where(p => Finishes.Contains(p.Key) && p.Value is not null).ToDictionary(p => p.Key, p => p.Value); }
        catch (JsonException) { return []; }
    }
    public static int Quantity(CollectionCard row, string finish) => finish switch { "foil" => row.QuantityFoil, "etched" => row.QuantityEtched, _ => row.Quantity };
    public static void SetQuantity(CollectionCard row, string finish, int quantity)
    {
        if (finish == "foil")
            row.QuantityFoil = quantity;
        else if (finish == "etched")
            row.QuantityEtched = quantity;
        else
            row.Quantity = quantity;
    }
    public static CollectionFinishMetadata Read(CollectionCard row, string finish, Dictionary<string, CollectionFinishMetadata>? values = null)
    {
        values ??= All(row);
        return values.TryGetValue(finish, out var stored) ? stored : new(row.Condition, row.PurchasePrice, row.PurchaseCurrency,
            CollectionMetadataService.Tags(row.TagsJson), row.Notes, row.AddedAt,
            finish switch { "foil" => row.PriceUsdFoilAtAdd, "etched" => row.PriceUsdEtchedAtAdd, _ => row.PriceUsdAtAdd });
    }
    public static void Write(CollectionCard row, string finish, CollectionFinishMetadata value)
    {
        var values = All(row);
        values[finish] = value;
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void Initialize(CollectionCard row, int normalAdded, int foilAdded, int etchedAdded, CardDefinition? card, DateTime now)
    {
        var values = All(row);
        var changed = false;
        foreach (var (finish, added) in new[] { ("nonfoil", normalAdded), ("foil", foilAdded), ("etched", etchedAdded) })
        {
            if (added <= 0 || Quantity(row, finish) != added)
                continue;
            var previous = Read(row, finish, values);
            values[finish] = previous with { AddedAt = now, PriceAtAdd = finish switch { "foil" => card?.Prices.UsdFoil, "etched" => card?.Prices.UsdEtched, _ => card?.Prices.Usd } };
            changed = true;
        }
        if (changed)
            row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void ClearEmpty(CollectionCard row)
    {
        var values = All(row);
        foreach (var finish in Finishes.Where(f => Quantity(row, f) == 0))
            values.Remove(finish);
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void PreserveOwned(CollectionCard row)
    {
        var values = All(row);
        foreach (var finish in Finishes.Where(f => Quantity(row, f) > 0))
            values[finish] = Read(row, finish, values);
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void CopyInto(CollectionCard source, CollectionCard target, int normal, int foil, int etched)
    {
        PreserveOwned(target);
        var values = All(target);
        var sourceValues = All(source);
        foreach (var (finish, moved) in new[] { ("nonfoil", normal), ("foil", foil), ("etched", etched) })
            if (moved > 0 && Quantity(target, finish) == 0)
                values[finish] = Read(source, finish, sourceValues);
        target.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static string Subset(CollectionCard source, int normal, int foil, int etched)
    {
        var values = All(source);
        return JsonSerializer.Serialize(new[] { ("nonfoil", normal), ("foil", foil), ("etched", etched) }.Where(p => p.Item2 > 0).ToDictionary(p => p.Item1, p => Read(source, p.Item1, values)));
    }
    public static Dictionary<string, CollectionFinishSummary> Summaries(CollectionCard row)
    {
        var values = All(row);
        return Finishes.Where(f => Quantity(row, f) > 0).ToDictionary(f => f, f => { var value = Read(row, f, values); return new CollectionFinishSummary(value.Condition, value.Tags ?? [], value.AddedAt, value.PriceAtAdd); });
    }
    public static void UpdateSharedNotes(CollectionCard row, string? notes)
    {
        var values = All(row);
        foreach (var finish in Finishes.Where(f => Quantity(row, f) > 0))
            values[finish] = Read(row, finish, values) with { Notes = notes };
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void ApplyLegacy(CollectionCard row)
    {
        var values = All(row);
        foreach (var finish in Finishes.Where(f => Quantity(row, f) > 0))
        {
            var previous = Read(row, finish, values);
            values[finish] = previous with { Condition = row.Condition, PurchasePrice = row.PurchasePrice, Currency = row.PurchaseCurrency, Tags = CollectionMetadataService.Tags(row.TagsJson) };
        }
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
    public static void Bulk(CollectionCard row, string action, string? tag, string? condition)
    {
        var values = All(row);
        foreach (var finish in Finishes.Where(f => Quantity(row, f) > 0))
        {
            var value = Read(row, finish, values);
            values[finish] = action switch { "condition" => value with { Condition = condition }, "tag" => value with { Tags = [.. (value.Tags ?? []).Append(tag!).Distinct(StringComparer.OrdinalIgnoreCase)] }, "untag" => value with { Tags = [.. (value.Tags ?? []).Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))] }, _ => value };
        }
        if (values.Values.Any(v => v.Tags?.Length > 20))
            throw new InvalidRequestException("Use at most 20 tags per finish.");
        row.FinishMetadataJson = JsonSerializer.Serialize(values);
    }
}
