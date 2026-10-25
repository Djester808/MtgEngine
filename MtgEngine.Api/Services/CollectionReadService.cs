using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

/// <summary>Owner-only read model. Filtering and ordering precede paging; only the requested
/// page and compact whole-collection aggregates cross the wire.</summary>
public sealed class CollectionReadService(MtgEngineDbContext db, ICollectionReader collections, ICardLookup lookup,
    CollectionReadCache? cache = null)
{
    public async Task<CollectionDetailDto?> PageAsync(Guid id, string owner, CollectionPageQuery query, CancellationToken ct)
    {
        var revision = await db.Collections.AsNoTracking().Where(c => c.Id == id && c.UserId == owner && !c.IsDeck)
            .Select(c => (DateTime?)c.UpdatedAt).FirstOrDefaultAsync(ct);
        if (revision is null)
            return null;
        var key = (owner, id, revision.Value, lookup.CatalogVersion);
        if (cache?.Get(key) is { } cached)
            return Project(cached, query, ct);
        var collection = await collections.GetCollectionAsync(id, owner, ct);
        ct.ThrowIfCancellationRequested();
        if (collection is not null)
            cache?.Set(key, collection);
        return collection is null ? null : Project(collection, query, ct);
    }

    public async Task<CollectionCardDto[]> LookupAsync(Guid id, string owner, string[] oracleIds, CancellationToken ct)
    {
        if (oracleIds.Length > 100)
            throw new InvalidRequestException("Look up at most 100 cards at once.");
        if (!await db.Collections.AsNoTracking().AnyAsync(c => c.Id == id && c.UserId == owner && !c.IsDeck, ct))
            throw new KeyNotFoundException();
        var rows = await db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == id && oracleIds.Contains(c.OracleId)).ToArrayAsync(ct);
        var result = new List<CollectionCardDto>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(MtgEngine.Api.Mapping.DomainMapper.ToDto(row, await lookup.ResolveForEntryAsync(row, ct)));
        }
        return result.ToArray();
    }

    public static CollectionDetailDto Project(CollectionDetailDto collection, CollectionPageQuery query, CancellationToken ct = default)
    {
        var matcher = new OwnedNameMatcher(query.Query, query.MatchCase, query.MatchWord, query.Regex);
        var matching = collection.Cards.Where(row => { ct.ThrowIfCancellationRequested(); return Matches(row, query, matcher); }).ToArray();
        var ordered = CollectionReadOrdering.Group(Order(matching, query), query.Group).ToArray();
        var size = Math.Clamp(query.PageSize, 20, 200);
        var units = query.GroupSameCard ? ordered.GroupBy(c => c.OracleId).Select(g => g.ToArray()).ToArray() : ordered.Select(c => new[] { c }).ToArray();
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling(units.Length / (double)size)));
        var cards = units.Skip((page - 1) * size).Take(size).SelectMany(g => g).ToArray();
        var (valuation, insights) = CollectionReadSummary.Build(collection.Cards);
        var sets = collection.Cards.Select(c => c.CardDetails?.SetCode).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Select(s => new CollectionSetOption(s!.ToLowerInvariant(), s.ToUpperInvariant())).ToArray();
        return collection with { Cards = cards, PageData = new(page, size, units.Length, collection.Cards.Length, valuation, insights, [new("__all", "All Sets"), .. sets], matcher.Error) };
    }

    private static bool Matches(CollectionCardDto row, CollectionPageQuery q, OwnedNameMatcher matcher)
    {
        var card = row.CardDetails;
        if (!matcher.Matches(card?.Name ?? "", row.OracleId))
            return false;
        if (!string.IsNullOrEmpty(q.Language) && !Equal(q.Language, row.Language))
            return false;
        if (q.DuplicatesOnly && Copies(row) < 2)
            return false;
        if (!string.IsNullOrEmpty(q.Set) && !Equal(q.Set, card?.SetCode))
            return false;
        if (q.Types.Length > 0 && !(card?.CardTypes ?? []).Any(t => q.Types.Contains(t.ToString(), StringComparer.OrdinalIgnoreCase)))
            return false;
        if (q.Rarities.Length > 0 && !q.Rarities.Contains(card?.Rarity ?? "", StringComparer.OrdinalIgnoreCase))
            return false;
        if (!ColorsMatch(card?.ColorIdentity ?? [], q.Colors))
            return false;
        var cmc = card?.ManaValue ?? 0;
        if (!string.IsNullOrEmpty(q.Cmc) && !(q.Cmc == "6+" ? cmc >= 6 : int.TryParse(q.Cmc, out var n) && cmc == n))
            return false;
        return CollectionFinishData.Finishes.Where(f => string.IsNullOrEmpty(q.Finish) || Equal(f, q.Finish)).Any(f =>
        {
            if (Quantity(row, f) <= 0)
                return false;
            row.FinishData.TryGetValue(f, out var data);
            return (string.IsNullOrEmpty(q.Condition) || Equal(q.Condition, data is null ? row.Condition : data.Condition))
                && (string.IsNullOrEmpty(q.Tag) || (data?.Tags ?? row.Tags).Contains(q.Tag, StringComparer.OrdinalIgnoreCase));
        });
    }
    private static bool ColorsMatch(ManaColorDto[] identity, string[] wanted)
    {
        if (wanted.Length == 0)
            return true;
        var selected = wanted.Select(s => s.ToUpperInvariant()).ToHashSet();
        var colors = selected.Where(s => s.Length == 1 && "WUBRG".Contains(s)).ToHashSet();
        if (selected.Contains("C") && identity.Length == 0)
            return true;
        return colors.Count > 0 ? identity.Length > 0 && identity.All(c => colors.Contains(c.ToString())) && (!selected.Contains("M") || identity.Length >= 2)
            : selected.Contains("M") && identity.Length >= 2;
    }
    private static IEnumerable<CollectionCardDto> Order(CollectionCardDto[] rows, CollectionPageQuery q)
    {
        Func<CollectionCardDto, IComparable?> key = q.Sort switch
        {
            "cmc" => c => c.CardDetails?.ManaValue ?? 0,
            "quantity" => c => Copies(c),
            "set" => c => c.CardDetails?.SetCode,
            "rarity" => c => Array.IndexOf(new[] { "common", "uncommon", "rare", "mythic", "special", "bonus" }, c.CardDetails?.Rarity),
            "price" => c => c.Quantity > 0 ? c.CardDetails?.Prices?.Usd : c.QuantityFoil > 0 ? c.CardDetails?.Prices?.UsdFoil : c.CardDetails?.Prices?.UsdEtched,
            "added" => c => c.AddedAt,
            _ => c => c.CardDetails?.Name ?? c.OracleId
        };
        var sorted = rows.OrderBy(c => key(c) is null ? 1 : 0);
        var result = q.SortDir == "desc" ? sorted.ThenByDescending(key, ReadKeyComparer.Instance) : sorted.ThenBy(key, ReadKeyComparer.Instance);
        return result.ThenBy(c => c.CardDetails?.Name ?? c.OracleId, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id);
    }
    private sealed class ReadKeyComparer : IComparer<IComparable?>
    {
        public static readonly ReadKeyComparer Instance = new();
        public int Compare(IComparable? x, IComparable? y) => x is string a && y is string b ? StringComparer.OrdinalIgnoreCase.Compare(a, b) : x?.CompareTo(y) ?? (y is null ? 0 : -1);
    }
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static int Copies(CollectionCardDto c) => c.Quantity + c.QuantityFoil + c.QuantityEtched;
    internal static int Quantity(CollectionCardDto c, string f) => f == "foil" ? c.QuantityFoil : f == "etched" ? c.QuantityEtched : c.Quantity;
}

/// <summary>Original Compleat search semantics: regex takes precedence over whole-token mode.
/// Invalid patterns and timed-out rows do not match. No browser regex executes.</summary>
public sealed class OwnedNameMatcher
{
    private readonly string text;
    private readonly bool matchCase, matchWord, useRegex;
    private readonly Regex? pattern;
    public string? Error { get; private set; }
    public OwnedNameMatcher(string? query, bool matchCase, bool matchWord, bool useRegex)
    {
        text = query?.Trim() ?? "";
        this.matchCase = matchCase;
        this.matchWord = matchWord;
        this.useRegex = useRegex;
        if (!useRegex || text.Length == 0)
            return;
        try
        { pattern = new Regex(text, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { Error = "This regular expression is invalid."; }
    }
    public bool Matches(string name, string oracleId)
    {
        if (text.Length == 0)
            return true;
        if (useRegex)
        {
            if (pattern is null)
                return false;
            try
            { return pattern.IsMatch(name); }
            catch (RegexMatchTimeoutException) { Error = "Some cards were skipped because this expression took too long. Refine your expression."; return false; }
        }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (matchWord)
            return name.Split([' ', ',', '-', '\'', '"', '(', ')', '/', ':', '.'], StringSplitOptions.RemoveEmptyEntries).Any(word => string.Equals(word, text, comparison));
        return name.Contains(text, comparison) || oracleId.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
