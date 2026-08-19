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
    /// <summary>
    /// Sampling temperature, or null to omit the parameter entirely.
    /// </summary>
    /// <remarks>
    /// Defaults to zero because every caller that predates thinking models wants the most
    /// repeatable answer it can get, and omitting the field would silently move them to the
    /// API default of 1.0.
    /// <para>
    /// **Null is not the same as zero here.** Claude Opus 5 and the other thinking models
    /// removed the sampling parameters: sending <c>temperature</c> at all — including
    /// <c>0</c> — is a 400. Those callers set this to null so the key is left out of the
    /// payload, and steer with the prompt instead.
    /// </para>
    /// </remarks>
    public double? Temperature { get; init; } = 0;

    /// <summary>
    /// How hard the model may think before answering — <c>low</c>, <c>medium</c>,
    /// <c>high</c>, <c>xhigh</c> or <c>max</c> — or null to take the provider default.
    /// </summary>
    /// <remarks>
    /// **Thinking is billed out of <see cref="MaxTokens"/>, not on top of it**, so effort
    /// and ceiling have to be chosen together. At the default effort a deck build spent all
    /// 16,000 of its tokens reasoning and emitted 1,543 characters of an unterminated JSON
    /// object; the parse failed, the caller read it as zero candidates, and the user was
    /// shown an empty deck with no error. Lowering effort leaves room for the answer.
    /// <para>
    /// This is the thinking control Opus 5 accepts. A <c>thinking.budget_tokens</c> is
    /// rejected outright by that model — it reasons adaptively and is steered by effort —
    /// so there is deliberately no token-budget knob here.
    /// </para>
    /// </remarks>
    public string? Effort { get; init; }

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

    /// <summary>
    /// Sends the request with <c>stream: true</c>, reporting text as it arrives.
    /// </summary>
    /// <remarks>
    /// For calls long enough that a caller has to show progress. A buffered request gives
    /// nothing to report until it finishes, which on a deck build is minutes of a progress
    /// bar sitting on one step while the user wonders whether it has died.
    /// <para>
    /// <paramref name="onText"/> receives each text delta and the running total. It must be
    /// cheap and must not throw — it is called from inside the read loop.
    /// </para>
    /// </remarks>
    /// <returns>The complete assembled text, as if the call had been buffered.</returns>
    /// <param name="onThinking">
    /// Called as the model reasons, with the running count of thinking characters. Thinking
    /// happens before a single visible token exists — on a deck build that is over two
    /// minutes of a progress bar with nothing to report — so a caller that wants to show
    /// liveness from the first second has to watch this instead.
    /// </param>
    Task<string> StreamTextAsync(
        AnthropicRequest request,
        Func<string, string, Task> onText,
        Func<int, Task>? onThinking = null,
        CancellationToken ct = default);
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
        var callWatch = System.Diagnostics.Stopwatch.StartNew();
        var payload = BuildPayload(request);

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

        _logger.LogInformation(
            "Anthropic {Operation} completed in {Elapsed}ms", request.Operation,
            callWatch.ElapsedMilliseconds);
        LogCacheUsage(request.Operation, body);
        WarnIfTruncated(request, body);
        return body;
    }

    /// <summary>
    /// Logs a warning when the model ran out of budget before finishing its answer.
    /// </summary>
    /// <remarks>
    /// A truncated answer is a 200 with a body that happens to be unfinished, so it reaches
    /// the caller looking like a valid — but empty or short — result. Every caller here asks
    /// for JSON, and half an object deserialises to nothing, which is then reported as the
    /// model having found nothing. Naming it at the point it happens keeps the diagnosis
    /// from having to start three layers away.
    /// </remarks>
    private void WarnIfTruncated(AnthropicRequest request, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("stop_reason", out var sr)
                && sr.ValueKind == JsonValueKind.String
                && sr.ValueEquals("max_tokens"))
            {
                _logger.LogWarning(
                    "Anthropic {Operation} truncated at max_tokens (budget {Budget}, "
                    + "effort {Effort}). The answer is incomplete.",
                    request.Operation, request.MaxTokens, request.Effort);
            }
        }
        catch (JsonException)
        {
            // Not our concern here; the caller's own parse reports an unusable body.
        }
    }

    public async Task<string> StreamTextAsync(
        AnthropicRequest request,
        Func<string, string, Task> onText,
        Func<int, Task>? onThinking = null,
        CancellationToken ct = default)
    {
        int thinkingChars = 0;
        var callWatch = System.Diagnostics.Stopwatch.StartNew();
        var payload = BuildPayload(request);
        payload["stream"] = true;

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", ApiVersion);

        // Headers-read, or HttpClient buffers the whole body and the point is lost.
        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Anthropic {Operation} {Status}: {Body}", request.Operation, resp.StatusCode, error);
            throw new AiUpstreamException("Anthropic", resp.StatusCode, error);
        }

        var assembled = new StringBuilder();

        // Why the answer stopped, and how much of the budget it cost. Both arrive on the
        // message_delta frame at the end of the stream. Without them a response truncated
        // at max_tokens is indistinguishable from a short one: the JSON never closes, the
        // caller's deserialiser returns default, and the build reports "0 candidates" with
        // nothing anywhere saying why.
        string stopReason = "(none)";
        int outputTokens = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // Anthropic's stream is SSE. Only the data lines carry anything; the event
            // lines and the blank separators are skipped.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]")
                continue;

            string? delta = null;
            string? thinking = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var mt)
                    && mt.GetString() == "message_delta")
                {
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr)
                        && sr.ValueKind == JsonValueKind.String)
                    {
                        stopReason = sr.GetString() ?? stopReason;
                    }
                    if (root.TryGetProperty("usage", out var us)
                        && us.TryGetProperty("output_tokens", out var ot)
                        && ot.TryGetInt32(out var otv))
                    {
                        outputTokens = otv;
                    }
                }

                if (root.TryGetProperty("type", out var t2)
                    && t2.GetString() == "content_block_delta"
                    && root.TryGetProperty("delta", out var d2)
                    && d2.TryGetProperty("type", out var dt2)
                    && dt2.GetString() == "thinking_delta"
                    && d2.TryGetProperty("thinking", out var think))
                {
                    thinking = think.GetString();
                }

                // Only content_block_delta carries visible text. Thinking deltas arrive on
                // the same stream and are deliberately not surfaced: they are not the answer,
                // and a caller counting the answer's shape would be misled by them.
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "content_block_delta"
                    && root.TryGetProperty("delta", out var d)
                    && d.TryGetProperty("type", out var dt)
                    && dt.GetString() == "text_delta"
                    && d.TryGetProperty("text", out var text))
                {
                    delta = text.GetString();
                }
            }
            catch (JsonException)
            {
                continue; // a partial or unexpected frame is not worth failing the call over
            }

            if (!string.IsNullOrEmpty(thinking))
            {
                // Counted whether or not a caller is listening: the total is what the
                // truncation log reports, and gating it on the callback made a budget
                // problem look like the model had not thought at all.
                thinkingChars += thinking.Length;
                if (onThinking is not null)
                    await onThinking(thinkingChars);
                continue;
            }

            if (string.IsNullOrEmpty(delta))
                continue;

            assembled.Append(delta);
            await onText(delta, assembled.ToString());
        }

        var full = assembled.ToString();

        if (stopReason == "max_tokens")
        {
            // The answer is cut mid-token. Nothing downstream can salvage it -- the JSON
            // object never closes -- so it is reported here rather than surfacing as an
            // empty result three layers up.
            _logger.LogWarning(
                "Anthropic {Operation} truncated at max_tokens: {Output} output tokens "
                + "(budget {Budget}), {Chars} chars of text, {Thinking} chars of thinking",
                request.Operation, outputTokens, request.MaxTokens, full.Length, thinkingChars);
        }
        else
        {
            _logger.LogInformation(
                "Anthropic {Operation} stream complete in {Elapsed}ms: stop={Stop}, {Output} "
                + "output tokens (budget {Budget}), {Chars} chars of text, {Thinking} chars "
                + "of thinking",
                request.Operation, callWatch.ElapsedMilliseconds, stopReason, outputTokens,
                request.MaxTokens, full.Length, thinkingChars);
        }

        // Re-shaped into the same envelope a buffered call returns, so every existing
        // parser works against a streamed response without knowing the difference.
        return JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = full } },
        });
    }

    /// <summary>The request body, shared by the buffered and streaming paths.</summary>
    internal static Dictionary<string, object> BuildPayload(AnthropicRequest request)
    {
        // A dictionary rather than an anonymous type so "system" can be left out of the
        // payload when unset, instead of serialising as an explicit null.
        var payload = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = request.Messages,
        };

        // Omitted rather than sent as null: the thinking models reject the key's presence,
        // not just a non-default value.
        if (request.Temperature is { } temperature)
            payload["temperature"] = temperature;

        if (request.Effort is { } effort)
            payload["output_config"] = new Dictionary<string, object> { ["effort"] = effort };

        if (request.System is not null)
            payload["system"] = request.System;

        return payload;
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
