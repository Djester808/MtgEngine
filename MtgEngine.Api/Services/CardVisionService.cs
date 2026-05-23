using System.Text;
using System.Text.Json;

namespace MtgEngine.Api.Services;

public sealed class CardVisionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<CardVisionService> _logger;

    private const string ModelId = "claude-sonnet-4-6";

    public CardVisionService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<CardVisionService> logger)
    {
        _httpFactory = httpFactory;
        _apiKey =
            config["Anthropic:ApiKey"]
            ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger = logger;
    }

    public async Task<(string? CardName, string? Error)> IdentifyCardAsync(string imageBase64, string mediaType)
    {
        var body = new
        {
            model = ModelId,
            max_tokens = 64,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = mediaType,
                                data = imageBase64,
                            },
                        },
                        new
                        {
                            type = "text",
                            text = "Someone is holding a Magic: The Gathering card up to the camera. Find the card in the image and read the card name printed at the top. Reply with ONLY the card name — no punctuation, no explanation. If you genuinely cannot read any card name, reply with exactly: UNKNOWN",
                        },
                    },
                },
            },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            ),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(req);
        var body2 = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic vision {Status}: {Body}", resp.StatusCode, body2);
            return (null, $"Claude API error {(int)resp.StatusCode}: {body2}");
        }

        using var doc = JsonDocument.Parse(body2);
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?.Trim();

        _logger.LogInformation("Claude vision response: {Text}", text);

        return string.IsNullOrEmpty(text) || text.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? (null, null)
            : (text, null);
    }
}
