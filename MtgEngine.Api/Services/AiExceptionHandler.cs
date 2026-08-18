using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MtgEngine.Api.Services;

/// <summary>
/// Translates service-layer exceptions into HTTP responses in one place.
/// </summary>
/// <remarks>
/// Previously every AI action repeated the same try/catch, and the mappings had drifted:
/// a missing deck surfaced as 503, and the vision endpoint returned 200 with an error
/// string where the others returned 502. Centralising it means a caller can handle
/// failure the same way for every endpoint.
/// </remarks>
public sealed class AiExceptionHandler(ILogger<AiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            ResourceNotFoundException e =>
                (StatusCodes.Status404NotFound, "Not found", e.Message),

            InvalidResourceStateException e =>
                (StatusCodes.Status409Conflict, "Cannot process in current state", e.Message),

            // Message is authored for the caller at the throw site (see the type's remarks).
            InvalidRequestException e =>
                (StatusCodes.Status400BadRequest, "Invalid request", e.Message),

            // Upstream detail is logged, not returned: it can echo request content.
            AiUpstreamException =>
                (StatusCodes.Status502BadGateway, "AI provider unavailable",
                 "The AI provider could not be reached or returned an error. Please retry."),

            // The resilience pipeline exhausted its retries or the total timeout.
            TaskCanceledException or TimeoutException =>
                (StatusCodes.Status504GatewayTimeout, "AI provider timed out",
                 "The AI provider did not respond in time. Please retry."),

            // Misconfiguration (e.g. missing API key) -- the service genuinely is
            // unavailable. The exception message holds setup instructions and config
            // paths, so a fixed detail goes to the client and the message to the log.
            // (This arm used to be InvalidOperationException, which also swept up
            // framework exceptions and echoed their messages to callers.)
            ConfigurationException =>
                (StatusCodes.Status503ServiceUnavailable, "Service unavailable",
                 "The service is not fully configured. Please contact the administrator."),

            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
            return false; // not ours; let the default handler produce a 500

        if (exception is AiUpstreamException up)
        {
            logger.LogError(exception,
                "AI upstream failure on {Path}: {Status} {Body}",
                http.Request.Path, (int)up.UpstreamStatus, Truncate(up.UpstreamBody, 2000));
        }
        else if (status >= 500)
        {
            logger.LogError(exception, "Request failed on {Path}", http.Request.Path);
        }
        else
        {
            logger.LogInformation("{Status} on {Path}: {Message}", status, http.Request.Path, exception.Message);
        }

        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = http.Request.Path,
        }, ct);

        return true;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
