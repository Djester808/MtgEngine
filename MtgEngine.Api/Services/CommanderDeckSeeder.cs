using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class CommanderDeckSeeder
{
    private readonly MtgEngineDbContext _db;
    private readonly IScryfallService _scryfall;
    private readonly HttpClient _scryfallHttp;
    private readonly HttpClient _edhrecHttp;
    private readonly ILogger<CommanderDeckSeeder> _logger;

    private const string SeedUsername = "CommunityBot";
    private const string SeedEmail = "communitybot@mtgengine.local";

    private static readonly string[] Adjectives =
    [
        "Optimized", "Budget", "Competitive", "Casual", "Upgraded", "Tuned",
        "Focused", "Streamlined", "Refined", "Spicy",
    ];

    private static readonly Dictionary<string, string> BasicLandNames = new()
    {
        ["W"] = "Plains",
        ["U"] = "Island",
        ["B"] = "Swamp",
        ["R"] = "Mountain",
        ["G"] = "Forest",
    };

    public CommanderDeckSeeder(
        MtgEngineDbContext db,
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        ILogger<CommanderDeckSeeder> logger)
    {
        _db = db;
        _scryfall = scryfall;
        _scryfallHttp = httpFactory.CreateClient("ScryfallApi");
        _edhrecHttp = httpFactory.CreateClient("EdhrecApi");
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task<string> SeedAsync(
        int commanderCount = 50,
        int decksPerCommander = 10,
        CancellationToken ct = default)
    {
        var userId = await EnsureSeedUserAsync(ct);

        var existing = await _db.ForumPosts.CountAsync(f => f.AuthorId == userId, ct);
        if (existing >= (commanderCount * decksPerCommander) / 2)
            return $"Already seeded ({existing} decks). Skipping.";

        // Pre-resolve basic land oracle IDs from bulk data
        var basicLandIds = await ResolveLandIdsAsync(ct);
        _logger.LogInformation("Resolved {N} basic land IDs", basicLandIds.Count);

        var commanders = await FetchTopCommandersAsync(commanderCount, ct);
        _logger.LogInformation("Fetched {N} commanders from Scryfall", commanders.Count);

        int created = 0;
        var rng = new Random(42);

        for (int cmdIdx = 0; cmdIdx < commanders.Count; cmdIdx++)
        {
            var cmd = commanders[cmdIdx];
            ct.ThrowIfCancellationRequested();
            try
            {
                var pool = await BuildCardPoolAsync(cmd, ct);
                if (pool.Count < 25)
                {
                    _logger.LogWarning("Skipping {Name}: pool only {N} cards", cmd.Name, pool.Count);
                    continue;
                }

                // Vary deck count by rank: top commanders get proportionally more decks
                // Rank 1 = decksPerCommander, rank 50 = max(3, decksPerCommander/2)
                var rankRatio = 1.0 - (cmdIdx / (double)Math.Max(1, commanders.Count - 1)) * 0.5;
                var deckCount = Math.Max(3, (int)Math.Round(decksPerCommander * rankRatio));

                for (int i = 0; i < deckCount; i++)
                {
                    await CreateDeckAsync(cmd, pool, basicLandIds, i, deckCount, userId, rng);
                    created++;
                }

                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Seeded {N} decks for {Name} (rank {R}, pool: {P})", deckCount, cmd.Name, cmdIdx + 1, pool.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed seeding {Name}", cmd.Name);
            }
        }

        return $"Done. Created {created} decks across {commanders.Count} commanders.";
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> EnsureSeedUserAsync(CancellationToken ct)
    {
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == SeedUsername, ct);
        if (existing is not null)
            return existing.Id.ToString();

        var user = new User
        {
            Username = SeedUsername,
            Email = SeedEmail,
            PasswordHash = "SEEDED_NO_LOGIN",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Created seed user {Id}", user.Id);
        return user.Id.ToString();
    }

    private async Task<Dictionary<string, string>> ResolveLandIdsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var (color, landName) in BasicLandNames)
        {
            var def = await _scryfall.GetByNameAsync(landName);
            if (def is not null)
                result[color] = def.OracleId;
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private record CommanderInfo(
        string OracleId,
        string ScryfallId,
        string Name,
        string[] ColorIdentity,
        string? ImageUri);

    public async Task<string> ClearSeedAsync(CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == SeedUsername, ct);
        if (user is null)
            return "No seed user found — nothing to clear.";

        var userId = user.Id.ToString();

        var postIds = await _db.ForumPosts
            .Where(f => f.AuthorId == userId)
            .Select(f => f.DeckId)
            .ToListAsync(ct);

        var deleted = postIds.Count;

        // Delete forum posts (cascades to comments)
        await _db.ForumPosts.Where(f => f.AuthorId == userId).ExecuteDeleteAsync(ct);

        // Delete collections + cascade to CollectionCards
        await _db.Collections.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);

        return $"Cleared {deleted} seeded decks.";
    }

    private async Task<List<CommanderInfo>> FetchTopCommandersAsync(int count, CancellationToken ct)
    {
        // Try EDHREC's top commanders page first (actual EDHREC ranking)
        var list = await TryFetchEdhrecCommandersAsync(count, ct);
        if (list.Count >= count / 2)
            return list;

        _logger.LogWarning("EDHREC commanders page insufficient ({N}), falling back to Scryfall", list.Count);

        // Fallback: Scryfall search sorted by edhrec rank (card rank, not commander-specific)
        string? url = "cards/search?q=is%3Acommander+game%3Apaper&order=edhrec&dir=asc&unique=cards";
        while (list.Count < count && url is not null)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct);

            try
            {
                var resp = await _scryfallHttp.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                    break;

                var doc = await JsonSerializer.DeserializeAsync<JsonElement>(
                    await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.TryGetProperty("data", out var data))
                    break;

                foreach (var card in data.EnumerateArray())
                {
                    if (list.Count >= count)
                        break;
                    var info = ParseCommanderCard(card);
                    if (info is not null && list.All(x => x.OracleId != info.OracleId))
                        list.Add(info);
                }

                url = null;
                if (list.Count < count
                    && doc.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean()
                    && doc.TryGetProperty("next_page", out var nextPage))
                {
                    var next = nextPage.GetString();
                    if (next is not null)
                        url = new Uri(next).PathAndQuery.TrimStart('/');
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scryfall fallback commander fetch failed");
                break;
            }
        }

        return list;
    }

    private async Task<List<CommanderInfo>> TryFetchEdhrecCommandersAsync(int count, CancellationToken ct)
    {
        var list = new List<CommanderInfo>();
        try
        {
            await Task.Delay(400, ct);
            var resp = await _edhrecHttp.GetAsync("pages/commanders.json", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("EDHREC commanders page returned {Status}", resp.StatusCode);
                return list;
            }

            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            // Try multiple paths EDHREC uses
            JsonElement cardlists = default;
            bool found = TryNav(doc, out cardlists, "container", "json_dict", "cardlists")
                      || TryNav(doc, out cardlists, "container", "json_dict", "commanders");

            if (!found)
                return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in cardlists.EnumerateArray())
            {
                if (list.Count >= count)
                    break;

                // Could be a "cardviews" section or direct commander entries
                var items = section.ValueKind == JsonValueKind.Array
                    ? section
                    : (section.TryGetProperty("cardviews", out var cv) ? cv : default);

                if (items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (list.Count >= count)
                        break;
                    var name = GetStr(item, "name");
                    if (name is null || !seen.Add(name))
                        continue;

                    var def = await _scryfall.GetByNameAsync(name);
                    if (def is null)
                        continue;

                    var colors = def.ColorIdentity
                        .Select(c => c switch
                        {
                            MtgEngine.Domain.Enums.ManaColor.White => "W",
                            MtgEngine.Domain.Enums.ManaColor.Blue => "U",
                            MtgEngine.Domain.Enums.ManaColor.Black => "B",
                            MtgEngine.Domain.Enums.ManaColor.Red => "R",
                            MtgEngine.Domain.Enums.ManaColor.Green => "G",
                            _ => "C",
                        })
                        .ToArray();

                    if (colors.Length == 0)
                        continue;

                    list.Add(new CommanderInfo(def.OracleId, def.OracleId, def.Name, colors, def.ImageUriNormal));
                }
            }

            _logger.LogInformation("EDHREC commanders page: {N} commanders", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDHREC commanders page fetch failed");
        }
        return list;
    }

    private CommanderInfo? ParseCommanderCard(JsonElement card)
    {
        var oracleId = GetStr(card, "oracle_id");
        var scryfallId = GetStr(card, "id");
        var name = GetStr(card, "name");
        if (oracleId is null || scryfallId is null || name is null)
            return null;

        var colors = Array.Empty<string>();
        if (card.TryGetProperty("color_identity", out var ci))
            colors = [.. ci.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => s.Length > 0)];

        if (colors.Length == 0)
            return null;

        string? imgUri = null;
        if (card.TryGetProperty("image_uris", out var imgs))
        {
            if (imgs.TryGetProperty("art_crop", out var art))
                imgUri = art.GetString();
            else if (imgs.TryGetProperty("normal", out var norm))
                imgUri = norm.GetString();
        }

        return new CommanderInfo(oracleId, scryfallId, name, colors, imgUri);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private record CardEntry(string OracleId, string Name);

    private async Task<List<CardEntry>> BuildCardPoolAsync(CommanderInfo cmd, CancellationToken ct)
    {
        var pool = await TryFetchEdhrecPoolAsync(cmd.Name, ct);

        if (pool.Count >= 40)
            return pool;

        _logger.LogWarning("EDHREC pool small ({N}) for {Name}, supplementing with Scryfall", pool.Count, cmd.Name);
        await SupplementWithScryfallAsync(pool, cmd, ct);
        return pool;
    }

    private async Task<List<CardEntry>> TryFetchEdhrecPoolAsync(string commanderName, CancellationToken ct)
    {
        var pool = new List<CardEntry>();
        var slug = ToEdhrecSlug(commanderName);

        try
        {
            await Task.Delay(400, ct);
            var resp = await _edhrecHttp.GetAsync($"pages/commanders/{slug}.json", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("EDHREC {Slug} → {Status}", slug, resp.StatusCode);
                return pool;
            }

            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            // Try: container.json_dict.cardlists
            if (!TryNav(doc, out var root, "container", "json_dict"))
                return pool;
            if (!root.TryGetProperty("cardlists", out var cardlists))
                return pool;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in cardlists.EnumerateArray())
            {
                var tag = section.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
                if (tag.Contains("basic", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!section.TryGetProperty("cardviews", out var cardviews))
                    continue;

                foreach (var view in cardviews.EnumerateArray())
                {
                    var name = GetStr(view, "name");
                    if (name is null || !seen.Add(name))
                        continue;

                    var def = await _scryfall.GetByNameAsync(name);
                    if (def is not null)
                        pool.Add(new CardEntry(def.OracleId, def.Name));
                }
            }

            _logger.LogInformation("EDHREC pool for {Name}: {N} cards", commanderName, pool.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDHREC fetch failed for {Name}", commanderName);
        }

        return pool;
    }

    private async Task SupplementWithScryfallAsync(List<CardEntry> pool, CommanderInfo cmd, CancellationToken ct)
    {
        var existingIds = pool.Select(c => c.OracleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Color identity query: identity<=WUBRG legal:commander not:commander not:basic
        var colorPart = string.Join("", cmd.ColorIdentity.Select(c => $"{{{c}}}"));
        var colorArg = Uri.EscapeDataString(
            $"identity<={colorPart} legal:commander not:commander not:basic");
        var url = $"cards/search?q={colorArg}&order=edhrec&unique=cards";

        try
        {
            await Task.Delay(150, ct);
            var resp = await _scryfallHttp.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return;

            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.TryGetProperty("data", out var data))
                return;

            foreach (var card in data.EnumerateArray())
            {
                var oId = GetStr(card, "oracle_id");
                var name = GetStr(card, "name");
                if (oId is null || name is null || existingIds.Contains(oId))
                    continue;
                pool.Add(new CardEntry(oId, name));
                existingIds.Add(oId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scryfall supplement failed for {Name}", cmd.Name);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task CreateDeckAsync(
        CommanderInfo cmd,
        List<CardEntry> pool,
        Dictionary<string, string> landIds,
        int index,
        int total,
        string userId,
        Random rng)
    {
        // Spread dates: most decks in last 6 months, taper off further back
        var maxDaysBack = 365;
        var daysBack = (int)(Math.Pow(rng.NextDouble(), 1.5) * maxDaysBack);
        var publishedAt = DateTime.UtcNow.AddDays(-daysBack);

        var adjective = Adjectives[index % Adjectives.Length];
        var deckName = $"{cmd.Name} — {adjective}";
        var colorStr = string.Join("", cmd.ColorIdentity.Select(c => $"{{{c}}}"));
        var desc = $"A {adjective.ToLower()} {colorStr} build of {cmd.Name}. " +
                   "Built from popular EDHREC picks.";

        var colorJson = JsonSerializer.Serialize(cmd.ColorIdentity);

        // Build color tags
        var colorTags = cmd.ColorIdentity.Select(c => c switch
        {
            "W" => "white",
            "U" => "blue",
            "B" => "black",
            "R" => "red",
            "G" => "green",
            _ => "colorless",
        }).ToList();

        var collection = new Collection
        {
            UserId = userId,
            Name = deckName,
            Description = desc,
            IsDeck = true,
            Format = "commander",
            CommanderOracleId = cmd.OracleId,
            CoverUri = cmd.ImageUri,
            Tags = [adjective.ToLower(), .. colorTags],
            CreatedAt = publishedAt,
            UpdatedAt = publishedAt,
        };
        _db.Collections.Add(collection);

        // Commander slot (board = "commander")
        _db.CollectionCards.Add(new CollectionCard
        {
            CollectionId = collection.Id,
            OracleId = cmd.OracleId,
            ScryfallId = null,
            Quantity = 1,
            Board = "commander",
            AddedAt = publishedAt,
        });

        // Sample 63 non-land cards from pool (randomized per deck)
        var sampled = pool
            .OrderBy(_ => rng.Next())
            .Take(63)
            .ToList();

        foreach (var card in sampled)
        {
            _db.CollectionCards.Add(new CollectionCard
            {
                CollectionId = collection.Id,
                OracleId = card.OracleId,
                ScryfallId = null,
                Quantity = 1,
                Board = "main",
                AddedAt = publishedAt,
            });
        }

        // Basic lands: 36 total distributed by color identity
        var deckColors = cmd.ColorIdentity.Where(c => landIds.ContainsKey(c)).ToList();
        if (deckColors.Count == 0)
            deckColors = landIds.Keys.Take(1).ToList();

        int totalLands = 36;
        int perColor = totalLands / deckColors.Count;
        int extraLands = totalLands - perColor * deckColors.Count;

        foreach (var color in deckColors)
        {
            var qty = perColor + (extraLands-- > 0 ? 1 : 0);
            _db.CollectionCards.Add(new CollectionCard
            {
                CollectionId = collection.Id,
                OracleId = landIds[color],
                ScryfallId = null,
                Quantity = qty,
                Board = "main",
                AddedAt = publishedAt,
            });
        }

        var post = new ForumPost
        {
            DeckId = collection.Id,
            AuthorId = userId,
            AuthorUsername = SeedUsername,
            Description = desc,
            ColorIdentityJson = colorJson,
            PublishedAt = publishedAt,
            UpdatedAt = publishedAt,
        };
        _db.ForumPosts.Add(post);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static bool TryNav(JsonElement root, out JsonElement result, params string[] path)
    {
        result = root;
        foreach (var key in path)
            if (!result.TryGetProperty(key, out result))
                return false;
        return true;
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    private static string ToEdhrecSlug(string name)
    {
        // "Atraxa, Praetor's Voice" → "atraxa-praetors-voice"
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"-+", "-").Trim('-');
        return s;
    }
}
