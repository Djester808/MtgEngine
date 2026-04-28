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

    private const string ModelId = "claude-haiku-4-5-20251001";

    public SynergyService(
        MtgEngineDbContext db,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<SynergyService> logger)
    {
        _db         = db;
        _httpFactory = httpFactory;
        _apiKey     = config["Anthropic:ApiKey"] ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger     = logger;
    }

    public async Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request)
    {
        var cached = await _db.CardSynergyScores.FirstOrDefaultAsync(s =>
            s.CommanderOracleId == request.CommanderOracleId &&
            s.CardOracleId      == request.CardOracleId);

        if (cached != null)
            return new SynergyResultDto { Score = cached.Score, Reason = cached.Reason };

        var result = await CallAnthropicAsync(request);

        _db.CardSynergyScores.Add(new CardSynergyScore
        {
            CommanderOracleId = request.CommanderOracleId,
            CardOracleId      = request.CardOracleId,
            Score             = result.Score,
            Reason            = result.Reason,
            ModelVersion      = ModelId,
        });

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cache synergy score"); }

        return result;
    }

    private async Task<SynergyResultDto> CallAnthropicAsync(SynergyRequest req)
    {
        var prompt = $$"""
            You are a Magic: The Gathering expert. Rate how well a card synergizes with a commander for the Commander/EDH format.

            Commander: {{req.CommanderName}}
            Commander oracle text: {{req.CommanderText}}

            Card to evaluate: {{req.CardName}}
            Card oracle text: {{req.CardText}}

            Respond with ONLY valid JSON in exactly this format (no markdown, no extra text):
            {"score": <integer 0-100>, "reason": "<one concise sentence explaining why>"}

            Where 0 = no synergy whatsoever, 100 = exceptional synergy.
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
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync();
        var doc      = JsonDocument.Parse(respJson);
        var text     = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        // Strip markdown fences if the model wraps the JSON
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
