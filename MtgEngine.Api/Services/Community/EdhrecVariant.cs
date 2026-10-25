using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MtgEngine.Api.Services.Community;

/// <summary>
/// Which cut of a commander's page is meant: all decks, the cheap ones, the expensive ones, one
/// bracket, or one theme. The key is the stored row's second half of the primary key and is the only
/// caller-supplied text that ever reaches an upstream path, which is why it is a closed grammar:
/// anything a <see cref="TryParse"/> does not recognise never becomes a URL or a cache key.
/// </summary>
public readonly partial record struct EdhrecVariant
{
    /// <summary>Every deck EDHREC has for the commander.</summary>
    public static EdhrecVariant Base => new("");

    private const string ThemePrefix = "theme-";

    /// <summary>A theme slug is lower-case letters, digits and hyphens, and short.</summary>
    [GeneratedRegex("^[a-z0-9-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeSlugPattern();

    private EdhrecVariant(string key) => Key = key;

    /// <summary>"" for the base page, else "budget", "expensive", "bracket-1".."bracket-5" or "theme-{slug}".</summary>
    public string Key { get; }

    /// <summary>True for a theme cut, whose slug must be one the commander's own page lists.</summary>
    public bool IsTheme => Key.StartsWith(ThemePrefix, StringComparison.Ordinal);

    /// <summary>The theme's slug, or an empty string when this is not a theme.</summary>
    public string ThemeSlug => IsTheme ? Key[ThemePrefix.Length..] : "";

    /// <summary>
    /// The sub-path table. A bracket page is **unverified**: EDHREC answered 403 for
    /// <c>…/bracket-3.json</c> on 2026-09-21, so the key is parsed and the path is written down here
    /// beside the verified ones, but a fetch of it stores "no page" until a real fixture is recorded.
    /// </summary>
    public string UpstreamPath(string commanderSlug) => Key switch
    {
        "" => $"pages/commanders/{commanderSlug}.json",
        "budget" => $"pages/commanders/{commanderSlug}/budget.json",
        "expensive" => $"pages/commanders/{commanderSlug}/expensive.json",
        _ when IsTheme => $"pages/commanders/{commanderSlug}/{ThemeSlug}.json",
        _ => $"pages/commanders/{commanderSlug}/{Key}.json",
    };

    /// <summary>The average deck's page for the same cut.</summary>
    public string AverageDeckPath(string commanderSlug) => Key.Length == 0
        ? $"pages/average-decks/{commanderSlug}.json"
        : IsTheme
            ? $"pages/average-decks/{commanderSlug}/{ThemeSlug}.json"
            : $"pages/average-decks/{commanderSlug}/{Key}.json";

    /// <summary>
    /// Reads a caller's variant key. Accepts exactly the closed set; a theme is accepted here on its
    /// shape alone and is checked against the commander's own theme list before any fetch.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out EdhrecVariant variant)
    {
        variant = Base;
        if (string.IsNullOrEmpty(value))
            return value is not null;
        switch (value)
        {
            case "budget":
            case "expensive":
            case "bracket-1":
            case "bracket-2":
            case "bracket-3":
            case "bracket-4":
            case "bracket-5":
                variant = new EdhrecVariant(value);
                return true;
        }
        if (!value.StartsWith(ThemePrefix, StringComparison.Ordinal))
            return false;
        var slug = value[ThemePrefix.Length..];
        if (!ThemeSlugPattern().IsMatch(slug))
            return false;
        variant = new EdhrecVariant(value);
        return true;
    }

    /// <summary>The key, so a log line or a cache key reads as the caller wrote it.</summary>
    public override string ToString() => Key;
}
