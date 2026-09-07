using System.Text.RegularExpressions;

namespace MtgEngine.Cards.Pipeline;

/// <summary>
/// The six mana patterns and the one replacement that comes with them. Every mana line a land
/// prints in this slice is "{T}: Add …", "{T}, Pay 1 life: Add …" or "This land enters tapped.",
/// and <see cref="IsManaPattern"/> already decides which faces qualify; this file turns the line
/// into the ability. CR 106 for mana, CR 605 for mana abilities, CR 614 for the tapped land.
/// </summary>
public static partial class Templates
{
    /// <summary>The ability one mana-pattern line spells, or null when the line is not one of the patterns.</summary>
    [RuleRef("605")]
    public static Ability? ManaLine(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);
        var line = Reminder().Replace(printed, string.Empty).Trim();
        if (!Mana().IsMatch(line))
            return null;

        var text = printed.Trim();
        if (line.Equals("This land enters tapped.", StringComparison.Ordinal))
        {
            // The empty filter is this permanent itself; SelfOnly says so, CR 614.1c.
            return new ReplacementAbility(new WouldEnterBattlefield(new ObjectFilter()), new EntersTapped(), SelfOnly: true) { Text = text };
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return null;

        var costs = new List<Cost> { new TapSelf() };
        if (line[..colon].Contains("Pay 1 life", StringComparison.Ordinal))
            costs.Add(new PayLife(new Fixed(1)));

        var production = Production(line[(colon + 1)..].Trim().TrimEnd('.').Trim());
        return production is null ? null : new ActivatedAbility(costs, new AddMana(production)) { Text = text };
    }

    /// <summary>What "Add {G}{G}", "Add one mana of any color" and their commander-identity form produce, CR 106.</summary>
    [RuleRef("106")]
    private static ManaProduction? Production(string add)
    {
        const string prefix = "Add ";
        if (!add.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var rest = add[prefix.Length..].Trim();
        if (rest.Equals("one mana of any color", StringComparison.Ordinal))
            return new ManaProduction(AnyColor: true);
        if (rest.Equals("one mana of any color in your commander's color identity", StringComparison.Ordinal))
            return new ManaProduction(CommanderIdentity: true);

        var alternatives = ManaAlternatives().Split(rest).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (alternatives.Count == 0)
            return null;
        if (alternatives.Count == 1)
            return new ManaProduction(Fixed: alternatives[0]);

        // "{W} or {U}": a choice of one symbol each. Anything wider than that is not a template.
        var colors = Color.Colorless;
        foreach (var alternative in alternatives)
        {
            if (alternative.Length != 3 || SymbolColor(alternative[1]) is not { } color)
                return null;
            colors |= color;
        }

        return new ManaProduction(OneOf: colors);
    }

    /// <summary>The colour a mana symbol's letter names, CR 107.4. {C} is colourless and has no flag.</summary>
    [RuleRef("107.4")]
    private static Color? SymbolColor(char letter) => letter switch
    {
        'W' => Color.White,
        'U' => Color.Blue,
        'B' => Color.Black,
        'R' => Color.Red,
        'G' => Color.Green,
        _ => null,
    };

    [GeneratedRegex(@"\s+or\s+|\s+and/or\s+")]
    private static partial Regex ManaAlternatives();
}
