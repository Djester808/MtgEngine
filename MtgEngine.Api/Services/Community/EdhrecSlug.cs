using System.Text.RegularExpressions;

namespace MtgEngine.Api.Services.Community;

/// <summary>
/// A card's name as EDHREC spells it in a path. Derived from Catalog's own name, never from caller
/// text, so no request can shape an upstream URL.
/// </summary>
internal static class EdhrecSlug
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex NonSlug = new("[^a-z0-9\\s-]", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Dashes = new("-+", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>"Atraxa, Praetors' Voice" -> "atraxa-praetors-voice"</summary>
    public static string From(string name)
    {
        var value = (name ?? "").Split("//", StringSplitOptions.None)[0].Trim().ToLowerInvariant();
        value = NonSlug.Replace(value, "");
        value = Whitespace.Replace(value, "-");
        return Dashes.Replace(value, "-").Trim('-');
    }
}
