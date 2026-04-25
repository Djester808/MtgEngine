using System.Collections.Concurrent;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IScryfallService
{
    Task<CardDefinition?> GetByOracleIdAsync(string oracleId);
    Task<CardDefinition?> GetByNameAsync(string name);
    Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId);
    Task<PrintingDto[]>    GetPrintingsAsync(string oracleId);
    Task<SetSummaryDto[]>  GetSetsAsync(string? filterQuery = null);
    Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc", bool matchCase = false, bool matchWord = false, bool useRegex = false);
}

/// <summary>
/// Fetches card data from the live Scryfall API with a two-layer cache
/// (in-memory + disk). Used as fallback when BulkDataService doesn't have a card.
/// </summary>
public sealed class ScryfallService : IScryfallService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScryfallService> _logger;

    private readonly ConcurrentDictionary<string, CardDefinition?> _byOracleId = new();
    private readonly ConcurrentDictionary<string, CardDefinition?> _byName = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheDir;

    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(110);

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public ScryfallService(IHttpClientFactory httpClientFactory, ILogger<ScryfallService> logger, IConfiguration config)
    {
        _http      = httpClientFactory.CreateClient("ScryfallApi");
        _logger    = logger;
        _cacheDir  = config["ScryfallCache:Directory"]
                     ?? Path.Combine(AppContext.BaseDirectory, "card-cache");

        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-oracle"));
        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-name"));
        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-scryfall"));
    }

    // ---- IScryfallService ----------------------------------------

    public async Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
    {
        if (_byOracleId.TryGetValue(oracleId, out var mem)) return mem;

        var json = await LoadDiskAsync(OraclePath(oracleId))
                   ?? await FetchAndSaveAsync(OraclePath(oracleId), $"cards/{oracleId}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        _byOracleId[oracleId] = def;
        return def;
    }

    public async Task<CardDefinition?> GetByNameAsync(string name)
    {
        if (_byName.TryGetValue(name, out var mem)) return mem;

        var encoded = Uri.EscapeDataString(name);
        var json = await LoadDiskAsync(NamePath(name))
                   ?? await FetchAndSaveAsync(NamePath(name), $"cards/named?fuzzy={encoded}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        _byName[name] = def;
        if (def is not null)
        {
            _byOracleId[def.OracleId] = def;
            if (json.HasValue && json.Value.TryGetProperty("oracle_id", out var oid))
                await SaveDiskAsync(OraclePath(oid.GetString()!), json.Value);
        }
        return def;
    }

    public async Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId)
    {
        var json = await LoadDiskAsync(ScryfallPath(scryfallId))
                   ?? await FetchAndSaveAsync(ScryfallPath(scryfallId), $"cards/{scryfallId}");

        var def = json is null ? null : CardParser.Parse(json.Value);
        if (def is not null)
        {
            _byOracleId.TryAdd(def.OracleId, def);
            if (json.HasValue && json.Value.TryGetProperty("oracle_id", out var oid))
                await SaveDiskAsync(OraclePath(oid.GetString()!), json.Value);
        }
        return def;
    }

    public Task<SetSummaryDto[]> GetSetsAsync(string? filterQuery = null) => Task.FromResult(Array.Empty<SetSummaryDto>());

    public async Task<PrintingDto[]> GetPrintingsAsync(string oracleId)
    {
        var encoded = Uri.EscapeDataString($"oracleid:{oracleId}");
        var json = await FetchRawAsync($"cards/search?q={encoded}&unique=prints&order=released&dir=asc");
        if (json is null) return [];
        if (!json.Value.TryGetProperty("data", out var data)) return [];

        var printings = new List<PrintingDto>();
        foreach (var card in data.EnumerateArray())
        {
            var id      = card.TryGetProperty("id",               out var idEl)  ? idEl.GetString()   ?? "" : "";
            var setCode = card.TryGetProperty("set",              out var setEl) ? setEl.GetString()  ?? "" : "";
            var setName = card.TryGetProperty("set_name",         out var snEl)  ? snEl.GetString()   ?? "" : "";
            var num     = card.TryGetProperty("collector_number", out var numEl) ? numEl.GetString()       : null;

            string? imgSmall = null, imgNormal = null;
            if (card.TryGetProperty("image_uris", out var imgs))
            {
                if (imgs.TryGetProperty("small",  out var s)) imgSmall  = s.GetString();
                if (imgs.TryGetProperty("normal", out var n)) imgNormal = n.GetString();
            }

            // Per-printing text — fall back to card_faces[0] for DFCs
            var face0 = card.TryGetProperty("card_faces", out var faces) && faces.GetArrayLength() > 0
                ? faces[0] : (JsonElement?)null;

            string? oracleText = GetStr(card, "oracle_text") ?? GetStr(face0, "oracle_text");
            string? flavorText = GetStr(card, "flavor_text") ?? GetStr(face0, "flavor_text");
            string? artist     = GetStr(card, "artist")      ?? GetStr(face0, "artist");
            string? manaCost   = GetStr(card, "mana_cost")   ?? GetStr(face0, "mana_cost");

            printings.Add(new PrintingDto
            {
                ScryfallId      = id,
                SetCode         = setCode,
                SetName         = setName,
                CollectorNumber = num,
                ImageUriSmall   = imgSmall,
                ImageUriNormal  = imgNormal,
                OracleText      = oracleText,
                FlavorText      = flavorText,
                Artist          = artist,
                ManaCost        = manaCost,
            });
        }
        return [..printings];
    }

    private static string? GetStr(JsonElement? el, string prop)
    {
        if (el is null) return null;
        return el.Value.TryGetProperty(prop, out var v) ? v.GetString() : null;
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
            if (end > start) name = name[start..end];
        }
        var def = await GetByNameAsync(name);
        return def is null ? [] : [def];
    }

    // ---- Disk helpers --------------------------------------------

    private string OraclePath(string id)   => Path.Combine(_cacheDir, "by-oracle",   $"{id}.json");
    private string NamePath(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_cacheDir, "by-name", $"{safe}.json");
    }
    private string ScryfallPath(string id) => Path.Combine(_cacheDir, "by-scryfall", $"{id}.json");

    private async Task<JsonElement?> LoadDiskAsync(string path)
    {
        if (!File.Exists(path)) return null;
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

    private async Task<JsonElement?> FetchAndSaveAsync(string cachePath, string apiPath)
    {
        var json = await FetchRawAsync(apiPath);
        if (json is not null)
            await SaveDiskAsync(cachePath, json.Value);
        return json;
    }

    // ---- HTTP + rate limit ----------------------------------------

    private async Task<JsonElement?> FetchRawAsync(string path)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < MinInterval)
                await Task.Delay(MinInterval - elapsed);
            _lastRequest = DateTime.UtcNow;

            var response = await _http.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Scryfall {Path} → {Status}", path, (int)response.StatusCode);
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scryfall request failed: {Path}", path);
            return null;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
