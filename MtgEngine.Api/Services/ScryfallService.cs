using System.Collections.Concurrent;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Looking up cards that are already identified, by id or by name.
/// </summary>
/// <remarks>
/// Split from <see cref="IScryfallService"/> because both providers can honour this much:
/// a single-card lookup is one API call, so the live-API provider answers it as well as
/// the bulk one. The operations that need a scan of the whole corpus are on
/// <see cref="IScryfallService"/> instead, which only the bulk provider implements.
/// Depend on this interface unless you genuinely need the corpus -- most callers only
/// resolve names to cards.
/// </remarks>
public interface ICardLookup
{
    Task<CardDefinition?> GetByOracleIdAsync(string oracleId);
    Task<CardDefinition?> GetByNameAsync(string name);
    Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId);
    Task<PrintingDto[]> GetPrintingsAsync(string oracleId);
    Task<RulingDto[]> GetRulingsAsync(string oracleId);
    Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc", bool matchCase = false, bool matchWord = false, bool useRegex = false);
}

public static class CardLookupExtensions
{
    /// <summary>
    /// Resolves the card definition a collection entry should render with: the pinned
    /// printing when the row carries one, else the oracle default.
    /// </summary>
    /// <remarks>
    /// Two call sites used to resolve by oracle id unconditionally, so a card pinned to
    /// a specific printing came back with the default printing's art from AddCard and
    /// GetCard but the pinned art from GetCollection. Falls back to the oracle default
    /// when the pinned id cannot be resolved rather than dropping the card details.
    /// </remarks>
    public static async Task<CardDefinition?> ResolveForEntryAsync(
        this ICardLookup lookup, CollectionCard card)
    {
        if (card.ScryfallId is not null)
        {
            var pinned = await lookup.GetByScryfallIdAsync(card.ScryfallId);
            if (pinned is not null)
                return pinned;
        }
        return await lookup.GetByOracleIdAsync(card.OracleId);
    }
}

/// <summary>
/// Everything in <see cref="ICardLookup"/>, plus the queries that have to walk the whole
/// card corpus. Only the bulk-data provider can answer these.
/// </summary>
public interface IScryfallService : ICardLookup
{
    Task<SetSummaryDto[]> GetSetsAsync(string? filterQuery = null);
    Task<IReadOnlySet<string>> GetRecentSetCodesAsync(int monthsBack = 6, int? maxSets = null);

    /// <summary>
    /// The same sets as <see cref="GetRecentSetCodesAsync"/>, but named and dated so the
    /// caller can tell the user which sets "latest" actually resolved to.
    /// </summary>
    Task<IReadOnlyList<RecentSetDto>> GetRecentSetsAsync(int monthsBack = 6, int? maxSets = null);
    /// <param name="debutOnly">
    /// Keep only cards first printed in these sets. Commander products are mostly
    /// reprints, so without this a "new cards" list fills up with decades-old staples.
    /// </param>
    Task<string[]> GetRecentCardNamesAsync(IReadOnlySet<string> setCodes, IReadOnlySet<ManaColor> commanderColors, IReadOnlySet<string>? allowedRarities = null, bool debutOnly = false);

    /// <summary>
    /// The full legal card pool for a commander, filtered by a free-text query, for the
    /// user to browse. No model involved -- this is what the AI picks are drawn from.
    /// </summary>
    /// <param name="commander">
    /// Used to rank the pool when there is no query, so it opens on cards that echo the
    /// commander rather than on whatever sorts first alphabetically.
    /// </param>
    Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
        IReadOnlySet<ManaColor> commanderColors, CardDefinition? commander = null, string? query = null,
        IReadOnlySet<string>? setCodes = null, bool gameChangersOnly = false,
        CardType types = CardType.None, int? cmcMin = null, int? cmcMax = null,
        int limit = 50, int offset = 0);

    /// <summary>
    /// Every card that could legally go in this deck: Commander-legal, inside the
    /// colour identity, and allowed at this bracket. Excludes basic lands and tokens.
    /// This is a legality filter only -- it does not rank or judge playability.
    /// Grouped by deck-building role, names sorted, so the result is deterministic.
    /// </summary>
    Task<IReadOnlyDictionary<CardRole, string[]>> GetLegalCardsByRoleAsync(
        IReadOnlySet<ManaColor> commanderColors, int bracket);

    /// <summary>
    /// Cards that can legally head a Commander deck, optionally filtered by name.
    /// Searches the whole card corpus, not just commanders that already have decks here.
    /// </summary>
    /// <param name="setCode">Optional set code, to list the commanders printed in one set.</param>
    Task<CardDefinition[]> SearchCommandersAsync(string? nameQuery, int limit = 100, string? setCode = null);
}

/// <summary>
/// Fetches card data from the live Scryfall API with a two-layer cache
/// (in-memory + disk). Used as fallback when BulkDataService doesn't have a card.
/// </summary>
/// <remarks>
/// Implements <see cref="ICardLookup"/> only. It used to declare the whole of
/// <see cref="IScryfallService"/> and stub the corpus-wide half with empty results, which
/// meant anything resolving that interface to this class got silently empty sets and
/// commander lists rather than an error.
/// </remarks>
public sealed class ScryfallService : ICardLookup, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ScryfallService> _logger;

    private readonly ConcurrentDictionary<string, CachedDef> _byOracleId = new();
    private readonly ConcurrentDictionary<string, CachedDef> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A cached lookup result (null = definite 404) plus when it was cached.</summary>
    private readonly record struct CachedDef(CardDefinition? Def, DateTime CachedAtUtc);

    // Card JSON carries the prices, and Scryfall refreshes those daily — so cached
    // fallback lookups expire on the same cadence the bulk data does. Without this,
    // a bulk-data miss served whatever prices the card had when first cached, forever.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly string _cacheDir;

    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(110);

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public ScryfallService(IHttpClientFactory httpClientFactory, ILogger<ScryfallService> logger, IConfiguration config)
    {
        _http = httpClientFactory.CreateClient("ScryfallApi");
        _logger = logger;
        _cacheDir = config["ScryfallCache:Directory"]
                     ?? Path.Combine(AppContext.BaseDirectory, "card-cache");

        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-oracle"));
        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-name"));
        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-scryfall"));
    }

    // ---- IScryfallService ----------------------------------------

    public async Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
    {
        if (TryGetFresh(_byOracleId, oracleId, out var mem))
            return mem;

        var (json, transient) = await LoadOrFetchAsync(OraclePath(oracleId), $"cards/{oracleId}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        if (!transient)
            _byOracleId[oracleId] = new CachedDef(def, DateTime.UtcNow); // a real card, or a definite 404
        return def; // on transient this may be a stale disk copy — served, but retried next call
    }

    public async Task<CardDefinition?> GetByNameAsync(string name)
    {
        if (TryGetFresh(_byName, name, out var mem))
            return mem;

        var encoded = Uri.EscapeDataString(name);
        var (json, transient) = await LoadOrFetchAsync(NamePath(name), $"cards/named?fuzzy={encoded}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        if (!transient)
            _byName[name] = new CachedDef(def, DateTime.UtcNow);
        if (def is not null && !transient)
        {
            _byOracleId[def.OracleId] = new CachedDef(def, DateTime.UtcNow);
            if (json.HasValue && json.Value.TryGetProperty("oracle_id", out var oid))
                await SaveDiskAsync(OraclePath(oid.GetString()!), json.Value);
        }
        return def;
    }

    public async Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId)
    {
        var (json, transient) = await LoadOrFetchAsync(ScryfallPath(scryfallId), $"cards/{scryfallId}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        if (def is not null && !transient)
        {
            _byOracleId.TryAdd(def.OracleId, new CachedDef(def, DateTime.UtcNow));
            if (json.HasValue && json.Value.TryGetProperty("oracle_id", out var oid))
                await SaveDiskAsync(OraclePath(oid.GetString()!), json.Value);
        }
        return def;
    }

    private static bool TryGetFresh(
        ConcurrentDictionary<string, CachedDef> cache, string key, out CardDefinition? def)
    {
        if (cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CachedAtUtc < CacheTtl)
        {
            def = entry.Def;
            return true;
        }
        def = null;
        return false;
    }

    public async Task<PrintingDto[]> GetPrintingsAsync(string oracleId)
    {
        var encoded = Uri.EscapeDataString($"oracleid:{oracleId}");
        // dir=desc: newest printing first, matching the bulk-data index ordering.
        var json = await FetchRawAsync($"cards/search?q={encoded}&unique=prints&order=released&dir=desc");
        if (json is null)
            return [];
        if (!json.Value.TryGetProperty("data", out var data))
            return [];

        // Shared mapping with the bulk-data index — the same oracle id must come back
        // identical (back-face image, combined DFC text) whichever source answered.
        var printings = new List<PrintingDto>();
        foreach (var card in data.EnumerateArray())
        {
            var parsed = ScryfallPrintingMapper.Parse(card);
            if (parsed is not null)
                printings.Add(parsed.Value.Dto);
        }
        return [.. printings];
    }

    private readonly ConcurrentDictionary<string, RulingDto[]> _rulingsByOracleId = new();

    // Scryfall's rulings endpoint requires a printing-specific Scryfall ID, not an oracle ID.
    // GetRulingsAsync resolves an oracleId → scryfallId via printings, then delegates here.
    internal async Task<RulingDto[]> GetRulingsByScryfallIdAsync(string scryfallId)
    {
        if (_rulingsByOracleId.TryGetValue(scryfallId, out var mem))
            return mem;

        Directory.CreateDirectory(Path.Combine(_cacheDir, "rulings"));
        var cachePath = Path.Combine(_cacheDir, "rulings", $"{scryfallId}.json");

        var (json, transient) = await LoadOrFetchAsync(cachePath, $"cards/{scryfallId}/rulings");
        if (transient && json is null)
            return []; // nothing cached either — retried on the next call

        if (json is null || !json.Value.TryGetProperty("data", out var data))
        {
            _rulingsByOracleId[scryfallId] = [];
            return [];
        }

        var rulings = data.EnumerateArray()
            .Select(r => new RulingDto(
                r.TryGetProperty("source", out var src) ? src.GetString() ?? "" : "",
                r.TryGetProperty("published_at", out var pub) ? pub.GetString() ?? "" : "",
                r.TryGetProperty("comment", out var cmt) ? cmt.GetString() ?? "" : ""))
            .Where(r => r.Comment.Length > 0)
            .ToArray();

        _rulingsByOracleId[scryfallId] = rulings;
        return rulings;
    }

    public async Task<RulingDto[]> GetRulingsAsync(string oracleId)
    {
        // Resolve oracle ID → any printing's Scryfall ID, then fetch rulings by that ID.
        var printings = await GetPrintingsAsync(oracleId);
        if (printings.Length == 0)
            return [];
        return await GetRulingsByScryfallIdAsync(printings[0].ScryfallId);
    }

    public async Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc", bool matchCase = false, bool matchWord = false, bool useRegex = false)
    {
        // Strip name:"..." wrapper if present so Scryfall fuzzy lookup gets the plain name
        var name = query.Trim();
        var idx = name.IndexOf("name:\"", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + 6;
            var end = name.IndexOf('"', start);
            if (end > start)
                name = name[start..end];
        }
        var def = await GetByNameAsync(name);
        return def is null ? [] : [def];
    }

    // ---- Disk helpers --------------------------------------------

    private string OraclePath(string id) => Path.Combine(_cacheDir, "by-oracle", $"{id}.json");
    private string NamePath(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_cacheDir, "by-name", $"{safe}.json");
    }
    private string ScryfallPath(string id) => Path.Combine(_cacheDir, "by-scryfall", $"{id}.json");

    private async Task<JsonElement?> LoadDiskAsync(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk cache read failed: {Path}", path);
            return null;
        }
    }

    private async Task SaveDiskAsync(string path, JsonElement json)
    {
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, json, _jsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk cache write failed: {Path}", path);
        }
    }

    /// <summary>
    /// Disk cache first (while younger than <see cref="CacheTtl"/>), else the live API
    /// (saving to disk on success). `Transient` is true for rate limits, 5xx, network
    /// faults and unparseable bodies — outcomes the caller must NOT cache; on those the
    /// expired disk copy is still returned when one exists, so an outage degrades to
    /// yesterday's prices rather than "card not found". Only a definite 404 comes back
    /// as a cacheable null.
    /// </summary>
    /// <remarks>
    /// Previously any failure returned a bare null that the lookups cached forever, so
    /// one 429 during a busy moment made a real card resolve as "does not exist" for
    /// the rest of the process lifetime — which the AI builder then rejected as
    /// unknown-card and the importer listed as unresolved. The disk cache also had no
    /// expiry, which pinned first-fetch prices for the life of the file.
    /// </remarks>
    private async Task<(JsonElement? Json, bool Transient)> LoadOrFetchAsync(
        string cachePath, string apiPath)
    {
        var age = CacheFileAge(cachePath);
        if (age is not null && age < CacheTtl)
        {
            var disk = await LoadDiskAsync(cachePath);
            if (disk is not null)
                return (disk, false);
        }

        var (outcome, json) = await FetchResultAsync(apiPath);
        if (outcome == FetchOutcome.Ok)
        {
            await SaveDiskAsync(cachePath, json!.Value);
            return (json, false);
        }
        if (outcome == FetchOutcome.NotFound)
            return (null, false);

        var stale = age is null ? null : await LoadDiskAsync(cachePath);
        return (stale, true);
    }

    private static TimeSpan? CacheFileAge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? DateTime.UtcNow - info.LastWriteTimeUtc : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- HTTP + rate limit ----------------------------------------

    private enum FetchOutcome { Ok, NotFound, Transient }

    private async Task<(FetchOutcome Outcome, JsonElement? Json)> FetchResultAsync(string path)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < MinInterval)
                await Task.Delay(MinInterval - elapsed);
            _lastRequest = DateTime.UtcNow;

            var response = await _http.GetAsync(path);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (FetchOutcome.NotFound, null);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Scryfall {Path} → {Status}", path, (int)response.StatusCode);
                return (FetchOutcome.Transient, null);
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return (FetchOutcome.Ok, await JsonSerializer.DeserializeAsync<JsonElement>(stream));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scryfall request failed: {Path}", path);
            return (FetchOutcome.Transient, null);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private async Task<JsonElement?> FetchRawAsync(string path)
    {
        var (_, json) = await FetchResultAsync(path);
        return json;
    }

    public void Dispose() => _rateLimiter.Dispose();
}
