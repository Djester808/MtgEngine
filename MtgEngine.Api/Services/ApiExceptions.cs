using System.Net;

namespace MtgEngine.Api.Services;

/// <summary>
/// An upstream AI provider call failed. Maps to 502 Bad Gateway: the request was fine,
/// a dependency was not.
/// </summary>
/// <remarks>
/// Carries the provider's response body for logging only. It is never returned to the
/// caller -- upstream error payloads can echo request content and reveal provider
/// internals, and the caller can do nothing with them.
/// </remarks>
public sealed class AiUpstreamException : Exception
{
    public HttpStatusCode UpstreamStatus { get; }
    public string UpstreamBody { get; }

    public AiUpstreamException(string provider, HttpStatusCode status, string body)
        : base($"{provider} returned {(int)status} {status}.")
    {
        UpstreamStatus = status;
        UpstreamBody = body;
    }
}

/// <summary>A requested resource does not exist, or is not visible to this user. Maps to 404.</summary>
public sealed class ResourceNotFoundException(string message) : Exception(message);

/// <summary>
/// The request is well-formed but cannot be satisfied in the current state, e.g. a deck
/// with no commander. Maps to 409 Conflict.
/// </summary>
public sealed class InvalidResourceStateException(string message) : Exception(message);

/// <summary>
/// The service is missing required configuration (e.g. an API key). Maps to 503 with a
/// fixed client-facing message — the exception message itself contains setup
/// instructions and config paths, which belong in the log, never in a response.
/// </summary>
public sealed class ConfigurationException(string message) : Exception(message);
