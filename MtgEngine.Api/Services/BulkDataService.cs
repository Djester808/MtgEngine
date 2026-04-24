using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Primary IScryfallService implementation backed by Scryfall bulk-data files.
/// Loads two bulk files on startup:
///   oracle_cards   – one card per oracle ID, used for all card lookups by name/oracle.
///   default_cards  – all English printings, used for the set-selector and scryfall-id lookups.
///
/// Falls back to ScryfallService (live API + disk cache) for any miss.
/// </summary>
public sealed class BulkDataService : IScryfallService
{
    private readonly ScryfallService _api;
    private readonly HttpClient _metaClient;      // ScryfallApi — small JSON, proven working
    private readonly HttpClient _downloadClient;  // ScryfallBulk — large files, long timeout
    private readonly ILogger<BulkDataService> _logger;
    private readonly string _bulkDir;

    // oracle_id → CardDefinition (from oracle_cards file)
    private Dictionary<string, CardDefinition> _byOracleId = new(32_000);
    // lower-case name → oracle_id
    private Dictionary<string, string> _byName = new(32_000, StringComparer.OrdinalIgnoreCase);
    // oracle_id → ordered array of all printings
    private Dictionary<string, PrintingDto[]> _printingsByOracleId = new(32_000);
    // scryfall_id → (oracleId, imgNormal, imgSmall, imgArtCrop, setCode) – lightweight
    private Dictionary<string, PrintingEntry> _byScryfallId = new(250_000);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _ready = false;

    private record PrintingEntry(string OracleId, string? ImgNormal, string? ImgSmall, string? ImgArtCrop, string? SetCode, string? ImgNormalBack = null);

    public BulkDataService(
        ScryfallService api,
        IHttpClientFactory httpClientFactory,
        ILogger<BulkDataService> logger,
        IConfiguration config)
    {
        _api            = api;
        _metaClient     = httpClientFactory.CreateClient("ScryfallApi");
        _downloadClient = httpClientFactory.CreateClient("ScryfallBulk");
        _logger         = logger;
        _bulkDir        = config["BulkData:Directory"]
                          ?? Path.Combine(AppContext.BaseDirectory, "bulk-data");
        Directory.CreateDirectory(_bulkDir);
    }

    // ---- IScryfallService ------------------------------------------------

    public async Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
    {
        await WaitReadyAsync();
        if (_byOracleId.TryGetValue(oracleId, out var def)) return def;
        _logger.LogDebug("Oracle miss, falling back to API: {Id}", oracleId);
        var result = await _api.GetByOracleIdAsync(oracleId);
        if (result is not null) _byOracleId.TryAdd(oracleId, result);
        return result;
    }

    public async Task<CardDefinition?> GetByNameAsync(string name)
    {
        await WaitReadyAsync();
        if (_byName.TryGetValue(name, out var oracleId) && _byOracleId.TryGetValue(oracleId, out var def))
            return def;
        _logger.LogDebug("Name miss, falling back to API: {Name}", name);
        var result = await _api.GetByNameAsync(name);
        if (result is not null)
        {
            _byOracleId.TryAdd(result.OracleId, result);
            _byName.TryAdd(name, result.OracleId);
        }
        return result;
    }

    public async Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId)
    {
        await WaitReadyAsync();
        if (_byScryfallId.TryGetValue(scryfallId, out var entry)
            && _byOracleId.TryGetValue(entry.OracleId, out var oracle))
        {
            return CardParser.WithPrinting(oracle, entry.ImgNormal, entry.ImgSmall, entry.ImgArtCrop, entry.SetCode, entry.ImgNormalBack);
        }
        _logger.LogDebug("ScryfallId miss, falling back to API: {Id}", scryfallId);
        return await _api.GetByScryfallIdAsync(scryfallId);
    }

    public async Task<PrintingDto[]> GetPrintingsAsync(string oracleId)
    {
        await WaitReadyAsync();
        if (_printingsByOracleId.TryGetValue(oracleId, out var printings)) return printings;
        _logger.LogDebug("Printings miss, falling back to API: {Id}", oracleId);
        return await _api.GetPrintingsAsync(oracleId);
    }

    public async Task<CardDefinition[]> SearchAsync(string query, int limit = 20)
    {
        await WaitReadyAsync();
        var q = query.Trim();
        if (q.Length < 2) return [];

        // Starts-with results first, then contains
        var results = _byName.Keys
            .Where(n => n.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .Concat(_byName.Keys
                .Where(n => !n.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                         && n.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .Select(n => _byOracleId.TryGetValue(_byName[n], out var d) ? d : null)
            .Where(d => d is not null)
            .Cast<CardDefinition>()
            .Select(d => d.ImageUriSmall is null ? EnrichWithFirstPrinting(d) : d)
            .ToArray();

        if (results.Length > 0) return results;

        // Nothing in bulk data — one API call as last resort
        return await _api.SearchAsync(query, limit);
    }

    // ---- Refresh logic ---------------------------------------------------

    /// <summary>
    /// Downloads bulk files if Scryfall has updated them since our local copy,
    /// then rebuilds the in-memory indexes.
    /// Called by BulkDataRefreshWorker on startup and daily thereafter.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Checking Scryfall bulk-data for updates…");

        var meta = await FetchBulkMetaAsync(ct);
        if (meta is null)
        {
            _logger.LogWarning("Could not reach Scryfall bulk-data API; will use existing files if present");
        }

        bool changed = false;

        if (meta?.OracleCards is not null)
            changed |= await DownloadIfStaleAsync(meta.OracleCards, "oracle_cards.json", ct);

        if (meta?.DefaultCards is not null)
            changed |= await DownloadIfStaleAsync(meta.DefaultCards, "default_cards.json", ct);

        // Load on first run regardless of whether we downloaded
        if (changed || !_ready)
            await RebuildIndexesAsync();
    }

    // ---- Internal --------------------------------------------------------

    private async Task WaitReadyAsync()
    {
        if (_ready) return;
        // Yield so the caller doesn't block the request thread on first startup
        await Task.Yield();
        // If still not ready, just let through — fallback to API will handle it
    }

    private async Task RebuildIndexesAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            _ready = false;

            var oraclePath  = Path.Combine(_bulkDir, "oracle_cards.json");
            var defaultPath = Path.Combine(_bulkDir, "default_cards.json");

            var sw = Stopwatch.StartNew();

            if (File.Exists(oraclePath))
                await LoadOracleCardsAsync(oraclePath);
            else
                _logger.LogWarning("oracle_cards.json not found — card lookups will use live API");

            if (File.Exists(defaultPath))
                await LoadDefaultCardsAsync(defaultPath);
            else
                _logger.LogWarning("default_cards.json not found — printings will use live API");

            sw.Stop();
            _logger.LogInformation(
                "Bulk indexes ready: {Oracle} oracle cards, {Prints} printing groups, {Scryfall} scryfall IDs ({Ms}ms)",
                _byOracleId.Count, _printingsByOracleId.Count, _byScryfallId.Count, sw.ElapsedMilliseconds);

            _ready = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadOracleCardsAsync(string path)
    {
        var byOracleId = new Dictionary<string, CardDefinition>(32_000);
        var byName     = new Dictionary<string, string>(32_000, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Parsing oracle_cards.json…");
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { AllowTrailingCommas = true });

        foreach (var card in doc.RootElement.EnumerateArray())
        {
            // Skip digital-only and non-English
            if (card.TryGetProperty("digital", out var dig) && dig.GetBoolean()) continue;
            if (card.TryGetProperty("lang", out var lang) && lang.GetString() != "en") continue;

            var def = CardParser.Parse(card);
            if (def is null) continue;

            byOracleId[def.OracleId] = def;
            byName[def.Name] = def.OracleId;
        }

        _byOracleId = byOracleId;
        _byName     = byName;
    }

    private async Task LoadDefaultCardsAsync(string path)
    {
        var printings  = new Dictionary<string, List<PrintingDto>>(32_000);
        var scryfallIdx = new Dictionary<string, PrintingEntry>(250_000);

        _logger.LogInformation("Parsing default_cards.json…");
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { AllowTrailingCommas = true });

        foreach (var card in doc.RootElement.EnumerateArray())
        {
            if (card.TryGetProperty("digital", out var dig) && dig.GetBoolean()) continue;
            if (card.TryGetProperty("lang", out var lang) && lang.GetString() != "en") continue;

            var id      = card.TryGetProperty("id",               out var idEl)  ? idEl.GetString()  : null;
            var oid     = card.TryGetProperty("oracle_id",        out var oidEl) ? oidEl.GetString() : null;
            var setCode = card.TryGetProperty("set",              out var scEl)  ? scEl.GetString()  ?? "" : "";
            var setName = card.TryGetProperty("set_name",         out var snEl)  ? snEl.GetString()  ?? "" : "";
            var num     = card.TryGetProperty("collector_number", out var numEl) ? numEl.GetString() : null;

            if (id is null || oid is null) continue;

            string? imgSmall = null, imgNormal = null, imgArtCrop = null, imgNormalBack = null;
            if (card.TryGetProperty("image_uris", out var imgs))
            {
                if (imgs.TryGetProperty("small",    out var s)) imgSmall   = s.GetString();
                if (imgs.TryGetProperty("normal",   out var n)) imgNormal  = n.GetString();
                if (imgs.TryGetProperty("art_crop", out var a)) imgArtCrop = a.GetString();
            }
            else if (card.TryGetProperty("card_faces", out var dfcImgs) && dfcImgs.GetArrayLength() > 0)
            {
                var f0 = dfcImgs[0];
                if (f0.TryGetProperty("image_uris", out var fi0))
                {
                    if (fi0.TryGetProperty("small",    out var s)) imgSmall   = s.GetString();
                    if (fi0.TryGetProperty("normal",   out var n)) imgNormal  = n.GetString();
                    if (fi0.TryGetProperty("art_crop", out var a)) imgArtCrop = a.GetString();
                }
                if (dfcImgs.GetArrayLength() > 1)
                {
                    var f1 = dfcImgs[1];
                    if (f1.TryGetProperty("image_uris", out var fi1) && fi1.TryGetProperty("normal", out var nb))
                        imgNormalBack = nb.GetString();
                }
            }

            // Only include cards that have artwork
            if (imgNormal is null) continue;

            // Per-printing text fields — fall back to card_faces[0] for DFCs
            JsonElement? face0 = null;
            if (card.TryGetProperty("card_faces", out var faces) && faces.GetArrayLength() > 0)
                face0 = faces[0];

            string? artist     = GetStr(card, "artist")      ?? GetStr(face0, "artist");
            string? oracleText = GetStr(card, "oracle_text") ?? GetStr(face0, "oracle_text");
            string? flavorText = GetStr(card, "flavor_text") ?? GetStr(face0, "flavor_text");
            string? manaCost   = GetStr(card, "mana_cost")   ?? GetStr(face0, "mana_cost");

            var dto = new PrintingDto
            {
                ScryfallId         = id,
                SetCode            = setCode,
                SetName            = setName,
                CollectorNumber    = num,
                ImageUriSmall      = imgSmall,
                ImageUriNormal     = imgNormal,
                ImageUriNormalBack = imgNormalBack,
                Artist             = artist,
                OracleText         = oracleText,
                FlavorText         = flavorText,
                ManaCost           = manaCost,
            };

            if (!printings.TryGetValue(oid, out var list))
            {
                list = new List<PrintingDto>(4);
                printings[oid] = list;
            }
            list.Add(dto);

            scryfallIdx[id] = new PrintingEntry(oid, imgNormal, imgSmall, imgArtCrop, setCode, imgNormalBack);
        }

        _printingsByOracleId = printings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _byScryfallId        = scryfallIdx;
    }

    private static string? GetStr(JsonElement? el, string prop)
    {
        if (el is null) return null;
        return el.Value.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    private CardDefinition EnrichWithFirstPrinting(CardDefinition oracle)
    {
        if (!_printingsByOracleId.TryGetValue(oracle.OracleId, out var printings) || printings.Length == 0)
            return oracle;
        var first = printings[0];
        return CardParser.WithPrinting(oracle, first.ImageUriNormal, first.ImageUriSmall, null, first.SetCode);
    }

    // ---- Bulk-data metadata & download -----------------------------------

    private record BulkMeta(BulkEntry? OracleCards, BulkEntry? DefaultCards);
    private record BulkEntry(string Name, string DownloadUri, DateTimeOffset UpdatedAt);

    private async Task<BulkMeta?> FetchBulkMetaAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _metaClient.GetAsync("bulk-data", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            BulkEntry? oracle = null, defaults = null;
            foreach (var entry in data.EnumerateArray())
            {
                var type = entry.TryGetProperty("type",         out var t) ? t.GetString() : null;
                var uri  = entry.TryGetProperty("download_uri", out var u) ? u.GetString() : null;
                var name = entry.TryGetProperty("name",         out var n) ? n.GetString() ?? "" : "";
                DateTimeOffset updatedAt = default;
                if (entry.TryGetProperty("updated_at", out var ua))
                    DateTimeOffset.TryParse(ua.GetString(), out updatedAt);

                if (uri is null) continue;
                var be = new BulkEntry(name, uri, updatedAt);
                if (type == "oracle_cards")  oracle   = be;
                if (type == "default_cards") defaults = be;
            }

            return new BulkMeta(oracle, defaults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch bulk-data metadata");
            return null;
        }
    }

    private async Task<bool> DownloadIfStaleAsync(BulkEntry entry, string fileName, CancellationToken ct)
    {
        var localPath = Path.Combine(_bulkDir, fileName);
        var metaPath  = localPath + ".meta";

        if (File.Exists(localPath) && File.Exists(metaPath))
        {
            if (DateTimeOffset.TryParse(await File.ReadAllTextAsync(metaPath, ct), out var saved)
                && saved >= entry.UpdatedAt)
            {
                _logger.LogInformation("Bulk file {File} is current (Scryfall: {At})", fileName, entry.UpdatedAt);
                return false;
            }
        }

        _logger.LogInformation("Downloading {Name} → {File}…", entry.Name, fileName);
        try
        {
            using var response = await _downloadClient.GetAsync(entry.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var tmpPath = localPath + ".tmp";
            await using (var src  = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = File.Create(tmpPath))
            {
                // Bulk files may be delivered as gzip even without .gz extension
                var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault() ?? "";
                if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                    || entry.DownloadUri.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    await using var gz = new GZipStream(src, CompressionMode.Decompress);
                    await gz.CopyToAsync(dest, ct);
                }
                else
                {
                    await src.CopyToAsync(dest, ct);
                }
            }

            File.Move(tmpPath, localPath, overwrite: true);
            await File.WriteAllTextAsync(metaPath, entry.UpdatedAt.ToString("O"), ct);

            var size = new FileInfo(localPath).Length / 1_048_576.0;
            _logger.LogInformation("Downloaded {File} ({Size:F1} MB)", fileName, size);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {File}", fileName);
            return false;
        }
    }
}
