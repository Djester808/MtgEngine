using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
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
    // set code → oracle IDs that have a printing in that set
    private Dictionary<string, List<string>> _bySetCode = new(StringComparer.OrdinalIgnoreCase);
    // set code → human-readable set name
    private Dictionary<string, string> _setNames = new(StringComparer.OrdinalIgnoreCase);
    // oracle_id → rarity of canonical printing
    private Dictionary<string, string> _rarityByOracleId = new(32_000, StringComparer.OrdinalIgnoreCase);

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

    public async Task<RulingDto[]> GetRulingsAsync(string oracleId)
    {
        await WaitReadyAsync();
        // Use the first known Scryfall ID for this oracle entry so we call the right rulings endpoint.
        if (_printingsByOracleId.TryGetValue(oracleId, out var prints) && prints.Length > 0)
            return await _api.GetRulingsByScryfallIdAsync(prints[0].ScryfallId);
        return await _api.GetRulingsAsync(oracleId); // fallback: let ScryfallService resolve it
    }

    public async Task<SetSummaryDto[]> GetSetsAsync(string? filterQuery = null)
    {
        await WaitReadyAsync();

        IEnumerable<KeyValuePair<string, List<string>>> source = _bySetCode;

        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            var q              = filterQuery.Trim();
            var nameFilter     = ParseName(q);
            var typeFlags      = ParseTypes(q);
            var raritySet      = ParseRarities(q);
            var (cmcOp, cmcVal) = ParseCmc(q);
            var (colorFilter, multicolor, colorless, colorSet) = ParseColors(q);

            var matchingOracleIds = _byOracleId.Values
                .Where(d => nameFilter is null || d.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .Where(d => typeFlags == CardType.None || (d.CardTypes & typeFlags) != CardType.None)
                .Where(d => raritySet.Count == 0 || (_rarityByOracleId.TryGetValue(d.OracleId, out var r) && raritySet.Contains(r)))
                .Where(d => cmcOp is null || MatchesCmc(d, cmcOp, cmcVal))
                .Where(d => !colorFilter || MatchesColor(d, multicolor, colorless, colorSet))
                .Select(d => d.OracleId)
                .ToHashSet();

            var relevantSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var oid in matchingOracleIds)
                if (_printingsByOracleId.TryGetValue(oid, out var prints))
                    foreach (var p in prints)
                        if (p.SetCode is not null) relevantSets.Add(p.SetCode);

            source = _bySetCode.Where(kv => relevantSets.Contains(kv.Key));
        }

        return source
            .OrderBy(kv => _setNames.TryGetValue(kv.Key, out var n) ? n : kv.Key)
            .Select(kv => new SetSummaryDto(
                kv.Key.ToUpperInvariant(),
                _setNames.TryGetValue(kv.Key, out var name) ? name : kv.Key.ToUpperInvariant(),
                kv.Value.Count))
            .ToArray();
    }

    public async Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc", bool matchCase = false, bool matchWord = false, bool useRegex = false)
    {
        await WaitReadyAsync();
        var q = query.Trim();
        if (q.Length < 2) return [];

        var nameFilter      = ParseName(q);
        var typeFlags       = ParseTypes(q);
        var setFilter       = ParseSet(q);
        var raritySet       = ParseRarities(q);
        var (cmcOp, cmcVal) = ParseCmc(q);
        var (colorFilter, multicolor, colorless, colorSet) = ParseColors(q);
        var descending      = sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);

        IEnumerable<string> oracleIds;
        if (setFilter is not null)
        {
            if (!_bySetCode.TryGetValue(setFilter, out var setList) || setList.Count == 0)
                return await _api.SearchAsync(query, limit, offset, sortBy, sortDir, matchCase, matchWord, useRegex);
            oracleIds = setList;
        }
        else
        {
            // Name-only fast path — only usable for plain case-insensitive contains (no regex/word/case flags)
            if (nameFilter is not null && typeFlags == CardType.None && raritySet.Count == 0
                && cmcOp is null && !colorFilter && !matchCase && !matchWord && !useRegex)
            {
                var nameKeys = _byName.Keys
                    .Where(n => n.StartsWith(nameFilter, StringComparison.OrdinalIgnoreCase))
                    .Concat(_byName.Keys.Where(n => !n.StartsWith(nameFilter, StringComparison.OrdinalIgnoreCase)
                                                 && n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)));
                nameKeys = descending ? nameKeys.OrderByDescending(n => n) : nameKeys.OrderBy(n => n);
                var localResults = nameKeys
                    .Skip(offset)
                    .Take(limit)
                    .Select(n => _byOracleId.TryGetValue(_byName[n], out var d) ? d : null)
                    .Where(d => d is not null).Cast<CardDefinition>()
                    .Select(d => d.ImageUriSmall is null ? EnrichWithFirstPrinting(d) : d)
                    .ToArray();
                if (localResults.Length > 0 || offset > 0) return localResults;
            }
            oracleIds = _byOracleId.Keys;
        }

        var filtered = oracleIds
            .Select(oid => _byOracleId.TryGetValue(oid, out var d) ? d : null)
            .Where(d => d is not null).Cast<CardDefinition>()
            .Where(d => nameFilter is null || MatchesName(d.Name, nameFilter, matchCase, matchWord, useRegex))
            .Where(d => typeFlags == CardType.None || (d.CardTypes & typeFlags) != CardType.None)
            .Where(d => raritySet.Count == 0 ||
                        (_rarityByOracleId.TryGetValue(d.OracleId, out var r) && raritySet.Contains(r)))
            .Where(d => cmcOp is null || MatchesCmc(d, cmcOp, cmcVal))
            .Where(d => !colorFilter || MatchesColor(d, multicolor, colorless, colorSet));

        var sorted = sortBy.Equals("cmc", StringComparison.OrdinalIgnoreCase)
            ? (descending
                ? filtered.OrderByDescending(d => d.ManaCost.ManaValue).ThenBy(d => d.Name)
                : filtered.OrderBy(d => d.ManaCost.ManaValue).ThenBy(d => d.Name))
            : (descending
                ? filtered.OrderByDescending(d => d.Name)
                : (IOrderedEnumerable<CardDefinition>)filtered.OrderBy(d => d.Name));

        var results = sorted
            .Skip(offset)
            .Take(limit)
            .Select(d =>
            {
                if (setFilter is not null && _printingsByOracleId.TryGetValue(d.OracleId, out var prints))
                {
                    var p = prints.FirstOrDefault(pr =>
                        pr.SetCode?.Equals(setFilter, StringComparison.OrdinalIgnoreCase) == true);
                    if (p is not null)
                        return CardParser.WithPrinting(d, p.ImageUriNormal, p.ImageUriSmall, null,
                                                       p.SetCode, p.ImageUriNormalBack);
                }
                return d.ImageUriSmall is null ? EnrichWithFirstPrinting(d) : d;
            })
            .ToArray();

        if (results.Length > 0 || offset > 0) return results;
        return await _api.SearchAsync(query, limit, offset, sortBy, sortDir, matchCase, matchWord, useRegex);
    }

    // ---- Query parsers ---------------------------------------------------

    private static string? ParseName(string q)
    {
        // name:"some text"
        var idx = q.IndexOf("name:\"", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + 6;
            var end = q.IndexOf('"', start);
            if (end > start) return q[start..end];
        }
        // Plain text with no query-syntax tokens → treat the whole query as a name filter
        // Any "key:" pattern (t:, s:, r:, c:, name:, cmc, etc.) signals structured query syntax
        var hasToken = q.Contains(':') || q.IndexOf("cmc", StringComparison.OrdinalIgnoreCase) >= 0;
        return hasToken ? null : q.Trim();
    }

    private static CardType ParseTypes(string q)
    {
        var flags = CardType.None;
        var i = 0;
        while ((i = q.IndexOf("t:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += 2;
            var end = i;
            while (end < q.Length && char.IsLetterOrDigit(q[end])) end++;
            flags |= q[i..end].ToLowerInvariant() switch
            {
                "creature"     => CardType.Creature,
                "instant"      => CardType.Instant,
                "sorcery"      => CardType.Sorcery,
                "enchantment"  => CardType.Enchantment,
                "artifact"     => CardType.Artifact,
                "land"         => CardType.Land,
                "planeswalker" => CardType.Planeswalker,
                "token"        => CardType.Token,
                "battle"       => CardType.Battle,
                "other"        => CardType.Other,
                _              => CardType.None
            };
            i = end;
        }
        return flags;
    }

    private static string? ParseSet(string q)
    {
        var idx = q.IndexOf("s:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + 2;
        var end = start;
        while (end < q.Length && char.IsLetterOrDigit(q[end])) end++;
        return end > start ? q[start..end] : null;
    }

    private static HashSet<string> ParseRarities(string q)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while ((i = q.IndexOf("r:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += 2;
            var end = i;
            while (end < q.Length && char.IsLetterOrDigit(q[end])) end++;
            var r = q[i..end].ToLowerInvariant();
            if (r is "common" or "uncommon" or "rare" or "mythic") result.Add(r);
            i = end;
        }
        return result;
    }

    private static (string? Op, int Val) ParseCmc(string q)
    {
        foreach (var op in new[] { "<=", ">=", "=" })
        {
            var key = "cmc" + op;
            var idx = q.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end   = start;
            while (end < q.Length && char.IsDigit(q[end])) end++;
            if (end > start && int.TryParse(q[start..end], out var val))
                return (op, val);
        }
        return (null, 0);
    }

    private static bool MatchesCmc(CardDefinition d, string op, int val)
        => op switch { "<=" => d.Cmc <= val, ">=" => d.Cmc >= val, _ => d.Cmc == val };

    private static (bool HasFilter, bool Multicolor, bool Colorless, HashSet<ManaColor> Colors) ParseColors(string q)
    {
        var idx = q.IndexOf("c:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (false, false, false, []);
        var start = idx + 2;
        var end = start;
        while (end < q.Length && char.IsLetter(q[end])) end++;
        if (end == start) return (false, false, false, []);
        var token = q[start..end].ToLowerInvariant();
        if (token == "m") return (true, true, false, []);
        if (token == "c") return (true, false, true, []);
        var colors = new HashSet<ManaColor>();
        foreach (var ch in token)
        {
            var c = ch switch { 'w' => ManaColor.White, 'u' => ManaColor.Blue, 'b' => ManaColor.Black,
                                'r' => ManaColor.Red,   'g' => ManaColor.Green, _ => (ManaColor?)null };
            if (c.HasValue) colors.Add(c.Value);
        }
        return colors.Count > 0 ? (true, false, false, colors) : (false, false, false, []);
    }

    private static bool MatchesColor(CardDefinition d, bool multicolor, bool colorless, HashSet<ManaColor> colors)
    {
        if (multicolor)  return d.ColorIdentity.Count >= 2;
        if (colorless)   return d.ColorIdentity.Count == 0;
        return d.ColorIdentity.Any(c => colors.Contains(c));
    }

    private static readonly char[] _wordSeparators = [' ', ',', '-', '\'', '"', '(', ')', '/', ':', '.'];

    internal static bool MatchesName(string name, string filter, bool matchCase, bool matchWord, bool useRegex)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (useRegex)
        {
            try
            {
                var opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(name, filter, opts | RegexOptions.CultureInvariant);
            }
            catch (RegexParseException) { return false; }
        }
        if (matchWord)
        {
            var words = name.Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.Equals(filter, comparison));
        }
        return name.Contains(filter, comparison);
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

        var rarityMap = new Dictionary<string, string>(32_000, StringComparer.OrdinalIgnoreCase);

        foreach (var card in doc.RootElement.EnumerateArray())
        {
            // Skip digital-only and non-English
            if (card.TryGetProperty("digital", out var dig) && dig.GetBoolean()) continue;
            if (card.TryGetProperty("lang", out var lang) && lang.GetString() != "en") continue;

            var def = CardParser.Parse(card);
            if (def is null) continue;

            byOracleId[def.OracleId] = def;
            byName[def.Name] = def.OracleId;

            var rarity = card.TryGetProperty("rarity", out var rEl) ? rEl.GetString() ?? "" : "";
            if (rarity.Length > 0) rarityMap[def.OracleId] = rarity;
        }

        _byOracleId        = byOracleId;
        _byName            = byName;
        _rarityByOracleId  = rarityMap;
    }

    private async Task LoadDefaultCardsAsync(string path)
    {
        var printings   = new Dictionary<string, List<PrintingDto>>(32_000);
        var scryfallIdx = new Dictionary<string, PrintingEntry>(250_000);
        var setIdx      = new Dictionary<string, List<string>>(500, StringComparer.OrdinalIgnoreCase);
        var setNames    = new Dictionary<string, string>(500, StringComparer.OrdinalIgnoreCase);

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

            // Build set-code → oracle-id index and set name lookup
            if (setCode.Length > 0)
            {
                if (!setIdx.TryGetValue(setCode, out var setList))
                {
                    setList = new List<string>(32);
                    setIdx[setCode] = setList;
                }
                if (!setList.Contains(oid)) setList.Add(oid);
                if (setName.Length > 0) setNames.TryAdd(setCode, setName);
            }
        }

        _printingsByOracleId = printings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _byScryfallId        = scryfallIdx;
        _bySetCode           = setIdx;
        _setNames            = setNames;
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
