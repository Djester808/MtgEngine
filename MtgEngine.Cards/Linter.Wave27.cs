namespace MtgEngine.Cards;

/// <summary>
/// The wave-27 records, and the places their halves can contradict each other or say nothing: a
/// keyword a filter both demands and forbids, a player filter that admits nobody, a count of deaths
/// that asks where the dead creature is, a prevented amount with no prevention to read, an attack
/// tax with no price to repeat, a mana rider on a source that cannot be tapped, a group of shared
/// names that names the name, a spree mode priced twice, and a preparation card with no prepare
/// spell. Each check names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave27(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 702.1: the same keyword demanded and forbidden by one filter admits nothing. Flying is
        // the keyword the printed negation appears on most (CR 702.9).
        foreach (var filter in nodes.OfType<ObjectFilter>().Where(f => f.Has is { } h && f.NotHas is { } n && (h & n) != 0))
            findings.Add(new(LintSeverity.Error, "L045", $"ObjectFilter has both Has and NotHas over {filter.Has! & filter.NotHas!}; a filter that demands and forbids one keyword admits nothing (CR 702.1).", path));

        // CR 102.2: a player is not their own opponent, so a filter that is both admits nobody and
        // the greatest damage over an empty group is no number at all.
        foreach (var greatest in nodes.OfType<GreatestDamageDealtThisTurn>().Where(g => g.To is { Opponent: true, IsYou: true }))
            findings.Add(new(LintSeverity.Error, "L045", "GreatestDamageDealtThisTurn reads over a PlayerFilter that is both an opponent and you; no player is both (CR 102.2).", path));

        // CR 700.4: died is "put into a graveyard from the battlefield", so the creature counted has
        // already left the battlefield and a zone on the filter asks where it is now instead.
        foreach (var died in nodes.OfType<CreaturesDiedThisTurn>().Where(d => d.Matching?.In is not null))
            findings.Add(new(LintSeverity.Error, "L045", $"CreaturesDiedThisTurn narrows by zone ({died.Matching!.In}); a creature that died is counted by the move it made and not by where it is now (CR 700.4).", path));

        // CR 615: "prevented this way" is the damage this ability's own prevention prevented, so
        // there has to be a prevention here to read one out of.
        if (nodes.OfType<PreventedDamage>().Any() && !nodes.OfType<PreventDamage>().Any())
            findings.Add(new(LintSeverity.Error, "L045", "PreventedDamage reads how much this ability's prevention prevented and nothing here prevents damage (CR 615); a trigger on somebody else's prevention reads TriggeringPreventedDamage.", path));

        // CR 508.1h: the tax repeats a price, and a repeat of nothing is nothing.
        foreach (var tax in nodes.OfType<CantAttack>().Where(c => c.PerAttackingCreature && c.UnlessPays is null))
            findings.Add(new(LintSeverity.Error, "L045", "CantAttack charges per attacking creature with no UnlessPays cost to repeat (CR 508.1h).", path));

        // CR 605.1a: only a permanent can be tapped for mana, so a rider that watches one and a
        // source filter that admits a spell describe two different events.
        foreach (var added in nodes.OfType<WouldAddMana>().Where(a => a.TappedFor && a.FromSource?.Spell == true))
            findings.Add(new(LintSeverity.Error, "L045", "WouldAddMana watches a permanent being tapped for mana but its FromSource admits a spell; only a permanent can be tapped for mana (CR 605.1a).", path));

        // CR 201.2a: the sentence is that a group shares some name, and a named filter has already
        // said which — that is a plain CountOf and not a group at all.
        foreach (var group in nodes.OfType<GreatestSameNameCount>().Where(g => g.Filter.Named is not null))
            findings.Add(new(LintSeverity.Error, "L045", $"GreatestSameNameCount counts the largest group sharing a name but its filter already names '{group.Filter.Named}'; a named filter is a CountOf (CR 201.2).", path));

        CheckSpreeModes(nodes, path, findings);
        _ = ability;
    }

    /// <summary>
    /// Spree, CR 702.172, against the mode budget of CR 700.2: one price is really paid as an
    /// additional cost of the spell and the other is only spent against a budget. A mode that
    /// carries both says its price is paid and not paid, and a spree list that also carries a budget
    /// counts its modes twice in two different currencies.
    /// </summary>
    private static void CheckSpreeModes(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var mode in nodes.OfType<Mode>().Where(m => m is { Cost: not null, PaidCost: not null }))
            findings.Add(new(LintSeverity.Error, "L046", $"Mode '{mode.Text}' carries both a budget Cost and a PaidCost; one is spent against a ChooseMode budget and the other is really paid as an additional cost (CR 702.172).", path));

        foreach (var choose in nodes.OfType<ChooseMode>().Where(c => c.Budget is not null && c.Modes.Any(m => m.PaidCost is not null)))
            findings.Add(new(LintSeverity.Error, "L046", "ChooseMode sets a Budget over modes that carry a PaidCost; a spree spell pays its modes' costs for real and has no budget to spend (CR 702.172).", path));
    }

    /// <summary>
    /// A preparation card, CR 722.1, and its inset frame, CR 722.2a: the layout is the frame and the
    /// role is the half inside it, so one without the other is half a card. A prepare spell on any
    /// other layout is a face that no frame prints.
    /// </summary>
    private static void CheckWave27Faces(CardFile file, List<LintFinding> findings)
    {
        var hasPrepareSpell = file.Definition.Faces.Any(f => f.Role == FaceRole.PrepareSpell);

        if (file.Definition.Layout == Layout.Prepare && !hasPrepareSpell)
            findings.Add(new(LintSeverity.Error, "L046", "A Prepare layout has no face with the PrepareSpell role; the inset frame is the half that makes it a preparation card (CR 722.2a).", "layout"));

        if (file.Definition.Layout != Layout.Prepare && hasPrepareSpell)
            findings.Add(new(LintSeverity.Error, "L046", $"A face has the PrepareSpell role on a {file.Definition.Layout} layout; the inset frame is printed only on a preparation card (CR 722.1).", "layout"));
    }

    /// <summary>
    /// The token specs a copy creates, CR 707.2: a copy takes the copiable values of what it copies,
    /// name included, so the spec beside a <c>CopyOf</c> has no name to carry and L010 must not ask
    /// for one. Held by reference, because two nameless specs are equal records and only one of them
    /// may be a copy.
    /// </summary>
    private static bool IsCopiedTokenSpec(IReadOnlyList<object> nodes, TokenSpec spec) =>
        nodes.OfType<CreateToken>().Any(c => c.CopyOf is not null && ReferenceEquals(c.Token, spec));
}
