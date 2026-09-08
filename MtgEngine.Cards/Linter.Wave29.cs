namespace MtgEngine.Cards;

/// <summary>
/// The wave-29 records, and the places their halves can contradict each other: a target range whose
/// low end is both named and zero, a granted keyword charged twice, a chosen card name narrowed to a
/// type no card name has, a damage replacement aimed at something damage cannot reach, and a
/// prepared entry outside an entering replacement. Each check names the rule it reads, as
/// <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave29(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        CheckTargetRange(nodes, path, findings);

        // CR 613.1f with 702.21a: one keyword has one cost. "Ward {2}" is the printed mana and
        // "Ward-Pay 2 life" is the cost list; a record carrying both grants a keyword whose cost is
        // said twice, and only one of the two would be paid.
        foreach (var grant in nodes.OfType<GrantKeyword>())
        {
            if (grant is { Cost: not null, Costs: not null })
                findings.Add(new(LintSeverity.Error, "L049", $"GrantKeyword '{grant.Keyword}' sets both Cost and Costs; a granted keyword has one cost (CR 613.1f, 702.21a).", path));

            if (grant.Costs is { Count: 0 })
                findings.Add(new(LintSeverity.Error, "L049", $"GrantKeyword '{grant.Keyword}' has an empty Costs list; leave it out for a keyword with no cost (CR 613.1f).", path));
        }

        // CR 201.2 with 205.2a: only a named card has a card type to be narrowed to. On any other
        // choice the field describes nothing and would be read by nobody.
        foreach (var choose in nodes.OfType<ChooseValue>())
        {
            if (choose.OfType is not null && choose.What != ChooseWhat.CardName)
                findings.Add(new(LintSeverity.Error, "L049", $"ChooseValue '{choose.Key}' sets OfType on a {choose.What} choice; only a CardName choice names a card that has a type (CR 201.2).", path));
        }

        // CR 614.1 with 302.1: damage is dealt to players and to permanents on the battlefield. A
        // recipient filter that names another zone describes an object the event can never be about.
        foreach (var would in nodes.OfType<WouldDealDamage>())
        {
            if (would.To is { In: not null and not Zone.Battlefield })
                findings.Add(new(LintSeverity.Error, "L049", $"WouldDealDamage's To filter names zone {would.To.In}; damage is dealt to players and to permanents on the battlefield (CR 614.1).", path));
        }

        // CR 722.3a with 614.1c: entering prepared is a replacement of the entering event, so the
        // ability that says it has to be watching for that event.
        if (nodes.OfType<EntersPrepared>().Any() && !nodes.OfType<WouldEnterBattlefield>().Any())
            findings.Add(new(LintSeverity.Error, "L049", $"EntersPrepared appears in a {ability.GetType().Name} with no WouldEnterBattlefield; entering prepared replaces the entry (CR 722.3a, 614.1c).", path));
    }

    /// <summary>
    /// CR 601.2c: "one or two target creatures" announces a number inside a range before the targets
    /// are chosen. Min is the low end of that range, so it needs a high end, it cannot be the zero
    /// UpTo already means, and a low end above the high end is a range nothing satisfies.
    /// </summary>
    private static void CheckTargetRange(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var target in nodes.OfType<Target>())
        {
            if (target.Min is null)
                continue;

            if (target.UpTo)
                findings.Add(new(LintSeverity.Error, "L049", "Target sets both Min and UpTo; \"up to\" is a range whose low end is zero (CR 601.2c).", path));

            if (target.Count is null)
                findings.Add(new(LintSeverity.Error, "L049", "Target sets Min with no Count; a range needs a high end (CR 601.2c).", path));

            if (target is { Min: Fixed low, Count: Fixed high } && low.N > high.N)
                findings.Add(new(LintSeverity.Error, "L049", $"Target's Min {low.N} is above its Count {high.N}; no number of targets satisfies that range (CR 601.2c).", path));
        }
    }
}
