namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// A token spec and, when its text is more than keywords, the note saying why its abilities are
/// missing. The spec is still worth writing: name, colours, types and stats come from Scryfall's
/// own token object and are never guessed.
/// </summary>
public sealed record TokenResult(TokenSpec Spec, string? Note);

/// <summary>
/// Token specs from Scryfall's <c>all_parts</c>, CR 111. The token's characteristics are printed
/// on its own card object, so a template can build them; its abilities can only be templated when
/// its text is keyword-only, and otherwise the translation stage fills them in.
/// </summary>
public static partial class Templates
{
    /// <summary>The spec for one token, or null when its type line is not one the vocabulary can spell.</summary>
    [RuleRef("111")]
    public static TokenResult? Token(CorpusToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (ParseTypeLine(token.TypeLine) is not { } types)
            return null;

        IReadOnlyList<Ability>? abilities = null;
        string? note = null;
        var lines = token.OracleText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 0)
        {
            var built = new List<Ability>();
            foreach (var line in lines)
            {
                if (IsBlank(line))
                    continue;
                if (KeywordLine(line) is { } keywords)
                {
                    built.AddRange(keywords);
                    continue;
                }

                built.Clear();
                note = "The token's text is not keyword-only; its abilities need a translation: " + line;
                break;
            }

            if (note is null && built.Count > 0)
                abilities = built;
        }

        var spec = new TokenSpec(
            token.Name,
            Colors(token.Colors),
            types.Types,
            types.Subtypes,
            types.Supertypes,
            Stat(token.Power),
            Stat(token.Toughness),
            abilities,
            token.OracleId.Length == 0 ? null : token.OracleId);

        return new TokenResult(spec, note);
    }

    /// <summary>Scryfall's colour letters as the vocabulary's flags, CR 105.1. An empty string is colourless.</summary>
    [RuleRef("105.1")]
    public static Color Colors(string? letters)
    {
        var colors = Color.Colorless;
        foreach (var letter in letters ?? string.Empty)
        {
            colors |= letter switch
            {
                'W' => Color.White,
                'U' => Color.Blue,
                'B' => Color.Black,
                'R' => Color.Red,
                'G' => Color.Green,
                _ => Color.Colorless,
            };
        }

        return colors;
    }

    /// <summary>A token's power or toughness. "*" is a characteristic-defining marker and has no number.</summary>
    private static int? Stat(string? printed) =>
        int.TryParse(printed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
