namespace MtgEngine.Cards;

/// <summary>
/// The wave-17 records whose reading turns on something the game supplies as they are played —
/// the card a search found, the board as a spell is cast — and whose riders can say nothing or
/// contradict the record they ride on. Each check names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckConditionals(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 701.23a: one search, one destination per card found. An "instead" entry that sends
        // the card where the search already sends it changes nothing, so the "instead" half of the
        // text was lost; an entry that taps a card outside the battlefield names a state no other
        // zone has (CR 110.5); an empty list is a rider with no entries.
        foreach (var search in nodes.OfType<Search>().Where(s => s.Instead is not null))
        {
            var instead = search.Instead!;
            if (instead.Count == 0)
                findings.Add(new(LintSeverity.Error, "L023", "Search.Instead is an empty list; leave it null when every card found goes to To.", path));
            foreach (var entry in instead)
            {
                if (entry.To == search.To && entry.Tapped == search.Tapped)
                    findings.Add(new(LintSeverity.Error, "L023", "A Search.Instead entry has the same To and Tapped as the search itself; it changes nothing, so the 'instead' half of the text was lost.", path));
                if (entry.Tapped && entry.To != Zone.Battlefield)
                    findings.Add(new(LintSeverity.Error, "L023", "A Search.Instead entry taps a card that does not go to the battlefield; nothing is tapped in another zone.", path));
            }
        }

        // CR 601.2b with 700.2: the number of modes is announced as the spell is cast, and a rider
        // that changes that number has to say both when and how many. A condition with no count,
        // a count with no condition, or a count no higher than Max says nothing.
        foreach (var choose in nodes.OfType<ChooseMode>())
        {
            if (choose.MaxIf is not null && choose.MaxIfMet is null)
                findings.Add(new(LintSeverity.Error, "L024", "ChooseMode has MaxIf without MaxIfMet; say how many modes may be chosen when the condition holds as the spell is cast.", path));
            if (choose.MaxIf is null && choose.MaxIfMet is not null)
                findings.Add(new(LintSeverity.Error, "L024", "ChooseMode has MaxIfMet without MaxIf; name the condition read as the spell is cast, or set Max.", path));
            if (choose.MaxIf is not null && choose.MaxIfMet is { } met && met <= choose.Max)
                findings.Add(new(LintSeverity.Error, "L024", "ChooseMode.MaxIfMet is not above Max; the rider raises the mode count when the condition holds, or it says nothing.", path));
        }
    }
}
