using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

public interface IManaFineTuneService
{
    Task<ManaFineTuneDto> GetFineTuneAsync(ManaFineTuneRequest request);
}

public sealed class ManaFineTuneService : IManaFineTuneService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<ManaFineTuneService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    public ManaFineTuneService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<ManaFineTuneService> logger)
    {
        _httpFactory = httpFactory;
        _apiKey      = config["Anthropic:ApiKey"] ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger      = logger;
    }

    public async Task<ManaFineTuneDto> GetFineTuneAsync(ManaFineTuneRequest req)
    {
        var colorNames = req.ActiveColors.Length > 0
            ? string.Join(", ", req.ActiveColors.Select(c => c switch
            {
                "W" => "White", "U" => "Blue", "B" => "Black", "R" => "Red", "G" => "Green", _ => c,
            }))
            : "Colorless";

        var deckContext = req.DeckCardNames.Length > 0
            ? $"\nCards in deck: {string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var format = string.IsNullOrWhiteSpace(req.Format) ? "unknown" : req.Format;

        var prompt = $$"""
            You are a Magic: The Gathering mana base expert.

            Deck info:
            - Format: {{format}}
            - Colors: {{colorNames}}
            - Current land count: {{req.CurrentLands}}
            - Recommended land count: {{req.RecommendedLands}}
            - Average CMC (non-land): {{req.AvgCmc:F1}}{{deckContext}}

            Provide specific mana base fine-tuning advice for this deck. Focus on:
            1. Whether the land count should be adjusted and why
            2. Specific dual lands, fetch lands, or utility lands by exact card name suited to the colors and format
            3. Any mana acceleration (ramp) if the CMC warrants it

            Respond with ONLY this exact JSON (no markdown, no extra text):
            {
              "advice": ["tip 1", "tip 2"],
              "landSuggestions": [
                {"name": "Exact Card Name", "reason": "why this helps"},
                ...
              ]
            }

            Rules:
            - advice: 2–4 concise, actionable tips tailored to this specific deck and format
            - landSuggestions: 4–6 specific land cards (exact MTG names) that fit the color identity and format
            - Only suggest lands NOT already in the deck
            - Only use real, officially printed Magic card names
            """;

        var body = new
        {
            model       = ModelId,
            max_tokens  = 800,
            temperature = 0,
            messages    = new[] { new { role = "user", content = prompt } },
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
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic mana-tune {Status}: {Body}", resp.StatusCode, err);
            throw new HttpRequestException($"{resp.StatusCode}: {err}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        var doc      = JsonDocument.Parse(respJson);
        var text     = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        text = ExtractJsonObject(text);

        var raw = JsonSerializer.Deserialize<RawFineTune>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new RawFineTune();

        return new ManaFineTuneDto
        {
            Advice          = raw.Advice,
            LandSuggestions = raw.LandSuggestions
                .Select(l => new ManaLandSuggestion { Name = l.Name, Reason = l.Reason })
                .ToArray(),
        };
    }

    private sealed class RawFineTune
    {
        [JsonPropertyName("advice")]          public string[]            Advice          { get; set; } = [];
        [JsonPropertyName("landSuggestions")] public RawLandSuggestion[] LandSuggestions { get; set; } = [];
    }

    private sealed class RawLandSuggestion
    {
        [JsonPropertyName("name")]   public string Name   { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    private static string ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return "{}";
        int depth = 0; bool inString = false; bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)              { escaped = false; continue; }
            if (c == '\\' && inString){ escaped = true;  continue; }
            if (c == '"')             { inString = !inString; continue; }
            if (inString)               continue;
            if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return text[start..(i + 1)]; }
        }
        return text[start..];
    }
}
