using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ISynergyService
{
    Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request);

    /// <summary>
    /// Scores every main-deck card against the commander in one call, and persists the
    /// results to the same cache the single-card path reads.
    /// </summary>
    Task<DeckScoreDto> ScoreDeckAsync(Guid deckId, string userId);

    /// <summary>
    /// Scores an arbitrary set of cards against a commander, reading the cache first and
    /// asking the model only about the ones missing from it.
    /// </summary>
    Task<ScoredCardDto[]> ScoreCardsAsync(string commanderOracleId, IReadOnlyList<string> cardOracleIds);
}

public sealed class SynergyService : ISynergyService
{
    private readonly MtgEngineDbContext _db;
    private readonly ICollectionService _collection;
    private readonly IScryfallService _scryfall;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<SynergyService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";
    private const string CacheVersion = "claude-haiku-4-5-20251001-deck-v1";

    public SynergyService(
        MtgEngineDbContext db,
        ICollectionService collection,
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<SynergyService> logger)
    {
        _db = db;
        _collection = collection;
        _scryfall = scryfall;
        _httpFactory = httpFactory;
        _apiKey = SecretConfig.AnthropicApiKey(config);
        _logger = logger;
    }

    public async Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request)
    {
        var cached = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId == request.CardOracleId &&
            s.ModelVersion == CacheVersion);

        if (cached != null)
            return new SynergyResultDto { Score = cached.Score, Reason = cached.Reason };

        var result = await CallAnthropicAsync(request);

        var entity = new CardSynergyScore
        {
            CommanderOracleId = request.CommanderOracleId,
            CardOracleId = request.CardOracleId,
            Score = result.Score,
            Reason = result.Reason,
            ModelVersion = CacheVersion,
        };

        // Upsert — a stale entry from an older cache version may already exist for this pair
        var stale = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId == request.CardOracleId);

        if (stale != null)
        {
            stale.Score = result.Score;
            stale.Reason = result.Reason;
            stale.ModelVersion = CacheVersion;
            stale.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.CardSynergyScores.Add(entity);
        }

        try
        { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cache synergy score"); }

        return result;
    }

    // ---- Batched deck scoring ---------------------------------------

    public async Task<DeckScoreDto> ScoreDeckAsync(Guid deckId, string userId)
    {
        var deck = await _collection.GetDeckAsync(deckId, userId)
            ?? throw new ResourceNotFoundException($"Deck not found: {deckId}");

        if (string.IsNullOrWhiteSpace(deck.CommanderOracleId))
            throw new InvalidResourceStateException("Deck has no commander to score against.");

        var cmdDef = await _scryfall.GetByOracleIdAsync(deck.CommanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {deck.CommanderOracleId}");

        // Score distinct non-commander main-deck cards. Basic lands are excluded:
        // scoring 30 Swamps says nothing useful and would dominate the average.
        var cards = deck.Cards
            .Where(c => (c.Board ?? "main") == "main"
                        && !string.Equals(c.OracleId, deck.CommanderOracleId, StringComparison.OrdinalIgnoreCase)
                        && c.CardDetails is not null
                        && !c.CardDetails.Supertypes.Contains("Basic"))
            .GroupBy(c => c.OracleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.CardDetails!.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cards.Length == 0)
            return new DeckScoreDto();

        var scores = await CallDeckScoringAsync(
            cmdDef.Name,
            cmdDef.OracleText ?? string.Empty,
            [.. cards.Select(c => new ScorableCard(
                c.OracleId, c.CardDetails!.Name, c.CardDetails.ManaCost, c.CardDetails.OracleText))]);

        var scored = cards
            .Select(c =>
            {
                var name = c.CardDetails!.Name;
                scores.TryGetValue(name, out var s);
                return new ScoredCardDto
                {
                    OracleId = c.OracleId,
                    Name = name,
                    Score = Math.Clamp(s?.Score ?? 0, 0, 100),
                    Reason = s?.Reason ?? string.Empty,
                };
            })
            .Where(s => s.Score > 0)   // unscored cards would drag the average to zero
            .ToArray();

        await PersistScoresAsync(deck.CommanderOracleId, scored);

        return new DeckScoreDto
        {
            Cards = scored,
            AverageScore = scored.Length == 0 ? 0 : (int)Math.Round(scored.Average(s => s.Score)),
            WeakestCards = [.. scored.Where(s => s.Score < DeckScoreDto.WeakThreshold).OrderBy(s => s.Score)],
        };
    }

    /// <summary>
    /// The fields the scoring prompt needs, so deck cards and loose candidates can share
    /// one code path.
    /// </summary>
    private readonly record struct ScorableCard(string OracleId, string Name, string ManaCost, string? OracleText);

    public async Task<ScoredCardDto[]> ScoreCardsAsync(
        string commanderOracleId, IReadOnlyList<string> cardOracleIds)
    {
        var wanted = cardOracleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
            return [];

        var cmdDef = await _scryfall.GetByOracleIdAsync(commanderOracleId)
            ?? throw new ResourceNotFoundException($"Commander not found: {commanderOracleId}");

        // Cache first. Browsing pages the same cards past repeatedly, and a page that is
        // fully cached must not cost an API call.
        var cached = await _db.CardSynergyScores
            .Where(s => s.CommanderOracleId == commanderOracleId
                        && s.ModelVersion == CacheVersion
                        && wanted.Contains(s.CardOracleId))
            .ToDictionaryAsync(s => s.CardOracleId, StringComparer.OrdinalIgnoreCase);

        var results = new List<ScoredCardDto>(wanted.Count);
        var missing = new List<ScorableCard>();

        foreach (var id in wanted)
        {
            if (cached.TryGetValue(id, out var hit))
            {
                results.Add(new ScoredCardDto
                {
                    OracleId = id,
                    Name = (await _scryfall.GetByOracleIdAsync(id))?.Name ?? string.Empty,
                    Score = hit.Score,
                    Reason = hit.Reason,
                });
                continue;
            }

            var def = await _scryfall.GetByOracleIdAsync(id);
            if (def is not null)
                missing.Add(new ScorableCard(id, def.Name, def.ManaCostRaw, def.OracleText));
        }

        if (missing.Count > 0)
        {
            var scores = await CallDeckScoringAsync(
                cmdDef.Name, cmdDef.OracleText ?? string.Empty, missing);

            var fresh = missing
                .Select(c =>
                {
                    scores.TryGetValue(c.Name, out var s);
                    return new ScoredCardDto
                    {
                        OracleId = c.OracleId,
                        Name = c.Name,
                        Score = Math.Clamp(s?.Score ?? 0, 0, 100),
                        Reason = s?.Reason ?? string.Empty,
                    };
                })
                .Where(s => s.Score > 0)
                .ToArray();

            await PersistScoresAsync(commanderOracleId, fresh);
            results.AddRange(fresh);
        }

        _logger.LogInformation(
            "Scored {Total} cards for {Commander}: {Cached} cached, {Fresh} from the model",
            wanted.Count, cmdDef.Name, cached.Count, missing.Count);

        return [.. results];
    }

    private async Task<Dictionary<string, SynergyJson>> CallDeckScoringAsync(
        string commanderName, string commanderText, IReadOnlyList<ScorableCard> cards)
    {
        // Include each card's rules text. Given names alone the model describes cards
        // from memory and gets them wrong -- it claimed Goblin Bombardment sacrifices
        // treasures when it sacrifices a creature.
        var cardList = string.Join("\n", cards.Select(c =>
        {
            var text = string.IsNullOrWhiteSpace(c.OracleText)
                ? "(no rules text)"
                : c.OracleText.Replace("\n", " ");
            return $"- {c.Name} | {c.ManaCost} | {text}";
        }));

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert reviewing a finished deck.

            Commander: {{commanderName}}
            Commander oracle text: {{commanderText}}

            Score how well each card below fits THIS deck, led by the commander's strategy
            but accounting for how the cards support each other.

            Cards ({{cards.Count}}), as "name | mana cost | rules text":
            {{cardList}}

            Respond with ONLY valid JSON in exactly this shape (no markdown, no extra text):
            {"scores":[{"name":"<exact card name as given>","score":<integer 0-100>,"reason":"<one short sentence>"}]}

            Rules:
            - Include every card listed above, exactly once, using the name exactly as given.
            - 0 = no synergy whatsoever, 100 = exceptional fit.
            - Be discriminating: a generic good card in a deck it does nothing special for
              should score in the 40s, not the 80s. Reserve 85+ for genuine synergy.
            - Keep each reason under 15 words.
            - Base every reason on the rules text given above, never on recollection of
              the card. If the text does not support a claim, do not make it.
            """;

        var body = new
        {
            model = ModelId,
            // ~99 cards x (name + score + short reason); 8k leaves room without truncating.
            max_tokens = 8000,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(httpReq);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic deck-scoring {Status}: {Body}", resp.StatusCode, errBody);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, errBody);
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        var parsed = AnthropicResponse.DeserializeJson<DeckScoreJson>(respJson);

        var map = new Dictionary<string, SynergyJson>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in parsed?.Scores ?? [])
            if (!string.IsNullOrWhiteSpace(s.Name))
                map[s.Name] = new SynergyJson { Score = s.Score, Reason = s.Reason };

        if (map.Count < cards.Count)
        {
            _logger.LogWarning(
                "Deck scoring returned {Got}/{Expected} cards for {Commander}",
                map.Count, cards.Count, commanderName);
        }

        return map;
    }

    /// <summary>Writes batch results into the same cache the single-card path reads.</summary>
    private async Task PersistScoresAsync(string commanderOracleId, ScoredCardDto[] scored)
    {
        try
        {
            // List, not array: on .NET 10 `string[].Contains` binds to the span-based
            // MemoryExtensions overload, which EF cannot translate to SQL.
            var cardIds = scored.Select(s => s.OracleId).ToList();
            var existing = await _db.CardSynergyScores
                .Where(s => s.CommanderOracleId == commanderOracleId && cardIds.Contains(s.CardOracleId))
                .ToDictionaryAsync(s => s.CardOracleId, StringComparer.OrdinalIgnoreCase);

            foreach (var s in scored)
            {
                if (existing.TryGetValue(s.OracleId, out var row))
                {
                    row.Score = s.Score;
                    row.Reason = Truncate(s.Reason, 500);
                    row.ModelVersion = CacheVersion;
                    row.CreatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.CardSynergyScores.Add(new CardSynergyScore
                    {
                        CommanderOracleId = commanderOracleId,
                        CardOracleId = s.OracleId,
                        Score = s.Score,
                        Reason = Truncate(s.Reason, 500),
                        ModelVersion = CacheVersion,
                    });
                }
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Persisting is a cache warm-up; never fail the caller over it.
            _logger.LogWarning(ex, "Failed to persist batch synergy scores");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private sealed class DeckScoreJson
    {
        [JsonPropertyName("scores")] public DeckScoreEntry[] Scores { get; set; } = [];
    }

    private sealed class DeckScoreEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    // ---- Single-card scoring ----------------------------------------

    private async Task<SynergyResultDto> CallAnthropicAsync(SynergyRequest req)
    {
        var deckContext = req.DeckCardNames.Length > 0
            ? $"\n\nOther cards already in the deck ({req.DeckCardNames.Length}):\n{string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert. Evaluate how well a card fits into a specific deck.

            Commander (primary focus): {{req.CommanderName}}
            Commander oracle text: {{req.CommanderText}}{{deckContext}}

            Card to evaluate: {{req.CardName}}
            Card oracle text: {{req.CardText}}

            Score how well this card fits the deck. The commander's strategy is the most important factor, but also consider how the card supports or complements the other cards already in the deck.

            Respond with ONLY valid JSON in exactly this format (no markdown, no extra text):
            {"score": <integer 0-100>, "reason": "<one concise sentence explaining the fit>"}

            Where 0 = no synergy whatsoever, 100 = exceptional fit.
            """;

        var body = new
        {
            model = ModelId,
            max_tokens = 256,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(httpReq);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic API {Status}: {Body}", resp.StatusCode, errBody);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, errBody);
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        var parsed = AnthropicResponse.DeserializeJson<SynergyJson>(respJson);

        return new SynergyResultDto
        {
            Score = Math.Clamp(parsed?.Score ?? 0, 0, 100),
            Reason = parsed?.Reason ?? string.Empty,
        };
    }

    private sealed class SynergyJson
    {
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
