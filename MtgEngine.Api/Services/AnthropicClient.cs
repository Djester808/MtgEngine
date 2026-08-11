using System.Text;
using System.Text.Json;

namespace MtgEngine.Api.Services;

/// <summary>
/// One call to the Anthropic Messages API.
/// </summary>
/// <param name="Model">Model id, e.g. <c>claude-haiku-4-5-20251001</c>.</param>
/// <param name="MaxTokens">Output ceiling for this call.</param>
/// <param name="Messages">
/// The <c>messages</c> array, as objects shaped like the wire format. Left loosely typed
/// because the blocks genuinely vary -- a plain string for text-only calls, an array of
/// typed blocks when a call carries an image or a cache breakpoint.
/// </param>
public sealed record AnthropicRequest(string Model, int MaxTokens, IReadOnlyList<object> Messages)
{
    /// <summary>Zero everywhere today: every caller wants the most repeatable answer it can get.</summary>
    public double Temperature { get; init; }

    /// <summary>Optional <c>system</c> blocks. Omitted from the payload entirely when null.</summary>
    public IReadOnlyList<object>? System { get; init; }

    /// <summary>
    /// Short label for logs, e.g. "refine" or "suggestions". Names which caller failed
    /// when an upstream error is logged, which the shared client cannot otherwise know.
    /// </summary>
    public string Operation { get; init; } = "request";
}

/// <summary>
/// Sends prompts to the Anthropic Messages API.
/// </summary>
/// <remarks>
/// Exists so the AI services depend on an interface they can fake in a test rather than
/// on <see cref="HttpClient"/>. Before this, five services each rebuilt the same request:
/// the auth header, the API version, the error-to-exception mapping and the prompt-cache
/// logging were duplicated at nine call sites and could drift independently.
/// </remarks>
public interface IAnthropicClient
{
    /// <summary>Sends the request and returns the raw response JSON.</summary>
    /// <remarks>
    /// Returns the unparsed body on purpose: callers each want a different shape out of
    /// it and already share <see cref="AnthropicResponse"/> for the parsing.
    /// </remarks>
    /// <exception cref="AiUpstreamException">The API returned a non-success status.</exception>
    Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default);
}

public sealed class AnthropicClient : IAnthropicClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnthropicClient> _logger;
    private readonly string _apiKey;

    private const string ApiVersion = "2023-06-01";

    public AnthropicClient(
        IHttpClientFactory httpFactory, IConfiguration config, ILogger<AnthropicClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _apiKey = SecretConfig.AnthropicApiKey(config);
    }

    public async Task<string> SendAsync(AnthropicRequest request, CancellationToken ct = default)
    {
        // A dictionary rather than an anonymous type so "system" can be left out of the
        // payload when unset, instead of serialising as an explicit null.
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = request.Messages,
        };

        if (request.System is not null)
            payload["system"] = request.System;

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", ApiVersion);

        var resp = await http.SendAsync(httpReq, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Anthropic {Operation} {Status}: {Body}", request.Operation, resp.StatusCode, body);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, body);
        }

        LogCacheUsage(request.Operation, body);
        return body;
    }

    /// <summary>
    /// Reports prompt-cache effectiveness. A read of ~0 across repeated calls with the
    /// same stable prefix means that prefix is not byte-stable and the cache is silently
    /// doing nothing.
    /// </summary>
    private void LogCacheUsage(string operation, string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage))
                return;

            int Read(string name) =>
                usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

            _logger.LogInformation(
                "{Operation} tokens: cache_read={Hit} cache_write={Write} uncached_input={In} output={Out}",
                operation,
                Read("cache_read_input_tokens"),
                Read("cache_creation_input_tokens"),
                Read("input_tokens"),
                Read("output_tokens"));
        }
        catch (JsonException)
        {
            // Usage reporting must never break a call whose content parsed fine.
        }
    }
}
