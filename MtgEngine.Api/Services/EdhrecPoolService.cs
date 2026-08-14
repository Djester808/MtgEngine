using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtgEngine.Api.Services;

/// <summary>A card EDHREC associates with a commander, and the section it came from.</summary>
/// <param name="Name">Card name as EDHREC reports it; not yet validated against Scryfall.</param>
/// <param name="Section">EDHREC section tag, e.g. "creatures", "utilitylands", "highsynergycards".</param>
public sealed record EdhrecCard(string Name, string Section);

public interface IEdhrecPoolService
{
    /// <summary>
    /// Cards the community actually plays with this commander, grouped by role.
    /// Returns an empty list when EDHREC has no page for the commander.
    /// </summary>
    Task<IReadOnlyList<EdhrecCard>> GetCommanderPoolAsync(string commanderName, CancellationToken ct = default);
}

/// <summary>
/// Fetches commander-specific card pools from EDHREC's public JSON.
/// </summary>
/// <remarks>
/// This is the retrieval step that makes a candidate pool tractable. The full corpus is
/// ~35k cards and even after filtering to a single colour identity it stays in the
/// thousands -- too many to put in a prompt. EDHREC collapses that to a few hundred
/// cards that are actually played with the commander, already grouped by role.
/// </remarks>
public sealed class EdhrecPoolService : IEdhrecPoolService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiCacheService _cache;
    private readonly ILogger<EdhrecPoolService> _logger;

    /// <summary>Bump to invalidate cached pools after changing the parse or filtering.</summary>
    private const string PoolVersion = "edhrec-pool-v1";

    /// <summary>EDHREC data moves on the order of weeks, so cache aggressively.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    public EdhrecPoolService(
        IHttpClientFactory httpFactory,
        IAiCacheService cache,
        ILogger<EdhrecPoolService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EdhrecCard>> GetCommanderPoolAsync(
        string commanderName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commanderName))
            return [];

        try
        {
            var cached = await _cache.GetOrCreateAsync(
                "edhrec-pool",
                PoolVersion,
                [commanderName],
                () => FetchAsync(commanderName, ct),
                CacheTtl);

            return cached ?? [];
        }
        catch (Exception ex)
        {
            // A transient EDHREC failure must NOT be cached as "no data" for 30 days — the
            // factory throwing means GetOrCreateAsync never wrote a cache entry. Callers
            // fall back to the Scryfall relevance pool for this one request only. (A genuine
            // 404 returns an empty list normally and IS cached — see FetchAsync.)
            _logger.LogWarning(ex, "EDHREC pool unavailable for {Commander}; using fallback", commanderName);
            return [];
        }
    }

    private async Task<List<EdhrecCard>> FetchAsync(string commanderName, CancellationToken ct)
    {
        var pool = new List<EdhrecCard>();
        var slug = ToSlug(commanderName);

        var http = _httpFactory.CreateClient("EdhrecApi");
        var resp = await http.GetAsync($"pages/commanders/{slug}.json", ct);

        // 404 is the one status that means "genuinely no page" — obscure or brand-new
        // commanders — and an empty pool for it is correct and cacheable.
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("EDHREC {Slug} -> 404 (no page)", slug);
            return pool;
        }
        // Any other non-success (5xx, 429, gateway) is transient — throw so the empty
        // result is not pinned in the 30-day cache.
        resp.EnsureSuccessStatusCode();

        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        if (!TryNav(doc, out var root, "container", "json_dict")
            || !root.TryGetProperty("cardlists", out var cardlists)
            || cardlists.ValueKind != JsonValueKind.Array)
        {
            // A one-off malformed payload is closer to a transient glitch than to real
            // "no data"; throw rather than caching an empty pool for a month.
            throw new InvalidOperationException($"EDHREC {slug}: unexpected payload shape");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in cardlists.EnumerateArray())
        {
            var tag = section.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";

            // Basic lands are handled separately by callers -- the land count depends
            // on the deck being built, not on what other people played.
            if (tag.Contains("basic", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!section.TryGetProperty("cardviews", out var cardviews)
                || cardviews.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var view in cardviews.EnumerateArray())
            {
                var name = view.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    pool.Add(new EdhrecCard(name, tag));
            }
        }

        _logger.LogInformation(
            "EDHREC pool for {Commander}: {Count} cards across {Sections} sections",
            commanderName, pool.Count, cardlists.GetArrayLength());

        return pool;
    }

    private static bool TryNav(JsonElement root, out JsonElement result, params string[] path)
    {
        result = root;
        foreach (var key in path)
            if (!result.TryGetProperty(key, out result))
                return false;
        return true;
    }

    /// <summary>"Atraxa, Praetors' Voice" -> "atraxa-praetors-voice"</summary>
    internal static string ToSlug(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"-+", "-").Trim('-');
        return s;
    }
}
