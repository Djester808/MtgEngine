using System.Text;

namespace MtgEngine.Cards;

/// <summary>
/// Turns a rulebook name into an enum identifier, the same way <c>tools/gen-vocabulary.js</c>
/// does. A test compares the generated enums with the parsed rulebook through this function,
/// so the two implementations cannot drift without a red build.
/// </summary>
public static class VocabularyNames
{
    public static string ToIdentifier(string name)
    {
        // "∞ (Infinity)" is the one rule whose name explains itself in parentheses; the symbol is the name.
        var text = System.Text.RegularExpressions.Regex.Replace(name, @"\([^)]*\)", string.Empty)
            .Replace("∞", "Infinity", StringComparison.Ordinal);
        var builder = new StringBuilder();
        var upperNext = true;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        var id = builder.ToString();
        if (id.Length > 0 && char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    /// <summary>The keyword a Scryfall keyword string names, or null when it is an ability word or unknown.</summary>
    public static KeywordKind? Keyword(string scryfallName) =>
        Enum.TryParse<KeywordKind>(ToIdentifier(scryfallName), ignoreCase: false, out var kind) ? kind : null;

    /// <summary>The keyword action a Scryfall keyword string names, or null.</summary>
    public static KeywordAction? Action(string scryfallName) =>
        Enum.TryParse<KeywordAction>(ToIdentifier(scryfallName), ignoreCase: false, out var action) ? action : null;

    public static string RuleOf<TEnum>(TEnum member)
        where TEnum : struct, Enum
    {
        var field = typeof(TEnum).GetField(member.ToString());
        var attribute = field?.GetCustomAttributes(typeof(RuleRefAttribute), false).OfType<RuleRefAttribute>().FirstOrDefault();
        return attribute?.Rule ?? string.Empty;
    }
}
