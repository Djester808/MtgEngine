namespace MtgEngine.Cards;

/// <summary>
/// The wave-15 play-permission and choice records, whose flags can contradict each other in ways
/// the shape alone does not stop. Each check names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckPermissions(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 118.9: an alternative cost replaces the mana cost, and a spell is cast for one
        // alternative cost at a time (CR 601.2b, "one" of them). Waiving the cost and paying life
        // in its place are two different alternative costs; a card prints one, never both.
        foreach (var permission in nodes.OfType<MayPlayFromZone>().Where(p => p.WithoutPayingManaCost && p.PayLifeEqualToManaValueInstead))
            findings.Add(new(LintSeverity.Error, "L021", "MayPlayFromZone sets both WithoutPayingManaCost and PayLifeEqualToManaValueInstead; a cast pays one alternative cost (CR 118.9), so a card prints one of the two.", path));

        // CR 700.2: a choice among named objects takes its candidates from the selector, wherever
        // those objects are. A zone on the filter beside it is either redundant or contradicts the
        // candidates' own zone, and a reader that honoured it would drop the revealed cards.
        foreach (var choose in nodes.OfType<Choose>().Where(c => c.Among is not null && c.Filter.In is not null))
            findings.Add(new(LintSeverity.Error, "L022", "Choose sets Among and a filter with In; the candidates' zone is the selector's own, so leave In out.", path));
    }
}
