namespace MtgEngine.Cards;

/// <summary>
/// The wave-28 records, and the places their halves can contradict each other: a token whose power
/// is both worked out and printed, an edict that both keeps one of each type and names how many go,
/// a permission that charges for a cast it also makes free, a filter that names a zone and a list of
/// zones, a cast restriction whose zones are said twice, and a scenario that says one permanent is
/// both phased out and somewhere other than the battlefield. Each check names the rule it reads, as
/// <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave28(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 111.3: the creating ability defines the token's characteristics once. A power that is
        // both a number the ability works out and a number printed on the spec is two definitions of
        // one characteristic, and only one of them can be the token's text.
        foreach (var spec in nodes.OfType<TokenSpec>())
        {
            if (spec is { PowerAmount: not null, Power: not null })
                findings.Add(new(LintSeverity.Error, "L047", $"TokenSpec '{spec.Name}' sets both Power and PowerAmount; the ability defines the token's power once (CR 111.3).", path));

            if (spec is { ToughnessAmount: not null, Toughness: not null })
                findings.Add(new(LintSeverity.Error, "L047", $"TokenSpec '{spec.Name}' sets both Toughness and ToughnessAmount; the ability defines the token's toughness once (CR 111.3).", path));

            // CR 208.1: only a creature has a power and a toughness at all.
            if ((spec.PowerAmount is not null || spec.ToughnessAmount is not null) && !spec.Types.HasFlag(CardType.Creature))
                findings.Add(new(LintSeverity.Error, "L047", $"TokenSpec '{spec.Name}' works out a power or toughness but is not a creature ({spec.Types}); only a creature has them (CR 208.1).", path));
        }

        // CR 701.21 with 110.4: keeping one permanent of each permanent type and sacrificing the
        // rest says which are kept; a count or a selector says which go. One sentence, not both.
        foreach (var edict in nodes.OfType<Sacrifice>().Where(s => s.KeepOnePerPermanentType && (s.N is not null || s.What is not null)))
            findings.Add(new(LintSeverity.Error, "L047", "Sacrifice keeps one permanent of each permanent type and also carries N or What; the rule already says everything else goes (CR 701.21 with 110.4).", path));

        // CR 118.8 against 118.9: an additional cost is paid on top of the mana cost, so a
        // permission that waives or replaces the mana cost has nothing to add the cost to.
        foreach (var may in nodes.OfType<MayPlayFromZone>().Where(m => m.AdditionalCost is not null && (m.WithoutPayingManaCost || m.PayLifeEqualToManaValueInstead)))
            findings.Add(new(LintSeverity.Error, "L047", "MayPlayFromZone carries an AdditionalCost beside a waived or replaced mana cost; an additional cost is paid on top of the mana cost (CR 118.8, 118.9).", path));

        // CR 400.1: an object is in exactly one zone, so a filter that names a zone and a list of
        // zones states two memberships and only one of them would be read.
        foreach (var filter in nodes.OfType<ObjectFilter>())
        {
            if (filter is { In: not null, InAnyOf: not null })
                findings.Add(new(LintSeverity.Error, "L047", $"ObjectFilter sets In ({filter.In}) and InAnyOf; a filter names one zone or the list of zones it admits, never both (CR 400.1).", path));

            if (filter.InAnyOf is { Count: < 2 })
                findings.Add(new(LintSeverity.Error, "L047", $"ObjectFilter.InAnyOf holds {filter.InAnyOf.Count} zone(s); one zone is In and no zone is no zone rider at all (CR 400.1).", path));
        }

        // CR 601.3: the restriction names the zones it covers once. The filter's own CastFrom and
        // NotCastFrom describe a single spell's zone, and two riders on one restriction can disagree.
        foreach (var cant in nodes.OfType<CantCastSpells>().Where(c => c.CastFrom is not null))
        {
            if (cant.CastFrom is { Count: 0 })
                findings.Add(new(LintSeverity.Error, "L047", "CantCastSpells.CastFrom is empty; a restriction that names no zone leaves the field out and covers every zone (CR 601.3).", path));

            if (cant.Matching is { CastFrom: not null } or { NotCastFrom: not null })
                findings.Add(new(LintSeverity.Error, "L047", "CantCastSpells names its zones and its Matching filter carries a CastFrom rider too; two zone riders on one restriction can disagree (CR 601.3).", path));
        }

        _ = ability;
    }

    /// <summary>
    /// Phasing in a scenario, CR 702.26b: a permanent that phases out does not change zones, so a
    /// scenario that expects it phased out and expects it somewhere other than the battlefield
    /// expects two things that cannot both hold. The reverse — phased in and off the battlefield —
    /// is an ordinary card that left play, so only the phased-out half is checked.
    /// </summary>
    private static void CheckWave28Scenarios(CardFile file, List<LintFinding> findings)
    {
        var scenarioIndex = 0;
        foreach (var scenario in file.ScenarioList)
        {
            var phasedOut = scenario.Expect.OfType<PhasedOutIs>().Where(p => p.PhasedOut).Select(p => p.Card).ToHashSet(StringComparer.Ordinal);

            foreach (var elsewhere in scenario.Expect.OfType<InZone>().Where(z => z.Zone != Zone.Battlefield && phasedOut.Contains(z.Card)))
                findings.Add(new(LintSeverity.Error, "L048", $"Scenario expects '{elsewhere.Card}' phased out and in {elsewhere.Zone}; a phased-out permanent stays on the battlefield (CR 702.26b).", $"scenarios[{scenarioIndex}].expect"));

            scenarioIndex++;
        }
    }
}
