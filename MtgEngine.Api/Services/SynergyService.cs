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
}

public sealed class SynergyService : ISynergyService
{
    private readonly MtgEngineDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<SynergyService> _logger;

    private const string ModelId      = "claude-haiku-4-5-20251001";
    private const string CacheVersion = "claude-haiku-4-5-20251001-deck-v1";

    public SynergyService(
        MtgEngineDbContext db,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<SynergyService> logger)
    {
        _db          = db;
        _httpFactory = httpFactory;
        _apiKey      = config["Anthropic:ApiKey"] ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger      = logger;
    }

    public async Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request)
    {
        var cached = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId      == request.CardOracleId &&
            s.ModelVersion      == CacheVersion);

        if (cached != null)
            return new SynergyResultDto { Score = cached.Score, Reason = cached.Reason };

        var result = await CallAnthropicAsync(request);

        var entity = new CardSynergyScore
        {
            CommanderOracleId = request.CommanderOracleId,
            CardOracleId      = request.CardOracleId,
            Score             = result.Score,
            Reason            = result.Reason,
            ModelVersion      = CacheVersion,
        };

        // Upsert — a stale entry from an older cache version may already exist for this pair
        var stale = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId      == request.CardOracleId);

        if (stale != null)
        {
            stale.Score        = result.Score;
            stale.Reason       = result.Reason;
            stale.ModelVersion = CacheVersion;
            stale.CreatedAt    = DateTime.UtcNow;
        }
        else
        {
            _db.CardSynergyScores.Add(entity);
        }

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cache synergy score"); }

        return result;
    }

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
            model      = ModelId,
            max_tokens = 256,
            messages   = new[] { new { role = "user", content = prompt } },
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
            throw new HttpRequestException($"{resp.StatusCode}: {errBody}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        var doc      = JsonDocument.Parse(respJson);
        var text     = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        text = text.Trim();
        if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
        if (text.EndsWith("```"))  text = text[..text.LastIndexOf("```")].TrimEnd();

        var parsed = JsonSerializer.Deserialize<SynergyJson>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return new SynergyResultDto
        {
            Score  = Math.Clamp(parsed?.Score ?? 0, 0, 100),
            Reason = parsed?.Reason ?? string.Empty,
        };
    }

    private sealed class SynergyJson
    {
        [JsonPropertyName("score")]  public int    Score  { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
