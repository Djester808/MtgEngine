using System.Text.RegularExpressions;

namespace MtgEngine.Api.Services;

/// <summary>
/// Whether a card's rules text names a creature type, as a word.
/// </summary>
/// <remarks>
/// Shared by the analysis pass, which decides what a commander's tribe is, and the build,
/// which collects the pool that matches it. They asked the same question and the second
/// copy is the one worth catching: a substring test here said the tribe <c>Battle</c>
/// appeared in every card that says "enters the battlefield", which is most of Magic —
/// 1,406 of one commander's 1,475 hits, and 1,349 of those said nothing else.
/// </remarks>
internal static class TribeText
{
    /// <summary>
    /// How long a single match may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Tribe names come from the analysis pass, which is model output, and the patterns are
    /// built from them. House rule for anything of that shape: never run an untimed regex
    /// over text you did not author.
    /// </remarks>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>One whole-word pattern per tribe, including its plural.</summary>
    /// <remarks>
    /// Rules text says "Wolves" far more often than "Wolf", and several creature types take
    /// the -f/-ves plural, so a pattern that only knew the singular would miss most of the
    /// cards it exists to find.
    /// </remarks>
    public static Regex[] MentionPatterns(IEnumerable<string> tribes)
    {
        var patterns = new List<Regex>();

        foreach (var tribe in tribes)
        {
            if (string.IsNullOrWhiteSpace(tribe))
                continue;

            var forms = new List<string> { Regex.Escape(tribe) + "s?" };
            if (tribe.EndsWith("fe", StringComparison.OrdinalIgnoreCase))
                forms.Insert(0, Regex.Escape(tribe[..^2]) + "ves");
            else if (tribe.EndsWith("f", StringComparison.OrdinalIgnoreCase))
                forms.Insert(0, Regex.Escape(tribe[..^1]) + "ves");

            try
            {
                patterns.Add(new Regex(
                    $@"\b(?:{string.Join("|", forms)})\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout));
            }
            catch (ArgumentException)
            {
                // A tribe name that will not compile is simply not searched for by text;
                // the creature-type match still finds the real members.
            }
        }

        return [.. patterns];
    }

    /// <summary>True when the text names one of the tribes as a word.</summary>
    public static bool Mentions(Regex[] patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(text))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // Treated as no match, per the house rule for timed patterns.
            }
        }

        return false;
    }

    /// <summary>Convenience for a single tribe against a single card's text.</summary>
    public static bool Mentions(string tribe, string? text) =>
        !string.IsNullOrEmpty(text) && Mentions(MentionPatterns([tribe]), text);
}
