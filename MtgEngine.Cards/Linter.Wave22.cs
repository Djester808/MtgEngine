namespace MtgEngine.Cards;

/// <summary>
/// The wave-22 records, which are all riders: a cost one permanent puts on other players' spells,
/// the excess of a damage event, a price on a mode, an exception carved out of a draw trigger and
/// the "if you do" of a mandatory cost. A rider is where two halves of one record contradict each
/// other most easily — a flag set beside the flag that denies it, a half that names something the
/// other half never provides — so each check below is exactly that, and each names the rule it
/// reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave22(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 118.8 with 601.2b: an additional cost is optional only because the caster chooses, and
        // a caster chooses to pay for what paying buys. With nothing gated on it the rider is a cost
        // no spell would ever take, which is a dropped reward rather than a card.
        foreach (var optional in nodes.OfType<OptionalAdditionalCost>())
        {
            if (optional.IfPaid is null)
                findings.Add(new(LintSeverity.Error, "L030", "OptionalAdditionalCost has no IfPaid; an optional additional cost with nothing gated on paying it is a cost no caster would take (CR 118.8, 601.2b).", path));
            if (optional.IfPaid is OptionalAdditionalCost)
                findings.Add(new(LintSeverity.Error, "L030", "OptionalAdditionalCost gates another OptionalAdditionalCost; what paying buys is an effect on that spell, not a second rider on it (CR 118.8).", path));
        }

        // CR 601.2f: a reduction applies to the cost as it stands, and the two flags name which half
        // of that cost it comes off. Generic-only and coloured are the two halves, so a reduction
        // that claims both says nothing about which symbols it removes.
        foreach (var modifier in nodes.OfType<CostModifier>().Where(m => m.OnlyColor is not null && m.GenericOnly))
            findings.Add(new(LintSeverity.Error, "L030", "CostModifier sets OnlyColor and GenericOnly together; a reduction comes off the generic half or off one colour's symbols, never both (CR 601.2f).", path));

        // CR 120.3d: excess damage is what is left over after lethal damage to a creature or
        // planeswalker. Damage dealt to a player is never in excess of anything, so with no object
        // damaged there is no excess for this rider to move.
        foreach (var damage in nodes.OfType<DealDamage>().Where(d => d.ExcessTo is not null && d.To is null))
            findings.Add(new(LintSeverity.Error, "L031", "DealDamage sets ExcessTo with no To; excess damage is what exceeds lethal damage to the object damaged, and damage to a player has no excess (CR 120.3d).", path));

        // The gate is on the redirection, so without a redirection it gates nothing: the damage
        // itself is dealt whatever the condition says (CR 702.19c, the printed sentence).
        foreach (var damage in nodes.OfType<DealDamage>().Where(d => d.ExcessIf is not null && d.ExcessTo is null))
            findings.Add(new(LintSeverity.Error, "L031", "DealDamage sets ExcessIf with no ExcessTo; the condition gates where the excess goes, and with nowhere for it to go it gates nothing (CR 120.3d).", path));

        CheckModeBudgets(nodes, path, findings);

        // CR 121.1 with 504.1: the exception spares the one draw the draw step's turn-based action
        // takes, so a trigger that admits only that draw and one that admits every draw but it are
        // the two readings of the same sentence and cannot both hold.
        foreach (var draws in nodes.OfType<Draws>().Where(d => d.ExceptFirstInDrawStep))
        {
            if (draws.FirstEachTurn)
                findings.Add(new(LintSeverity.Error, "L033", "Draws sets FirstEachTurn and ExceptFirstInDrawStep; one admits the first draw alone and the other admits every draw but it (CR 121.1).", path));
            if (draws.DuringDrawStep == false)
                findings.Add(new(LintSeverity.Error, "L033", "Draws excludes the draw step and then carves out its first draw; the exception names a draw this trigger already refuses (CR 121.1, 504.1).", path));
        }

        // CR 118.12: "you may pay [cost]. If you do, [effect]" is MayPay, one record. The same
        // sentence written as a May around a mandatory cost with its own "if you do" is the four-
        // record shape MayPay exists to replace, and the two spellings must never both be legal.
        foreach (var may in nodes.OfType<May>().Where(m => m.Do is PayCost { Then: not null }))
            findings.Add(new(LintSeverity.Error, "L034", "A May wraps a PayCost that carries Then; 'you may pay ... if you do' is one MayPay, and PayCost.Then is the mandatory cost the card prints without a choice (CR 118.12).", path));

        // CR 608.2c: the second half is what happens because the cost was paid. One that does
        // nothing is the bare instruction with a clause bolted on that no reader can act on.
        foreach (var payment in nodes.OfType<PayCost>().Where(p => p.Then is DoNothing))
            findings.Add(new(LintSeverity.Error, "L034", "PayCost sets Then to DoNothing; leave Then out for a cost with nothing hanging on it (CR 608.2c).", path));
    }

    /// <summary>
    /// A mode list spent against a budget, CR 700.2. The budget and the prices are the two halves of
    /// one printed line — "choose modes with total cost {5} or less" over priced modes — and either
    /// half alone leaves a choice nothing can rule.
    /// </summary>
    private static void CheckModeBudgets(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var choose in nodes.OfType<ChooseMode>())
        {
            var priced = choose.Modes.Count(m => m.Cost is not null);
            if (choose.Budget is not null && priced != choose.Modes.Count)
                findings.Add(new(LintSeverity.Error, "L032", "ChooseMode has a Budget and a mode with no Cost; a budget is spent against the price of every mode it can choose (CR 700.2).", path));
            if (choose.Budget is null && priced > 0)
                findings.Add(new(LintSeverity.Error, "L032", "A Mode has a Cost and its ChooseMode has no Budget; a mode's price is read against the budget and nowhere else (CR 700.2).", path));
        }
    }
}
