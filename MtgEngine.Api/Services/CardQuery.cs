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

    internal static string? ParseName(string q)
    {
        // name:"some text"
        var idx = q.IndexOf("name:\"", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + 6;
            var end = q.IndexOf('"', start);
            if (end > start)
                return q[start..end];
        }
        // Plain text with no query-syntax tokens → treat the whole query as a name filter
        // Any "key:" pattern (t:, s:, r:, c:, name:, cmc, etc.) signals structured query syntax
        var hasToken = q.Contains(':') || q.IndexOf("cmc", StringComparison.OrdinalIgnoreCase) >= 0;
        return hasToken ? null : q.Trim();
    }

    internal static string? ParseOracleText(string q)
    {
        // o:"some text"
        var idx = q.IndexOf("o:\"", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + 3;
            var end = q.IndexOf('"', start);
            if (end > start)
                return q[start..end];
        }
        // o:word (unquoted single token)
        idx = q.IndexOf("o:", StringComparison.OrdinalIgnoreCase);
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
        while ((i = q.IndexOf("t:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
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
        while ((i = q.IndexOf("t:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
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
        var idx = q.IndexOf("s:", StringComparison.OrdinalIgnoreCase);
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
        while ((i = q.IndexOf("r:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
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
            var idx = q.IndexOf(key, StringComparison.OrdinalIgnoreCase);
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
        var idx = q.IndexOf("c:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return (false, false, false, []);
        var start = idx + 2;
        var end = start;
        while (end < q.Length && char.IsLetter(q[end]))
            end++;
        if (end == start)
            return (false, false, false, []);
        var token = q[start..end].ToLowerInvariant();
        if (token == "m")
            return (true, true, false, []);
        if (token == "c")
            return (true, false, true, []);
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
        return colors.Count > 0 ? (true, false, false, colors) : (false, false, false, []);
    }

    internal static bool MatchesColor(CardDefinition d, bool multicolor, bool colorless, HashSet<ManaColor> colors)
    {
        if (multicolor)
            return d.ColorIdentity.Count >= 2;
        if (colorless)
            return d.ColorIdentity.Count == 0;
        return d.ColorIdentity.Any(c => colors.Contains(c));
    }

    private static readonly char[] _wordSeparators = [' ', ',', '-', '\'', '"', '(', ')', '/', ':', '.'];

    internal static bool MatchesName(string name, string filter, bool matchCase, bool matchWord, bool useRegex)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (useRegex)
        {
            try
            {
                var opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(name, filter, opts | RegexOptions.CultureInvariant);
            }
            // A half-typed regex is a normal thing for a user to send; treat it as
            // matching nothing rather than failing the whole search.
            catch (RegexParseException) { return false; }
        }
        if (matchWord)
        {
            var words = name.Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.Equals(filter, comparison));
        }
        return name.Contains(filter, comparison);
    }
}
