using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// The search query language: parses a raw query string into filters, and tests cards
/// against them.
/// </summary>
/// <remarks>
/// Understands a subset of Scryfall's syntax -- <c>name:"..."</c>, <c>o:</c>, <c>t:</c>,
/// <c>s:</c>, <c>r:</c>, <c>c:</c> and <c>cmc</c> comparisons -- and treats a query with no
/// recognised token as a plain name filter.
/// <para>
/// Lives apart from <see cref="BulkDataService"/> because none of it needs the card index:
/// every method here is a pure function of its arguments. That makes the query language
/// testable on its own, where previously exercising a matching rule meant standing up bulk
/// data and network I/O first.
/// </para>
/// </remarks>
internal static class CardQuery
{
    /// <summary>
    /// The singular forms a query might be the plural of, including the query itself.
    /// </summary>
    /// <remarks>
    /// Players type "wolves", not "Wolf". English is irregular enough that guessing one
    /// singular goes wrong ("faeries" is not "faery"), so every plausible form is offered
    /// and the caller keeps whichever is a real creature type.
    /// </remarks>
    internal static string[] SingularCandidates(string word)
    {
        var w = word.Trim();
        if (w.Length < 3)
            return [w];

        var forms = new List<string>(5) { w };

        if (w.EndsWith("ves", StringComparison.OrdinalIgnoreCase))
            forms.Add(w[..^3] + "f");           // wolves -> wolf, dwarves -> dwarf
        if (w.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            forms.Add(w[..^3] + "y");           // allies -> ally
        if (w.EndsWith("es", StringComparison.OrdinalIgnoreCase))
            forms.Add(w[..^2]);                 // foxes -> fox
        if (w.EndsWith('s'))
            forms.Add(w[..^1]);                 // goblins -> goblin, faeries -> faerie

        return [.. forms.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The token prefixes the query language actually understands.</summary>
    private static readonly string[] QueryTokens = ["name:", "o:", "t:", "s:", "r:", "c:"];

    /// <summary>
    /// The index of <paramref name="token"/> at a word boundary (start of query,
    /// whitespace, or an opening bracket), or -1. Every parser goes through this: a loose
    /// <c>IndexOf</c> made "foo:bar" match "o:" mid-word, which turned unrecognised
    /// queries into accidental oracle-text searches — or, before the name-filter fix,
    /// into a match of the entire corpus.
    /// </summary>
    /// <remarks>
    /// The bracket cases are not cosmetic. The client sends every text search as
    /// <c>(name:"x" or o:"x")</c>; with only whitespace counting as a boundary the
    /// leading <c>name:</c> was invisible, so the name filter silently dropped out and
    /// the search degraded to oracle text alone. That looked like it worked — most
    /// creatures name themselves in their own rules text — but any card that doesn't
    /// (Llanowar Elves, whose text is just "{T}: Add {G}") could not be found at all.
    /// </remarks>
    private static int IndexOfToken(string q, string token, int startIndex = 0)
    {
        var idx = q.IndexOf(token, startIndex, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            if (idx == 0 || char.IsWhiteSpace(q[idx - 1]) || q[idx - 1] is '(' or '[')
                return idx;
            idx = q.IndexOf(token, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return -1;
    }

    /// <summary>
    /// True only for syntax this parser will actually act on. "cmc" counts only with a
    /// comparison operator — a bare "cmcguffin" is a name, and suppressing the name
    /// filter without producing any structured filter matches everything.
    /// </summary>
    private static bool HasQuerySyntax(string q) =>
        QueryTokens.Any(t => IndexOfToken(q, t) >= 0) || ParseCmc(q).Op is not null;

    internal static string? ParseName(string q)
    {
        // name:"some text"
        var idx = IndexOfToken(q, "name:\"");
        if (idx >= 0)
        {
            var start = idx + 6;
            var end = q.IndexOf('"', start);
            if (end > start)
                return q[start..end];
        }
        // Plain text with no recognised tokens → treat the whole query as a name filter
        return HasQuerySyntax(q) ? null : q.Trim();
    }

    internal static string? ParseOracleText(string q)
    {
        // o:"some text"
        var idx = IndexOfToken(q, "o:\"");
        if (idx >= 0)
        {
            var start = idx + 3;
            var end = q.IndexOf('"', start);
            if (end > start)
                return q[start..end];
        }
        // o:word (unquoted single token)
        idx = IndexOfToken(q, "o:");
        if (idx >= 0)
        {
            var start = idx + 2;
            var end = start;
            while (end < q.Length && !char.IsWhiteSpace(q[end]))
                end++;
            if (end > start)
                return q[start..end];
        }
        return null;
    }

    internal static bool MatchesOracleText(CardDefinition d, string filter, bool matchCase)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return d.OracleText.Contains(filter, comparison);
    }

    internal static CardType ParseTypes(string q)
    {
        var flags = CardType.None;
        var i = 0;
        while ((i = IndexOfToken(q, "t:", i)) >= 0)
        {
            i += 2;
            var end = i;
            while (end < q.Length && char.IsLetterOrDigit(q[end]))
                end++;
            flags |= q[i..end].ToLowerInvariant() switch
            {
                "creature" => CardType.Creature,
                "instant" => CardType.Instant,
                "sorcery" => CardType.Sorcery,
                "enchantment" => CardType.Enchantment,
                "artifact" => CardType.Artifact,
                "land" => CardType.Land,
                "planeswalker" => CardType.Planeswalker,
                "token" => CardType.Token,
                "battle" => CardType.Battle,
                "other" => CardType.Other,
                _ => CardType.None
            };
            i = end;
        }
        return flags;
    }

    internal static HashSet<string> ParseSupertypes(string q)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while ((i = IndexOfToken(q, "t:", i)) >= 0)
        {
            i += 2;
            var end = i;
            while (end < q.Length && char.IsLetterOrDigit(q[end]))
                end++;
            var token = q[i..end].ToLowerInvariant();
            if (token is "legendary" or "basic" or "snow" or "world")
                result.Add(token);
            i = end;
        }
        return result;
    }

    internal static string? ParseSet(string q)
    {
        var idx = IndexOfToken(q, "s:");
        if (idx < 0)
            return null;
        var start = idx + 2;
        var end = start;
        while (end < q.Length && char.IsLetterOrDigit(q[end]))
            end++;
        return end > start ? q[start..end] : null;
    }

    internal static HashSet<string> ParseRarities(string q)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while ((i = IndexOfToken(q, "r:", i)) >= 0)
        {
            i += 2;
            var end = i;
            while (end < q.Length && char.IsLetterOrDigit(q[end]))
                end++;
            var r = q[i..end].ToLowerInvariant();
            if (r is "common" or "uncommon" or "rare" or "mythic")
                result.Add(r);
            i = end;
        }
        return result;
    }

    internal static (string? Op, int Val) ParseCmc(string q)
    {
        foreach (var op in new[] { "<=", ">=", "=" })
        {
            var key = "cmc" + op;
            var idx = IndexOfToken(q, key);
            if (idx < 0)
                continue;
            var start = idx + key.Length;
            var end = start;
            while (end < q.Length && char.IsDigit(q[end]))
                end++;
            if (end > start && int.TryParse(q[start..end], out var val))
                return (op, val);
        }
        return (null, 0);
    }

    internal static bool MatchesCmc(CardDefinition d, string op, int val)
        => op switch { "<=" => d.Cmc <= val, ">=" => d.Cmc >= val, _ => d.Cmc == val };

    internal static (bool HasFilter, bool Multicolor, bool Colorless, HashSet<ManaColor> Colors) ParseColors(string q)
    {
        var idx = IndexOfToken(q, "c:");
        if (idx < 0)
            return (false, false, false, []);
        var start = idx + 2;
        var end = start;
        while (end < q.Length && char.IsLetter(q[end]))
            end++;
        if (end == start)
            return (false, false, false, []);
        // Every lit pip rides in one token — 'm' and 'c' are not colour letters, so they
        // cannot be confused with one. The old form only recognised a whole token of "m" or
        // "c", which meant a combination like "multicolour, within red and white" had to be
        // thrown away by the client before it was ever sent.
        var token = q[start..end].ToLowerInvariant();
        var multicolor = token.Contains('m');
        var colorless = token.Contains('c');
        var colors = new HashSet<ManaColor>();
        foreach (var ch in token)
        {
            var c = ch switch
            {
                'w' => ManaColor.White,
                'u' => ManaColor.Blue,
                'b' => ManaColor.Black,
                'r' => ManaColor.Red,
                'g' => ManaColor.Green,
                _ => (ManaColor?)null
            };
            if (c.HasValue)
                colors.Add(c.Value);
        }
        var hasFilter = multicolor || colorless || colors.Count > 0;
        return hasFilter ? (true, multicolor, colorless, colors) : (false, false, false, []);
    }

    /// <summary>
    /// "Within these colours": a card matches when its whole colour identity fits inside the
    /// selection, so <c>c:r</c> is mono-red and <c>c:rw</c> is mono-red, mono-white and Boros.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>matchesColorSelection</c> in the client's <c>utils/color-filter.ts</c>; the
    /// two must agree, because the same pip row filters locally on the grids and through this
    /// query on the search panel. Previously both said "contains any selected colour", so
    /// picking Red returned every Boros, Grixis and five-colour card that merely included red.
    /// </remarks>
    internal static bool MatchesColor(CardDefinition d, bool multicolor, bool colorless, HashSet<ManaColor> colors)
    {
        var identity = d.ColorIdentity;

        // Colourless widens rather than narrows: it unions with whatever else is selected.
        if (colorless && identity.Count == 0)
            return true;

        if (colors.Count > 0)
        {
            // An empty identity is excluded here so that c:r does not return every artifact;
            // the 'c' pip is how a caller asks for those.
            var fits = identity.Count > 0 && identity.All(colors.Contains);
            return multicolor ? fits && identity.Count >= 2 : fits;
        }

        if (multicolor)
            return identity.Count >= 2;

        // Only 'c' was asked for, and this card has a colour.
        return false;
    }

    private static readonly char[] _wordSeparators = [' ', ',', '-', '\'', '"', '(', ')', '/', ':', '.'];

    /// <summary>
    /// Compiles a user-supplied name pattern with a hard match timeout, or null if the
    /// pattern is invalid. Compile once per search and reuse across the scan — never per
    /// card: the timeout is the only guard against a catastrophic-backtracking pattern
    /// (e.g. <c>(a+)+b</c>) pegging a core over ~35k names.
    /// </summary>
    internal static Regex? CompileNameRegex(string pattern, bool matchCase)
    {
        try
        {
            var opts = (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase)
                       | RegexOptions.CultureInvariant;
            return new Regex(pattern, opts, TimeSpan.FromMilliseconds(50));
        }
        // A half-typed regex is a normal thing for a user to send; caller treats null as
        // "matches nothing" rather than failing the whole search.
        catch (RegexParseException) { return null; }
        catch (ArgumentException) { return null; }
    }

    internal static bool MatchesName(
        string name, string filter, bool matchCase, bool matchWord, bool useRegex, Regex? compiled)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (useRegex)
        {
            if (compiled is null)
                return false; // invalid pattern → no matches
            try
            { return compiled.IsMatch(name); }
            // One pathological name hitting the timeout is a non-match, not a dead search.
            catch (RegexMatchTimeoutException) { return false; }
        }
        if (matchWord)
        {
            var words = name.Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.Equals(filter, comparison));
        }
        return name.Contains(filter, comparison);
    }
}
