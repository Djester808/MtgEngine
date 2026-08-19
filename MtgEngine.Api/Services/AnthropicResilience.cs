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
    /// <summary>
    /// Per-attempt ceiling, sized for the slowest caller — the deck build and the commander
    /// suggestion, both on a thinking model.
    /// </summary>
    /// <remarks>
    /// Sixty seconds was right when every call was a non-thinking model answering from a
    /// fixed prompt. Adaptive thinking spends real time before the first output token, and a
    /// 99-card build reasons through a large candidate pool — a run that lands well inside
    /// the budget on a warm cache can take minutes on a cold one. Too low a value here does
    /// not degrade the answer, it throws away a call that was still working and bills for it.
    /// </remarks>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Wall-clock ceiling across all attempts, so a caller can never hang indefinitely.
    /// </summary>
    /// <remarks>
    /// Raised from 360s after a measured build blew through it: the model produced nothing
    /// for 161 seconds, wrote the deck in 8, and the assessment pass took another 118 — and
    /// that is one healthy run, before a single retry. At 360 the pipeline was timing out on
    /// success. This bounds a hung call, not a slow one.
    /// </remarks>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(900);

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
                    // Computed rather than returned as null. A null here is documented as
                    // "no opinion", but with a DelayGenerator present it has meant no delay
                    // at all: a 529 burned all three retries inside five seconds and gave up
                    // while the service was merely busy — which is the one status where
                    // waiting is the entire remedy. Measured before this: four attempts in
                    // 5.06s against a configured 2s base.
                    return ValueTask.FromResult<TimeSpan?>(BackoffFor(args.AttemptNumber));
                },

                OnRetry = args =>
                {
                    var reason = args.Outcome.Exception?.GetType().Name
                                 ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString()
                                 ?? "unknown";
                    logger.LogWarning(
                        "Anthropic call failed ({Reason}); retry {Attempt}/{Max} in {Delay}",
                        reason, args.AttemptNumber + 1, MaxRetryAttempts, BackoffFor(args.AttemptNumber));
                    return default;
                },
            });

            pipeline.AddTimeout(AttemptTimeout);
        });

        return builder;
    }

    /// <summary>
    /// The wait before retry number <paramref name="attemptNumber"/> (0-based): 2s, 4s, 8s.
    /// </summary>
    /// <remarks>
    /// Shared by the delay generator and the log line so the two cannot disagree. They did:
    /// <c>OnRetryArguments.RetryDelay</c> stays zero when a delay generator is supplied, so
    /// every retry logged "in 00:00:00" regardless of how long it actually waited — which
    /// reads exactly like a backoff that is not working.
    /// </remarks>
    private static TimeSpan BackoffFor(int attemptNumber) =>
        TimeSpan.FromSeconds(Math.Pow(2, attemptNumber + 1))
        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));

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
