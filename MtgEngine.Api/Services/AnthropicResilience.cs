using Polly;
using Polly.Timeout;

namespace MtgEngine.Api.Services;

/// <summary>
/// Resilience pipeline for outbound Anthropic API calls.
/// </summary>
/// <remarks>
/// The Anthropic API returns 429 when rate-limited and 529 when overloaded; both
/// are transient and worth retrying. 4xx responses other than 429 mean the request
/// itself is wrong (bad key, malformed body), so retrying only burns time and money.
/// </remarks>
internal static class AnthropicResilience
{
    /// <summary>Per-attempt ceiling. Sized for the slowest caller (AiBuildService, max_tokens 6000).</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Wall-clock ceiling across all attempts, so a caller can never hang indefinitely.</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(180);

    private const int MaxRetryAttempts = 3;

    public static IHttpClientBuilder AddAnthropicResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("anthropic", (pipeline, context) =>
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("AnthropicResilience");

            // Order matters: outermost strategy is added first.
            // total timeout -> retry -> attempt timeout

            pipeline.AddTimeout(TotalTimeout);

            pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true, // decorrelate concurrent callers so retries don't sync up
                Delay = TimeSpan.FromSeconds(2),

                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),

                // Anthropic sends Retry-After on 429. Honour it rather than guessing.
                DelayGenerator = args =>
                {
                    var retryAfter = args.Outcome.Result?.Headers.RetryAfter;
                    if (retryAfter?.Delta is { } delta)
                        return ValueTask.FromResult<TimeSpan?>(delta);
                    if (retryAfter?.Date is { } date)
                    {
                        var wait = date - DateTimeOffset.UtcNow;
                        if (wait > TimeSpan.Zero)
                            return ValueTask.FromResult<TimeSpan?>(wait);
                    }
                    return ValueTask.FromResult<TimeSpan?>(null); // fall back to exponential backoff
                },

                OnRetry = args =>
                {
                    var reason = args.Outcome.Exception?.GetType().Name
                                 ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString()
                                 ?? "unknown";
                    logger.LogWarning(
                        "Anthropic call failed ({Reason}); retry {Attempt}/{Max} in {Delay}",
                        reason, args.AttemptNumber + 1, MaxRetryAttempts, args.RetryDelay);
                    return default;
                },
            });

            pipeline.AddTimeout(AttemptTimeout);
        });

        return builder;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException or TimeoutRejectedException)
            return true;

        if (outcome.Result is not { } response)
            return false;

        var status = (int)response.StatusCode;
        return status == 429           // rate limited
            || status == 529           // Anthropic: overloaded
            || status >= 500;          // server-side failure
    }
}
