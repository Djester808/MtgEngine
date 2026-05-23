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

    public async Task<string?> IdentifyCardAsync(string imageBase64, string mediaType)
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
                            text = "This is a photo of a Magic: The Gathering card. What is the exact card name printed at the top of the card? Reply with ONLY the card name, nothing else. If you cannot clearly identify a Magic: The Gathering card or read its name, reply with exactly: UNKNOWN",
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
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic vision {Status}: {Body}", resp.StatusCode, err);
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?.Trim();

        return string.IsNullOrEmpty(text) || text.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }
}
