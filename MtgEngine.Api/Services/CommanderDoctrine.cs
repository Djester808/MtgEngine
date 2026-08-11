namespace MtgEngine.Api.Services;

/// <summary>
/// Serves the deckbuilding doctrine that every AI pass reasons from.
/// </summary>
/// <remarks>
/// The doctrine lives in <c>Knowledge/commander-doctrine.md</c> so the deck philosophy can
/// be edited and reviewed as prose without a code change. It is the single definition of
/// what "good" means here -- when the suggestion pass and the scoring pass each carried
/// their own idea of it, the same card came back as 85% in one list and 55% in the other.
/// </remarks>
public interface ICommanderDoctrine
{
    /// <summary>The full doctrine text, injected verbatim into prompts.</summary>
    string Text { get; }

    /// <summary>
    /// Rough token count, used to decide whether the text clears Anthropic's minimum
    /// cacheable prefix length.
    /// </summary>
    int ApproximateTokens { get; }
}

public sealed class CommanderDoctrine : ICommanderDoctrine
{
    public const string FileName = "commander-doctrine.md";
    public const string FolderName = "Knowledge";

    /// <summary>
    /// Anthropic will not cache a prefix shorter than this on Haiku. Below it the
    /// cache_control marker is accepted and silently does nothing, so it is worth
    /// knowing rather than assuming a cache that is not there.
    /// </summary>
    public const int MinCacheableTokens = 2048;

    public string Text { get; }
    public int ApproximateTokens { get; }

    public CommanderDoctrine(IHostEnvironment env, ILogger<CommanderDoctrine> logger)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FolderName, FileName);

        // Fall back to the source tree so `dotnet run` works before a copy-to-output.
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, FolderName, FileName);

        if (!File.Exists(path))
        {
            // Loud, not silent. A missing doctrine would leave every prompt subtly
            // ungrounded while still returning plausible-looking scores -- exactly the
            // failure mode that let stale bulk data run for two and a half months.
            throw new InvalidOperationException(
                $"Commander doctrine not found at '{path}'. It ships as a content file; " +
                $"check that {FolderName}/{FileName} exists and that the csproj copies it to output.");
        }

        Text = File.ReadAllText(path);

        // ~4 characters per token is close enough to decide cacheability.
        ApproximateTokens = Text.Length / 4;

        if (ApproximateTokens < MinCacheableTokens)
        {
            logger.LogWarning(
                "Doctrine is ~{Tokens} tokens, below Anthropic's {Min}-token minimum for prompt " +
                "caching. It will be re-sent in full on every request",
                ApproximateTokens, MinCacheableTokens);
        }

        logger.LogInformation(
            "Commander doctrine loaded: {Chars} chars, ~{Tokens} tokens, from {Path}",
            Text.Length, ApproximateTokens, path);
    }
}
