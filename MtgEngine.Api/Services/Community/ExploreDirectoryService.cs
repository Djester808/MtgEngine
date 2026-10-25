using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
namespace MtgEngine.Api.Services.Community;

public sealed record ExploreDirectoryOptions(string Q = "", string? Identity = null, string Mode = "within", string Window = "all", string Sort = "decks", string Dir = "desc", int Page = 1, int PageSize = 40);
public sealed class ExploreDirectoryService(IScryfallService cards, ExploreService explore)
{
    public async Task<ExploreDirectory> Search(ExploreDirectoryOptions options, CancellationToken ct)
    {
        Validate(options);
        var commanders = await cards.SearchCommandersAsync(options.Q, int.MaxValue);
        var popular = await explore.Directory(ct);
        var counts = popular.Commanders.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var entries = commanders.Select(c => { counts.TryGetValue(c.Name, out var counted); return new ExploreEntry(c.Name, DomainMapper.ToDto(c), Decks: options.Window == "all" ? counted?.Decks : null, Kind: "commander", Rank: c.EdhrecRank); });
        return Run(entries, options, popular.FetchedAt);
    }
    public static void Validate(ExploreDirectoryOptions o)
    {
        if (o.Q.Length > 200 || o.Identity is not null && (o.Identity.Length > 5 || o.Identity.ToUpperInvariant().Any(c => !"CWUBRG".Contains(c))) || o.Mode is not ("within" or "exact") || o.Window is not ("all" or "week" or "month" or "year") || o.Sort is not ("name" or "decks" or "rank") || o.Dir is not ("asc" or "desc") || o.Page < 1 || o.PageSize is < 1 or > 100)
            throw new InvalidRequestException("Choose valid commander directory filters.");
    }
    public static ExploreDirectory Run(IEnumerable<ExploreEntry> source, ExploreDirectoryOptions o, DateTimeOffset fetched)
    {
        Validate(o);
        var identity = (o.Identity ?? "").ToUpperInvariant().Where(c => c != 'C').ToHashSet();
        var rows = source.Where(e => e.Name.Contains(o.Q, StringComparison.OrdinalIgnoreCase) && (o.Identity is null || e.Card is { } c && c.ColorIdentity.All(color => identity.Contains(color.ToString()[0])) && (o.Mode != "exact" || c.ColorIdentity.Length == identity.Count))).ToArray();
        var dir = o.Dir == "asc" ? 1 : -1;
        Array.Sort(rows, (a, b) =>
        {
            if (o.Sort == "name")
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) * dir;
            int? x = o.Sort == "rank" ? a.Rank : a.Decks, y = o.Sort == "rank" ? b.Rank : b.Decks;
            var order = x is null ? y is null ? 0 : 1 : y is null ? -1 : x.Value.CompareTo(y.Value) * dir;
            return order != 0 ? order : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        var page = Math.Min(o.Page, Math.Max(1, (int)Math.Ceiling((double)rows.Length / o.PageSize)));
        return new(fetched, [.. rows.Skip((page - 1) * o.PageSize).Take(o.PageSize)], rows.Length, page, o.PageSize, o.Window);
    }
}
