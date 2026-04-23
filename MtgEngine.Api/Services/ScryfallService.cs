using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Services;

public interface IScryfallService
{
    Task<CardDefinition?> GetByOracleIdAsync(string oracleId);
    Task<CardDefinition?> GetByNameAsync(string name);
}

/// <summary>
/// Fetches card data from the Scryfall API.
/// Two-layer cache: in-memory (process lifetime) + disk (persists across restarts).
/// Disk cache stores raw Scryfall JSON so new parsed fields don't require re-fetching.
/// Per Scryfall ToS: descriptive User-Agent, max ~10 req/sec.
/// </summary>
public sealed class ScryfallService : IScryfallService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScryfallService> _logger;

    // In-memory cache (fast path)
    private readonly ConcurrentDictionary<string, CardDefinition?> _byOracleId = new();
    private readonly ConcurrentDictionary<string, CardDefinition?> _byName     = new(StringComparer.OrdinalIgnoreCase);

    // Disk cache root
    private readonly string _cacheDir;

    // Rate limiting: Scryfall asks for max 10 req/sec
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(110);

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public ScryfallService(HttpClient http, ILogger<ScryfallService> logger, IConfiguration config)
    {
        _http      = http;
        _logger    = logger;
        _cacheDir  = config["ScryfallCache:Directory"]
                     ?? Path.Combine(AppContext.BaseDirectory, "card-cache");

        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-oracle"));
        Directory.CreateDirectory(Path.Combine(_cacheDir, "by-name"));
    }

    // ---- Public API ----------------------------------------

    public async Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
    {
        if (_byOracleId.TryGetValue(oracleId, out var mem)) return mem;

        var json = await LoadDiskAsync(OracleCachePath(oracleId))
                   ?? await FetchAndCacheByOracleIdAsync(oracleId);

        var def = json is null ? null : ParseCardDefinition(json.Value);
        _byOracleId[oracleId] = def;
        return def;
    }

    public async Task<CardDefinition?> GetByNameAsync(string name)
    {
        if (_byName.TryGetValue(name, out var mem)) return mem;

        var json = await LoadDiskAsync(NameCachePath(name))
                   ?? await FetchAndCacheByNameAsync(name);

        var def = json is null ? null : ParseCardDefinition(json.Value);
        _byName[name] = def;
        if (def is not null) _byOracleId[def.OracleId] = def;
        return def;
    }

    // ---- Fetch + disk-write --------------------------------

    private async Task<JsonElement?> FetchAndCacheByOracleIdAsync(string oracleId)
    {
        var json = await FetchAsync($"cards/{oracleId}");
        if (json is null) return null;

        await SaveDiskAsync(OracleCachePath(oracleId), json.Value);
        return json;
    }

    private async Task<JsonElement?> FetchAndCacheByNameAsync(string name)
    {
        var encoded = Uri.EscapeDataString(name);
        var json    = await FetchAsync($"cards/named?fuzzy={encoded}");
        if (json is null) return null;

        await SaveDiskAsync(NameCachePath(name), json.Value);

        // Cross-populate oracle ID cache so a future lookup by ID hits disk too
        if (json.Value.TryGetProperty("oracle_id", out var oid))
            await SaveDiskAsync(OracleCachePath(oid.GetString()!), json.Value);

        return json;
    }

    // ---- Disk helpers --------------------------------------

    private string OracleCachePath(string oracleId) =>
        Path.Combine(_cacheDir, "by-oracle", $"{oracleId}.json");

    private string NameCachePath(string name)
    {
        var safe = string.Concat(name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_cacheDir, "by-name", $"{safe}.json");
    }

    private async Task<JsonElement?> LoadDiskAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var el = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
            _logger.LogDebug("Disk cache hit: {Path}", path);
            return el;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read disk cache: {Path}", path);
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
            _logger.LogWarning(ex, "Failed to write disk cache: {Path}", path);
        }
    }

    // ---- HTTP + rate limit --------------------------------

    private async Task<JsonElement?> FetchAsync(string path)
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
                _logger.LogWarning("Scryfall {Path} returned {Status}", path, response.StatusCode);
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

    // ---- Parsing ------------------------------------------

    private static CardDefinition? ParseCardDefinition(JsonElement json)
    {
        try
        {
            var oracleId = json.GetProperty("oracle_id").GetString() ?? Guid.NewGuid().ToString();
            var name     = json.GetProperty("name").GetString() ?? "";
            var typeLine = json.GetProperty("type_line").GetString() ?? "";
            var oracle   = json.TryGetProperty("oracle_text", out var ot) ? ot.GetString() ?? "" : "";
            var mc       = json.TryGetProperty("mana_cost", out var mcEl) ? ParseManaCostString(mcEl.GetString() ?? "") : ManaCost.Zero;

            int? power = null, toughness = null, loyalty = null;
            if (json.TryGetProperty("power",     out var pw) && int.TryParse(pw.GetString(), out var p)) power     = p;
            if (json.TryGetProperty("toughness", out var th) && int.TryParse(th.GetString(), out var t)) toughness = t;
            if (json.TryGetProperty("loyalty",   out var lo) && int.TryParse(lo.GetString(), out var l)) loyalty   = l;

            string? imgNormal = null, imgSmall = null, imgArtCrop = null;
            if (json.TryGetProperty("image_uris", out var imgs))
            {
                if (imgs.TryGetProperty("normal",   out var n)) imgNormal  = n.GetString();
                if (imgs.TryGetProperty("small",    out var s)) imgSmall   = s.GetString();
                if (imgs.TryGetProperty("art_crop", out var a)) imgArtCrop = a.GetString();
            }

            var flavorText = json.TryGetProperty("flavor_text", out var ft) ? ft.GetString() : null;
            var artist     = json.TryGetProperty("artist",       out var ar) ? ar.GetString() : null;
            var setCode    = json.TryGetProperty("set",          out var sc) ? sc.GetString() : null;

            var cardTypes  = ParseCardTypes(typeLine);
            var subtypes   = ParseSubtypes(typeLine);
            var supertypes = ParseSupertypes(typeLine);
            var keywords   = ParseKeywords(json);
            var colorId    = ParseColorIdentity(json);
            var speed      = cardTypes.HasFlag(CardType.Instant) || HasFlash(keywords)
                ? SpeedRestriction.Instant
                : SpeedRestriction.Sorcery;

            return new CardDefinition
            {
                OracleId        = oracleId,
                Name            = name,
                ManaCost        = mc,
                CardTypes       = cardTypes,
                Subtypes        = subtypes,
                Supertypes      = supertypes,
                OracleText      = oracle,
                Power           = power,
                Toughness       = toughness,
                StartingLoyalty = loyalty,
                Keywords        = keywords,
                ColorIdentity   = colorId,
                ImageUriNormal  = imgNormal,
                ImageUriSmall   = imgSmall,
                ImageUriArtCrop = imgArtCrop,
                CastingSpeed    = speed,
                FlavorText      = flavorText,
                Artist          = artist,
                SetCode         = setCode,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ManaCost ParseManaCostString(string cost)
    {
        var cleaned = cost
            .Replace("{", "").Replace("}", "")
            .Replace("X", "");
        try { return ManaCost.Parse(cleaned); }
        catch { return ManaCost.Zero; }
    }

    private static CardType ParseCardTypes(string typeLine)
    {
        var flags = CardType.None;
        if (typeLine.Contains("Creature"))     flags |= CardType.Creature;
        if (typeLine.Contains("Instant"))      flags |= CardType.Instant;
        if (typeLine.Contains("Sorcery"))      flags |= CardType.Sorcery;
        if (typeLine.Contains("Enchantment"))  flags |= CardType.Enchantment;
        if (typeLine.Contains("Artifact"))     flags |= CardType.Artifact;
        if (typeLine.Contains("Land"))         flags |= CardType.Land;
        if (typeLine.Contains("Planeswalker")) flags |= CardType.Planeswalker;
        return flags == CardType.None ? CardType.Sorcery : flags;
    }

    private static IReadOnlyList<string> ParseSubtypes(string typeLine)
    {
        var idx = typeLine.IndexOf('—');
        if (idx < 0) return [];
        return typeLine[(idx + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> ParseSupertypes(string typeLine)
    {
        var supers = new[] { "Legendary", "Basic", "Snow", "World" };
        return supers.Where(typeLine.Contains).ToList();
    }

    private static KeywordAbility ParseKeywords(JsonElement json)
    {
        var flags = KeywordAbility.None;
        if (!json.TryGetProperty("keywords", out var kwArr)) return flags;

        foreach (var kw in kwArr.EnumerateArray())
        {
            var s = kw.GetString() ?? "";
            flags |= s switch
            {
                "Flying"        => KeywordAbility.Flying,
                "Reach"         => KeywordAbility.Reach,
                "First strike"  => KeywordAbility.FirstStrike,
                "Double strike" => KeywordAbility.DoubleStrike,
                "Trample"       => KeywordAbility.Trample,
                "Deathtouch"    => KeywordAbility.Deathtouch,
                "Lifelink"      => KeywordAbility.Lifelink,
                "Vigilance"     => KeywordAbility.Vigilance,
                "Haste"         => KeywordAbility.Haste,
                "Hexproof"      => KeywordAbility.Hexproof,
                "Indestructible"=> KeywordAbility.Indestructible,
                "Menace"        => KeywordAbility.Menace,
                "Flash"         => KeywordAbility.Flash,
                "Shroud"        => KeywordAbility.Shroud,
                _ => KeywordAbility.None
            };
        }
        return flags;
    }

    private static IReadOnlyList<ManaColor> ParseColorIdentity(JsonElement json)
    {
        if (!json.TryGetProperty("color_identity", out var ci)) return [];
        return ci.EnumerateArray()
            .Select(c => c.GetString() switch
            {
                "W" => ManaColor.White, "U" => ManaColor.Blue, "B" => ManaColor.Black,
                "R" => ManaColor.Red,   "G" => ManaColor.Green, _ => ManaColor.Colorless
            }).ToList();
    }

    private static bool HasFlash(KeywordAbility kw) => kw.HasFlag(KeywordAbility.Flash);
}
