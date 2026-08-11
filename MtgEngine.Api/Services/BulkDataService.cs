using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

// The query language moved to CardQuery; imported statically so the filter pipelines
// below still read as ParseTypes(q) rather than CardQuery.ParseTypes(q).
using static MtgEngine.Api.Services.CardQuery;

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
    // set code → release date (from released_at on card printings)
    private Dictionary<string, DateOnly> _setReleaseDates = new(StringComparer.OrdinalIgnoreCase);
    // set code → Scryfall set_type (expansion, commander, promo, alchemy, token, ...)
    private Dictionary<string, string> _setTypes = new(StringComparer.OrdinalIgnoreCase);
    // oracle_id → rarity of canonical printing
    private Dictionary<string, string> _rarityByOracleId = new(32_000, StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _ready = false;

    private record PrintingEntry(string OracleId, string? ImgNormal, string? ImgLarge, string? ImgSmall, string? ImgArtCrop, string? SetCode, string? ImgNormalBack = null);

    public BulkDataService(
        ScryfallService api,
        IHttpClientFactory httpClientFactory,
        ILogger<BulkDataService> logger,
        IConfiguration config)
    {
        _api = api;
        _metaClient = httpClientFactory.CreateClient("ScryfallApi");
        _downloadClient = httpClientFactory.CreateClient("ScryfallBulk");
        _logger = logger;
        _bulkDir = config["BulkData:Directory"]
                          ?? Path.Combine(AppContext.BaseDirectory, "bulk-data");
        Directory.CreateDirectory(_bulkDir);
    }

    // ---- IScryfallService ------------------------------------------------

    public async Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
    {
        await WaitReadyAsync();
        if (_byOracleId.TryGetValue(oracleId, out var def))
            return def;
        _logger.LogDebug("Oracle miss, falling back to API: {Id}", oracleId);
        var result = await _api.GetByOracleIdAsync(oracleId);
        if (result is not null)
            _byOracleId.TryAdd(oracleId, result);
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
            return CardParser.WithPrinting(oracle, entry.ImgNormal, entry.ImgLarge, entry.ImgSmall, entry.ImgArtCrop, entry.SetCode, entry.ImgNormalBack);
        }
        _logger.LogDebug("ScryfallId miss, falling back to API: {Id}", scryfallId);
        return await _api.GetByScryfallIdAsync(scryfallId);
    }

    public async Task<PrintingDto[]> GetPrintingsAsync(string oracleId)
    {
        await WaitReadyAsync();
        if (_printingsByOracleId.TryGetValue(oracleId, out var printings))
            return printings;
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
            var q = filterQuery.Trim();
            var nameFilter = ParseName(q);
            var typeFlags = ParseTypes(q);
            var supertypeFilter = ParseSupertypes(q);
            var raritySet = ParseRarities(q);
            var (cmcOp, cmcVal) = ParseCmc(q);
            var (colorFilter, multicolor, colorless, colorSet) = ParseColors(q);

            var matchingOracleIds = _byOracleId.Values
                .Where(d => nameFilter is null || d.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .Where(d => typeFlags == CardType.None || (d.CardTypes & typeFlags) != CardType.None)
                .Where(d => supertypeFilter.Count == 0 || supertypeFilter.All(s => d.Supertypes.Contains(s, StringComparer.OrdinalIgnoreCase)))
                .Where(d => raritySet.Count == 0 || (_rarityByOracleId.TryGetValue(d.OracleId, out var r) && raritySet.Contains(r)))
                .Where(d => cmcOp is null || MatchesCmc(d, cmcOp, cmcVal))
                .Where(d => !colorFilter || MatchesColor(d, multicolor, colorless, colorSet))
                .Select(d => d.OracleId)
                .ToHashSet();

            var relevantSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var oid in matchingOracleIds)
                if (_printingsByOracleId.TryGetValue(oid, out var prints))
                    foreach (var p in prints)
                        if (p.SetCode is not null)
                            relevantSets.Add(p.SetCode);

            // A bare word is far more likely to name the set than a card inside it.
            // Matching only card text meant "hobbit" listed the Lord of the Rings sets
            // (they contain cards with Hobbit in the name) and not The Hobbit itself.
            foreach (var code in MatchSetCodes(nameFilter ?? q))
                relevantSets.Add(code);

            source = _bySetCode.Where(kv => relevantSets.Contains(kv.Key));
        }

        // Newest first: a set picker is nearly always used to reach a recent set, and
        // alphabetical order buries this month's release somewhere in the middle.
        // Undated sets sort last rather than jumping to the top as DateOnly.MinValue.
        return source
            .Select(kv => new SetSummaryDto(
                kv.Key.ToUpperInvariant(),
                _setNames.TryGetValue(kv.Key, out var name) ? name : kv.Key.ToUpperInvariant(),
                kv.Value.Count,
                _setReleaseDates.TryGetValue(kv.Key, out var d) ? d : null))
            .OrderByDescending(s => s.ReleasedAt.HasValue)
            .ThenByDescending(s => s.ReleasedAt)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Set types that represent a genuine new-card release players can build with.
    /// </summary>
    /// <remarks>
    /// Excludes promo, token, memorabilia, art series, minigame, Alchemy (digital-only,
    /// not legal in paper Commander), and reprint-only products such as masters sets.
    /// Without this filter "recent" is dominated by Secret Lairs and promo drops, which
    /// is why the latest-set suggestions were full of cards from nowhere in particular.
    /// </remarks>
    /// <remarks>
    /// "eternal" covers the Universes Beyond bonus sheets (Transformers, Jurassic World,
    /// The Hobbit Eternal). They are Commander-legal releases of new cards, so leaving
    /// them out hid half of a set's launch.
    /// </remarks>
    private static readonly HashSet<string> ReleaseSetTypes = new(StringComparer.OrdinalIgnoreCase)
        { "expansion", "core", "commander", "draft_innovation", "eternal" };

    /// <summary>
    /// How many Commander-legal cards a set needs before it counts as released here.
    /// </summary>
    /// <remarks>
    /// Scryfall carries partially-spoiled sets weeks before release, sometimes with a
    /// single card. Such a set is "the newest" by date but has nothing to suggest from,
    /// so treating it as the latest release silently starves the category.
    /// </remarks>
    private const int MinLegalCardsForRelease = 50;


    /// <summary>
    /// Number of Commander-legal cards in a set.
    /// </summary>
    /// <remarks>
    /// Some sets carry a release set_type yet are entirely outside the format -- the
    /// Marvel sets are standalone products where every card is not_legal. Treating one
    /// as "the latest set" made the suggestions panel offer cards that cannot be played.
    /// </remarks>
    private int CountCommanderLegalCards(string setCode) =>
        _bySetCode.TryGetValue(setCode, out var oracleIds)
            ? oracleIds.Count(oid => _byOracleId.TryGetValue(oid, out var d) && CommanderRules.IsLegalInCommander(d))
            : 0;

    /// <summary>
    /// The most recently released real sets, newest first.
    /// </summary>
    /// <param name="monthsBack">
    /// Ignored when <paramref name="maxSets"/> is supplied; kept for callers that still
    /// think in time windows.
    /// </param>
    /// <param name="maxSets">
    /// Take this many most-recent sets instead of everything inside a date window. A
    /// window is a poor proxy: Magic ships many products a month, so six months can mean
    /// twenty-plus sets, and the genuinely new cards get lost among them.
    /// </param>
    public async Task<IReadOnlySet<string>> GetRecentSetCodesAsync(int monthsBack = 6, int? maxSets = null)
    {
        var sets = await GetRecentSetsAsync(monthsBack, maxSets);
        return new HashSet<string>(sets.Select(s => s.Code), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<RecentSetDto>> GetRecentSetsAsync(int monthsBack = 6, int? maxSets = null)
    {
        await WaitReadyAsync();

        // Walk newest-first and record why each set was passed over. When the answer is
        // a set from months ago, the log has to explain what happened to everything
        // newer -- otherwise it just looks like the date sort is broken.
        var skipped = new List<string>();
        var accepted = new List<RecentSetDto>();

        foreach (var kv in _setReleaseDates
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (code, date) = (kv.Key, kv.Value);
            _setTypes.TryGetValue(code, out var setType);

            // No cutoff on the release date. Scryfall carries sets months ahead, but a
            // set only reaches the card-count threshold below once it is substantially
            // spoiled, and by then players are already building with it.
            string? skipReason =
                setType is null || !ReleaseSetTypes.Contains(setType) ? $"set_type={setType ?? "?"}"
                : null;

            int legal = 0;
            if (skipReason is null)
            {
                legal = CountCommanderLegalCards(code);
                if (legal == 0)
                    skipReason = "no Commander-legal cards";
                else if (legal < MinLegalCardsForRelease)
                    skipReason = $"only {legal} legal cards — looks partially spoiled";
            }

            if (skipReason is not null)
            {
                // Only the sets ahead of the ones we chose matter; older skips are noise.
                if (skipped.Count < 8 && accepted.Count == 0)
                    skipped.Add($"{code} ({skipReason})");
                continue;
            }

            accepted.Add(new RecentSetDto(
                code, _setNames.TryGetValue(code, out var n) ? n : code.ToUpperInvariant(), date, legal));

            if (maxSets is > 0 && accepted.Count >= maxSets.Value)
                break;
        }

        if (maxSets is not > 0)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-monthsBack));
            accepted = [.. accepted.Where(s => s.ReleasedAt >= cutoff)];
        }

        _logger.LogInformation(
            "Recent sets ({Mode}): {Chosen}. Skipped newer: {Skipped}",
            maxSets is > 0 ? $"latest {maxSets}" : $"{monthsBack}mo",
            accepted.Count == 0
                ? "(none)"
                : string.Join(", ", accepted.Select(s => $"{s.Code} {s.ReleasedAt:yyyy-MM-dd} [{s.LegalCardCount} legal]")),
            skipped.Count == 0 ? "(none)" : string.Join("; ", skipped));

        return accepted;
    }

    /// <summary>
    /// Cached Game Changer definitions (~53 cards), built once on first use. Scanning
    /// the whole corpus per request would be wasteful for a set this small and static.
    /// </summary>
    private CardDefinition[]? _gameChangers;

    /// <summary>Cached list of every Commander-legal commander, built once on first use.</summary>
    private CardDefinition[]? _commanders;

    public async Task<CardDefinition[]> SearchCommandersAsync(
        string? nameQuery, int limit = 100, string? setCode = null)
    {
        await WaitReadyAsync();

        _commanders ??= _byOracleId.Values
            .Where(IsCommanderEligible)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var pool = _commanders;

        // Restrict to one set when asked. Uses the set index rather than the card's
        // canonical SetCode, so a commander reprinted into the set still counts.
        if (!string.IsNullOrWhiteSpace(setCode))
        {
            var inSet = _bySetCode.TryGetValue(setCode.Trim(), out var oracleIds)
                ? new HashSet<string>(oracleIds, StringComparer.OrdinalIgnoreCase)
                : [];
            pool = [.. pool.Where(d => inSet.Contains(d.OracleId))];
        }

        var q = nameQuery?.Trim();
        if (string.IsNullOrEmpty(q))
            return [.. pool.Take(limit)];

        // A query is as likely to name a set as a card. Searching only card names meant
        // "hobbit" returned the five cards with Hobbit in the title and none of the
        // commanders from The Hobbit, which is what someone typing it actually wants.
        var matchedSets = MatchSetCodes(q);
        var inMatchedSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in matchedSets)
            if (_bySetCode.TryGetValue(code, out var ids))
                inMatchedSets.UnionWith(ids);

        // Someone searching "wolf" wants Wolf commanders, not the six cards with Wolf in
        // the name. Plurals count too -- "wolves" is the natural way to ask.
        var subtype = ResolveSubtype(q);

        // Prefix matches first -- typing "kro" should surface Krosan before Sakashima
        // of a Thousand Faces just because the latter contains the letters later on.
        // Type and set members come last: an exact name match is the stronger signal.
        var prefix = new List<CardDefinition>();
        var contains = new List<CardDefinition>();
        var byType = new List<CardDefinition>();
        var bySet = new List<CardDefinition>();
        foreach (var d in pool)
        {
            if (d.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                prefix.Add(d);
            else if (d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                contains.Add(d);
            else if (subtype is not null && d.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                byType.Add(d);
            else if (inMatchedSets.Contains(d.OracleId))
                bySet.Add(d);
        }

        if (matchedSets.Count > 0 || subtype is not null)
            _logger.LogInformation(
                "Commander search '{Query}': {ByName} by name, {ByType} by type {Subtype}, {BySet} by set {Sets}",
                q, prefix.Count + contains.Count, byType.Count, subtype ?? "-",
                bySet.Count, matchedSets.Count == 0 ? "-" : string.Join(",", matchedSets));

        return [.. prefix.Concat(contains).Concat(byType).Concat(bySet).Take(limit)];
    }

    /// <summary>
    /// Set codes whose code or name matches the query. An exact code match ("hob") wins
    /// outright; otherwise any set whose name contains the query counts.
    /// </summary>
    private List<string> MatchSetCodes(string q)
    {
        if (_setNames.ContainsKey(q))
            return [q];

        var matches = new List<string>();
        foreach (var (code, name) in _setNames)
        {
            // Tokens, art series and the like share their parent's name; they hold no
            // commanders, and including them only widens the scan.
            if (_setTypes.TryGetValue(code, out var t)
                && (t is "token" or "memorabilia" or "art_series" or "minigame"))
                continue;
            if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
                matches.Add(code);
        }
        return matches;
    }

    /// <summary>Every subtype in the corpus, built once. Used to tell a creature type from a word.</summary>
    private HashSet<string>? _allSubtypes;

    private HashSet<string> AllSubtypes => _allSubtypes ??= new HashSet<string>(
        _byOracleId.Values.SelectMany(d => d.Subtypes), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The creature type a query names, or null when it names no type at all.
    /// </summary>
    private string? ResolveSubtype(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;
        return Array.Find(SingularCandidates(query), AllSubtypes.Contains);
    }

    /// <summary>
    /// Legendary creatures, plus the cards that explicitly say they may head a deck
    /// (planeswalker commanders, backgrounds' partners, Grist, and similar).
    /// </summary>
    private static bool IsCommanderEligible(CardDefinition d)
    {
        if (!d.Legalities.TryGetValue("commander", out var legal) || legal != "legal")
            return false;
        if (d.CardTypes.HasFlag(Domain.Enums.CardType.Token))
            return false;

        bool legendaryCreature =
            d.Supertypes.Contains("Legendary") && d.CardTypes.HasFlag(Domain.Enums.CardType.Creature);

        return legendaryCreature
            || (d.OracleText?.Contains("can be your commander", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task<IReadOnlyDictionary<CardRole, string[]>> GetLegalCardsByRoleAsync(
        IReadOnlySet<ManaColor> commanderColors, int bracket)
    {
        await WaitReadyAsync();

        var legal = _byOracleId.Values
            .Where(d =>
                // Legality only. No popularity or playability judgement -- a card the
                // model could reasonably want must not be filtered out here.
                d.Legalities.TryGetValue("commander", out var leg) && leg == "legal"
                && !d.CardTypes.HasFlag(Domain.Enums.CardType.Token)
                && !d.Supertypes.Contains("Basic")
                && (commanderColors.Count == 0
                    || d.ColorIdentity.All(c => c == ManaColor.Colorless || commanderColors.Contains(c)))
                && !(d.GameChanger && bracket < 4))
            .ToArray();

        // Enum order for sections, alphabetical within them: the prompt prefix must be
        // byte-identical across builds for the same colours and bracket, or the
        // prompt cache never hits.
        var byRole = legal
            .GroupBy(CardRoleClassifier.Classify)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => d.Name)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                      .ToArray());

        _logger.LogInformation(
            "Legal pool: {Count} cards for colours [{Colors}] at bracket {Bracket} -- {Breakdown}",
            byRole.Sum(kv => kv.Value.Length), string.Join(",", commanderColors), bracket,
            string.Join(", ", byRole.Select(kv => $"{kv.Key}={kv.Value.Length}")));

        return byRole;
    }

    /// <summary>
    /// Every card that could legally go in this commander's deck, narrowed by a free-text
    /// query, for the user to browse and pick from directly.
    /// </summary>
    /// <remarks>
    /// Deliberately has no model in the loop and no ranking beyond match quality. The
    /// suggestion lists are short on purpose now, so this is the escape hatch: when the
    /// judge cuts a card the player wanted, they can still go and find it.
    /// </remarks>
    /// <summary>
    /// Terms that say a card is about what this commander is about: its own creature
    /// types, and the ability words and keywords its rules text turns on.
    /// </summary>
    /// <remarks>
    /// Deliberately drawn from the commander itself rather than a hand-written list, so
    /// it works for any commander without anyone maintaining a table of archetypes.
    /// </remarks>
    private static string[] CommanderSignals(CardDefinition? commander)
    {
        if (commander is null)
            return [];

        var signals = new List<string>(commander.Subtypes);

        // Ability words and keywords are the capitalised terms in the rules text --
        // Ferocious, Menace, Landfall. Skipping the first word of each sentence avoids
        // picking up ordinary sentence starts.
        var text = commander.OracleText ?? string.Empty;
        bool sentenceStart = true;
        foreach (var raw in text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('(', ')', ',', '.', ':', ';', '—', '-', '"');
            bool endsSentence = raw.EndsWith('.') || raw.EndsWith(':') || raw.Contains('\n');

            if (!sentenceStart && word.Length > 3 && char.IsUpper(word[0])
                && word.All(char.IsLetter) && !signals.Contains(word, StringComparer.OrdinalIgnoreCase))
                signals.Add(word);

            sentenceStart = endsSentence;
        }

        return [.. signals];
    }

    public async Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
        IReadOnlySet<ManaColor> commanderColors,
        CardDefinition? commander = null,
        string? query = null,
        IReadOnlySet<string>? setCodes = null,
        bool gameChangersOnly = false,
        Domain.Enums.CardType types = Domain.Enums.CardType.None,
        int? cmcMin = null,
        int? cmcMax = null,
        int limit = 50,
        int offset = 0)
    {
        await WaitReadyAsync();

        IEnumerable<CardDefinition> pool;
        if (setCodes is { Count: > 0 })
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in setCodes)
                if (_bySetCode.TryGetValue(code, out var setIds))
                    ids.UnionWith(setIds);
            pool = ids.Select(id => _byOracleId.TryGetValue(id, out var d) ? d : null)
                      .Where(d => d is not null)!;
        }
        else
        {
            pool = _byOracleId.Values;
        }

        pool = pool.Where(d =>
            CommanderRules.IsLegalInCommander(d)
            // The commander is already in the deck; offering it back is noise, and it
            // ranks first now that ordering rewards matching the commander.
            && !string.Equals(d.OracleId, commander?.OracleId, StringComparison.OrdinalIgnoreCase)
            && !d.CardTypes.HasFlag(Domain.Enums.CardType.Token)
            && !d.Supertypes.Contains("Basic")
            && (!gameChangersOnly || d.GameChanger)
            // Types are alternatives: ticking Creature and Artifact means either.
            && (types == Domain.Enums.CardType.None || (d.CardTypes & types) != Domain.Enums.CardType.None)
            && (cmcMin is null || d.Cmc >= cmcMin)
            && (cmcMax is null || d.Cmc <= cmcMax)
            && (commanderColors.Count == 0
                || d.ColorIdentity.All(c => c == ManaColor.Colorless || commanderColors.Contains(c))));

        // Every term must match somewhere on the card. Typing "destroy target creature"
        // should narrow, not return ten thousand cards containing any of those words;
        // and read as a focus, "wolf token" means wolf-token cards, not wolves plus
        // everything that makes a token.
        var terms = (query ?? string.Empty)
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length > 0)
        {
            var subtypes = terms.Select(ResolveSubtype).ToArray();

            // Rank by how directly the card answers the query. When a term names a real
            // creature type, being that type outranks having the word in the title:
            // searching "wolf" should lead with Wolves, not Wolf Strike and Wolf's Quarry.
            static int TermTier(CardDefinition d, string term, string? subtype)
            {
                bool isType = subtype is not null
                    && d.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase);
                bool namePrefix = d.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase);

                if (isType)
                    return namePrefix ? 0 : 1;
                if (namePrefix)
                    return 2;
                if (d.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return 3;
                return (d.OracleText?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    ? 4 : int.MaxValue;
            }

            // A card is out if any term misses; among survivors, rank by the strongest
            // match, so a Wolf that also says "token" leads over one that only says both
            // in its rules text.
            int MatchTier(CardDefinition d)
            {
                int best = int.MaxValue;
                for (int i = 0; i < terms.Length; i++)
                {
                    int tier = TermTier(d, terms[i], subtypes[i]);
                    if (tier == int.MaxValue)
                        return int.MaxValue;
                    best = Math.Min(best, tier);
                }
                return best;
            }

            pool = pool
                .Select(d => (Card: d, Tier: MatchTier(d)))
                .Where(x => x.Tier != int.MaxValue)
                .OrderBy(x => x.Tier)
                .ThenBy(x => x.Card.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Card);
        }
        else
        {
            // With no query this used to fall back to alphabetical, so every category's
            // browse list opened on whatever sorted first -- a Marvel card called
            // Abomination, for a Wolf commander. Rank by how much a card echoes the
            // commander instead: shares a creature type, or names an ability word its
            // rules text turns on. Nothing is filtered out, only ordered.
            var signals = CommanderSignals(commander);
            if (signals.Length == 0)
            {
                pool = pool.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                int Relevance(CardDefinition d)
                {
                    int score = 0;
                    foreach (var s in signals)
                    {
                        // Being the type counts for more than mentioning it.
                        if (d.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase))
                            score += 3;
                        else if (d.OracleText?.Contains(s, StringComparison.OrdinalIgnoreCase) == true)
                            score += 2;
                    }
                    return score;
                }

                pool = pool
                    .Select(d => (Card: d, Rank: Relevance(d)))
                    .OrderByDescending(x => x.Rank)
                    .ThenBy(x => x.Card.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Card);
            }
        }

        var all = pool.ToArray();
        var page = all.Skip(offset).Take(limit)
            .Select(d => d.ImageUriSmall is null ? EnrichWithFirstPrinting(d) : d)
            .ToArray();

        return (page, all.Length);
    }

    public async Task<string[]> GetGameChangerNamesAsync(IReadOnlySet<ManaColor> commanderColors)
    {
        await WaitReadyAsync();

        // Sorted so the resulting prompt is stable and cacheable.
        _gameChangers ??= _byOracleId.Values
            .Where(d => d.GameChanger)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var legal = commanderColors.Count == 0
            ? _gameChangers.AsEnumerable()
            : _gameChangers.Where(d =>
                d.ColorIdentity.All(c => c == ManaColor.Colorless || commanderColors.Contains(c)));

        return legal
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// True when the card debuted in one of these sets rather than being reprinted into
    /// them.
    /// </summary>
    /// <remarks>
    /// Modern Commander products are largely reprints, so "new from the latest sets"
    /// was offering cards like Thran Dynamo, first printed in 1998. Filtering on the
    /// debut printing is what makes the heading true.
    /// </remarks>
    private bool DebutedIn(string oracleId, IReadOnlySet<string> setCodes)
    {
        if (!_printingsByOracleId.TryGetValue(oracleId, out var prints) || prints.Length == 0)
            return false;

        DateOnly? earliest = null;
        foreach (var p in prints)
        {
            if (p.SetCode is not null
                && _setReleaseDates.TryGetValue(p.SetCode, out var d)
                && (earliest is null || d < earliest))
                earliest = d;
        }

        DateOnly? target = null;
        foreach (var code in setCodes)
            if (_setReleaseDates.TryGetValue(code, out var d) && (target is null || d < target))
                target = d;

        if (earliest is null || target is null)
            return false;

        // Prerelease promos and Secret Lair tie-ins ship days before the set proper, so
        // an exact date match would wrongly disqualify genuinely new cards.
        return earliest >= target.Value.AddDays(-14);
    }

    public async Task<string[]> GetRecentCardNamesAsync(
        IReadOnlySet<string> setCodes,
        IReadOnlySet<ManaColor> commanderColors,
        IReadOnlySet<string>? allowedRarities = null,
        bool debutOnly = false)
    {
        await WaitReadyAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(256);

        // Iterate set codes in a fixed order: HashSet enumeration order is not part of
        // its contract, and callers depend on this list being stable to build a
        // reproducible, cacheable prompt.
        foreach (var setCode in setCodes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (!_bySetCode.TryGetValue(setCode, out var oracleIds))
                continue;
            foreach (var oid in oracleIds)
            {
                if (!seen.Add(oid))
                    continue;
                if (!_byOracleId.TryGetValue(oid, out var def))
                    continue;
                // Not-legal cards (Un-sets, Alchemy, standalone Universes Beyond
                // products) must never be spotlighted -- they cannot go in the deck.
                if (!CommanderRules.IsLegalInCommander(def))
                    continue;
                if (debutOnly && !DebutedIn(oid, setCodes))
                    continue;
                // Skip tokens, basic lands, and commander-illegal card types
                if (def.Supertypes.Contains("Basic"))
                    continue;
                if (def.CardTypes.HasFlag(Domain.Enums.CardType.Token))
                    continue;
                // Rarity filter
                if (allowedRarities != null && allowedRarities.Count > 0 &&
                    !allowedRarities.Contains(def.Rarity ?? string.Empty))
                    continue;
                // Color identity check
                if (commanderColors.Count > 0 &&
                    !def.ColorIdentity.All(c => c == ManaColor.Colorless || commanderColors.Contains(c)))
                    continue;
                names.Add(def.Name);
            }
        }

        // Returns the full candidate set in a deterministic order. Truncating here
        // would bias the result (alphabetically, or by whichever set sorted first),
        // and shuffling here would make every caller's prompt unreproducible and
        // uncacheable. Callers pick their own subset via DeterministicSample.
        return [.. names];
    }

    public async Task<CardDefinition[]> SearchAsync(string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc", bool matchCase = false, bool matchWord = false, bool useRegex = false)
    {
        await WaitReadyAsync();
        var q = query.Trim();
        if (q.Length < 2)
            return [];

        var nameFilter = ParseName(q);
        var oracleFilter = ParseOracleText(q);
        var typeFlags = ParseTypes(q);
        var supertypeFilter = ParseSupertypes(q);
        var setFilter = ParseSet(q);
        var raritySet = ParseRarities(q);
        var (cmcOp, cmcVal) = ParseCmc(q);
        var (colorFilter, multicolor, colorless, colorSet) = ParseColors(q);
        var descending = sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);

        // "wolves" should find Wolves, not just cards with Wolf in the title. Only a bare
        // word is treated this way -- an explicit t:/o: query already says what it means.
        var subtype = useRegex || matchWord ? null : ResolveSubtype(nameFilter);

        IEnumerable<string> oracleIds;
        if (setFilter is not null)
        {
            if (!_bySetCode.TryGetValue(setFilter, out var setList) || setList.Count == 0)
                return await _api.SearchAsync(query, limit, offset, sortBy, sortDir, matchCase, matchWord, useRegex);
            oracleIds = setList;
        }
        else
        {
            // Name-only fast path — only usable for plain case-insensitive contains (no regex/word/case flags, no oracle filter).
            // A recognised creature type has to take the full scan: the name index cannot see subtypes.
            if (nameFilter is not null && subtype is null && oracleFilter is null && typeFlags == CardType.None && supertypeFilter.Count == 0
                && raritySet.Count == 0 && cmcOp is null && !colorFilter && !matchCase && !matchWord && !useRegex)
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
                if (localResults.Length > 0 || offset > 0)
                    return localResults;
            }
            oracleIds = _byOracleId.Keys;
        }

        var filtered = oracleIds
            .Select(oid => _byOracleId.TryGetValue(oid, out var d) ? d : null)
            .Where(d => d is not null).Cast<CardDefinition>()
            .Where(d => (nameFilter is null && oracleFilter is null)
                     || (nameFilter is not null && MatchesName(d.Name, nameFilter, matchCase, matchWord, useRegex))
                     || (subtype is not null && d.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                     || (oracleFilter is not null && MatchesOracleText(d, oracleFilter, matchCase)))
            .Where(d => typeFlags == CardType.None || (d.CardTypes & typeFlags) != CardType.None)
            .Where(d => supertypeFilter.Count == 0 || supertypeFilter.All(s => d.Supertypes.Contains(s, StringComparer.OrdinalIgnoreCase)))
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
                        return CardParser.WithPrinting(d, p.ImageUriNormal, p.ImageUriLarge, p.ImageUriSmall, null,
                                                       p.SetCode, p.ImageUriNormalBack);
                }
                return d.ImageUriSmall is null ? EnrichWithFirstPrinting(d) : d;
            })
            .ToArray();

        if (results.Length > 0 || offset > 0)
            return results;
        return await _api.SearchAsync(query, limit, offset, sortBy, sortDir, matchCase, matchWord, useRegex);
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
        if (_ready)
            return;
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

            var oraclePath = Path.Combine(_bulkDir, "oracle_cards.json");
            var defaultPath = Path.Combine(_bulkDir, "default_cards.json");

            var sw = Stopwatch.StartNew();

            // Parsing is CPU-bound and takes seconds; keep it off the caller's thread.
            if (File.Exists(oraclePath))
                await Task.Run(() => LoadOracleCards(oraclePath));
            else
                _logger.LogWarning("oracle_cards.json not found — card lookups will use live API");

            if (File.Exists(defaultPath))
                await Task.Run(() => LoadDefaultCards(defaultPath));
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

    /// <summary>
    /// Streams the card objects out of a bulk file.
    /// </summary>
    /// <remarks>
    /// Handles both shapes Scryfall has published: a single JSON array, and the
    /// newline-delimited form it serves now. Line-at-a-time parsing also keeps peak
    /// memory flat, where the array path had to hold the whole 500 MB document.
    /// The yielded element is only valid inside the caller's loop body.
    /// </remarks>
    private IEnumerable<JsonElement> EnumerateCards(string path)
    {
        using var reader = new StreamReader(path);
        var stats = new ParseStats();

        foreach (var el in ReadCards(reader, stats))
            yield return el;

        if (stats.BadLines > 0)
            _logger.LogWarning("Skipped {Count} unparseable lines in {Path}", stats.BadLines, Path.GetFileName(path));
    }

    internal sealed class ParseStats
    {
        public int BadLines;
    }

    /// <inheritdoc cref="EnumerateCards"/>
    internal static IEnumerable<JsonElement> ReadCards(TextReader reader, ParseStats? stats = null)
    {
        while (reader.Peek() is ' ' or '\t' or '\r' or '\n')
            reader.Read();

        if (reader.Peek() == '[')
        {
            using var doc = JsonDocument.Parse(
                reader.ReadToEnd(), new JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (var el in doc.RootElement.EnumerateArray())
                yield return el;
            yield break;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Tolerate the array file's punctuation in case a mixed file turns up.
            var trimmed = line.TrimEnd(',', '\r');
            if (trimmed.Length < 2 || trimmed is "[" or "]")
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                if (stats is not null)
                    stats.BadLines++;
                continue;
            }

            using (doc)
                yield return doc.RootElement;
        }
    }

    private void LoadOracleCards(string path)
    {
        var byOracleId = new Dictionary<string, CardDefinition>(32_000);
        var byName = new Dictionary<string, string>(32_000, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Parsing oracle_cards.json…");

        var rarityMap = new Dictionary<string, string>(32_000, StringComparer.OrdinalIgnoreCase);

        foreach (var card in EnumerateCards(path))
        {
            // Skip digital-only and non-English
            if (card.TryGetProperty("digital", out var dig) && dig.GetBoolean())
                continue;
            if (card.TryGetProperty("lang", out var lang) && lang.GetString() != "en")
                continue;

            var def = CardParser.Parse(card);
            if (def is null)
                continue;

            byOracleId[def.OracleId] = def;
            byName[def.Name] = def.OracleId;

            var rarity = card.TryGetProperty("rarity", out var rEl) ? rEl.GetString() ?? "" : "";
            if (rarity.Length > 0)
                rarityMap[def.OracleId] = rarity;
        }

        _byOracleId = byOracleId;
        _byName = byName;
        _rarityByOracleId = rarityMap;
    }

    private void LoadDefaultCards(string path)
    {
        var printings = new Dictionary<string, List<PrintingDto>>(32_000);
        var scryfallIdx = new Dictionary<string, PrintingEntry>(250_000);
        var setIdx = new Dictionary<string, List<string>>(500, StringComparer.OrdinalIgnoreCase);
        var setNames = new Dictionary<string, string>(500, StringComparer.OrdinalIgnoreCase);
        var setReleaseDates = new Dictionary<string, DateOnly>(500, StringComparer.OrdinalIgnoreCase);
        var setTypes = new Dictionary<string, string>(500, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Parsing default_cards.json…");

        foreach (var card in EnumerateCards(path))
        {
            if (card.TryGetProperty("digital", out var dig) && dig.GetBoolean())
                continue;
            if (card.TryGetProperty("lang", out var lang) && lang.GetString() != "en")
                continue;

            var id = card.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var oid = card.TryGetProperty("oracle_id", out var oidEl) ? oidEl.GetString() : null;
            var setCode = card.TryGetProperty("set", out var scEl) ? scEl.GetString() ?? "" : "";
            var setName = card.TryGetProperty("set_name", out var snEl) ? snEl.GetString() ?? "" : "";
            var releasedAt = card.TryGetProperty("released_at", out var raEl) ? raEl.GetString() : null;
            var setType = card.TryGetProperty("set_type", out var stEl) ? stEl.GetString() ?? "" : "";
            var num = card.TryGetProperty("collector_number", out var numEl) ? numEl.GetString() : null;

            if (id is null || oid is null)
                continue;

            string? imgSmall = null, imgNormal = null, imgLarge = null, imgArtCrop = null, imgNormalBack = null;
            if (card.TryGetProperty("image_uris", out var imgs))
            {
                if (imgs.TryGetProperty("small", out var s))
                    imgSmall = s.GetString();
                if (imgs.TryGetProperty("normal", out var n))
                    imgNormal = n.GetString();
                if (imgs.TryGetProperty("large", out var lg))
                    imgLarge = lg.GetString();
                if (imgs.TryGetProperty("art_crop", out var a))
                    imgArtCrop = a.GetString();
            }
            else if (card.TryGetProperty("card_faces", out var dfcImgs) && dfcImgs.GetArrayLength() > 0)
            {
                var f0 = dfcImgs[0];
                if (f0.TryGetProperty("image_uris", out var fi0))
                {
                    if (fi0.TryGetProperty("small", out var s))
                        imgSmall = s.GetString();
                    if (fi0.TryGetProperty("normal", out var n))
                        imgNormal = n.GetString();
                    if (fi0.TryGetProperty("large", out var lg))
                        imgLarge = lg.GetString();
                    if (fi0.TryGetProperty("art_crop", out var a))
                        imgArtCrop = a.GetString();
                }
                if (dfcImgs.GetArrayLength() > 1)
                {
                    var f1 = dfcImgs[1];
                    if (f1.TryGetProperty("image_uris", out var fi1) && fi1.TryGetProperty("normal", out var nb))
                        imgNormalBack = nb.GetString();
                }
            }

            // Only include cards that have artwork
            if (imgNormal is null)
                continue;

            // Per-printing text fields — fall back to card_faces[0/1] for DFCs
            JsonElement? face0 = null, face1 = null;
            if (card.TryGetProperty("card_faces", out var faces) && faces.GetArrayLength() > 0)
            {
                face0 = faces[0];
                if (faces.GetArrayLength() > 1)
                    face1 = faces[1];
            }

            string? artist = GetStr(card, "artist") ?? GetStr(face0, "artist");
            string? flavorText = GetStr(card, "flavor_text") ?? GetStr(face0, "flavor_text");
            string? manaCost = GetStr(card, "mana_cost") ?? GetStr(face0, "mana_cost");

            // For DFCs, combine both faces with the same separator CardParser uses so the
            // modal can split them when showing the flipped side's oracle text.
            string? oracleText = GetStr(card, "oracle_text");
            if (oracleText is null)
            {
                var f0Text = GetStr(face0, "oracle_text") ?? "";
                var f1Text = GetStr(face1, "oracle_text") ?? "";
                oracleText = f0Text.Length > 0 && f1Text.Length > 0
                    ? f0Text + "\n//\n" + f1Text
                    : f0Text.Length > 0 ? f0Text : null;
            }

            var dto = new PrintingDto
            {
                ScryfallId = id,
                SetCode = setCode,
                SetName = setName,
                CollectorNumber = num,
                ImageUriSmall = imgSmall,
                ImageUriNormal = imgNormal,
                ImageUriLarge = imgLarge,
                ImageUriNormalBack = imgNormalBack,
                Artist = artist,
                OracleText = oracleText,
                FlavorText = flavorText,
                ManaCost = manaCost,
            };

            if (!printings.TryGetValue(oid, out var list))
            {
                list = new List<PrintingDto>(4);
                printings[oid] = list;
            }
            list.Add(dto);

            scryfallIdx[id] = new PrintingEntry(oid, imgNormal, imgLarge, imgSmall, imgArtCrop, setCode, imgNormalBack);

            // Build set-code → oracle-id index and set name lookup
            if (setCode.Length > 0)
            {
                if (!setIdx.TryGetValue(setCode, out var setList))
                {
                    setList = new List<string>(32);
                    setIdx[setCode] = setList;
                }
                if (!setList.Contains(oid))
                    setList.Add(oid);
                if (setName.Length > 0)
                    setNames.TryAdd(setCode, setName);
                // released_at is per printing, and promos folded into a set can carry
                // later dates. The set released when its first card did, so keep the
                // earliest -- TryAdd would have frozen whichever card the file listed first.
                if (releasedAt is not null && DateOnly.TryParse(releasedAt, out var releaseDate))
                {
                    if (!setReleaseDates.TryGetValue(setCode, out var known) || releaseDate < known)
                        setReleaseDates[setCode] = releaseDate;
                }
                if (setType.Length > 0)
                    setTypes.TryAdd(setCode, setType);
            }
        }

        _printingsByOracleId = printings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _byScryfallId = scryfallIdx;
        _bySetCode = setIdx;
        _setNames = setNames;
        _setReleaseDates = setReleaseDates;
        _setTypes = setTypes;
    }

    private static string? GetStr(JsonElement? el, string prop)
    {
        if (el is null)
            return null;
        return el.Value.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    private CardDefinition EnrichWithFirstPrinting(CardDefinition oracle)
    {
        if (!_printingsByOracleId.TryGetValue(oracle.OracleId, out var printings) || printings.Length == 0)
            return oracle;
        var first = printings[0];
        return CardParser.WithPrinting(oracle, first.ImageUriNormal, first.ImageUriLarge, first.ImageUriSmall, null, first.SetCode);
    }

    // ---- Bulk-data metadata & download -----------------------------------

    private record BulkMeta(BulkEntry? OracleCards, BulkEntry? DefaultCards);
    private record BulkEntry(string Name, string DownloadUri, DateTimeOffset UpdatedAt);

    private async Task<BulkMeta?> FetchBulkMetaAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _metaClient.GetAsync("bulk-data", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            BulkEntry? oracle = null, defaults = null;
            var missingUri = new List<string>();
            foreach (var entry in data.EnumerateArray())
            {
                var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DateTimeOffset updatedAt = default;
                if (entry.TryGetProperty("updated_at", out var ua))
                    DateTimeOffset.TryParse(ua.GetString(), out updatedAt);

                // Scryfall replaced download_uri (a JSON array) with jsonl_download_uri
                // (gzipped newline-delimited JSON). Reading only the old field made every
                // entry look unusable, so refreshes quietly stopped for months.
                var uri = (entry.TryGetProperty("jsonl_download_uri", out var ju) ? ju.GetString() : null)
                          ?? (entry.TryGetProperty("download_uri", out var u) ? u.GetString() : null);

                if (uri is null)
                {
                    if (type is "oracle_cards" or "default_cards")
                        missingUri.Add(type);
                    continue;
                }
                var be = new BulkEntry(name, uri, updatedAt);
                if (type == "oracle_cards")
                    oracle = be;
                if (type == "default_cards")
                    defaults = be;
            }

            // Never let a schema change downgrade itself into "nothing to do".
            if (missingUri.Count > 0)
                _logger.LogError(
                    "Scryfall bulk-data entries {Types} have no recognised download URI — " +
                    "the API shape has changed and card data will go stale",
                    string.Join(", ", missingUri));

            return new BulkMeta(oracle, defaults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch bulk-data metadata");
            return null;
        }
    }

    /// <summary>
    /// A read-only stream that replays a few already-consumed bytes before continuing
    /// with the underlying stream. Lets us sniff a magic number without buffering the
    /// whole (hundreds of megabytes) body.
    /// </summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private int _prefixPos;

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefixPos < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - _prefixPos);
                prefix.Span.Slice(_prefixPos, n).CopyTo(buffer);
                _prefixPos += n;
                return n;
            }
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prefixPos < prefix.Length)
                return ValueTask.FromResult(Read(buffer.Span));
            return inner.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<bool> DownloadIfStaleAsync(BulkEntry entry, string fileName, CancellationToken ct)
    {
        var localPath = Path.Combine(_bulkDir, fileName);
        var metaPath = localPath + ".meta";

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
            await using (var raw = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = File.Create(tmpPath))
            {
                // Whether the body still needs decompressing depends on the URL, the
                // Content-Encoding header, and whether the handler already unwrapped it.
                // The first two bytes settle it without having to reason about all three.
                var head = new byte[2];
                int got = await raw.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, ct);
                await using var src = new PrefixedStream(head.AsMemory(0, got), raw);

                if (got == 2 && head[0] == 0x1f && head[1] == 0x8b)
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
