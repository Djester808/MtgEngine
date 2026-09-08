using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// What one creature would cost its controller to attack with, CR 508.1h, asked of whoever
/// determines and pays that total.
/// <para>
/// This is a seam and not a reading. CR 508.1h locks the total in against the declaration being
/// made, CR 508.1j pays it in full or not at all, and the amount inside the cost can count the
/// declaration itself ("{2} for each creature attacking you") — a second reading of any of that
/// in the view layer is exactly the drift the <c>CouldAttack</c> seam removed for CR 508.1a. So
/// the view asks the subsystem that would charge it, and the answer the player is offered can
/// never disagree with the price the declaration actually pays.
/// </para>
/// </summary>
[RuleRef("508.1h")]
public interface IAttackTaxes
{
    /// <summary>
    /// The tax this creature owes to attack now, or null when it owes none — which is every
    /// creature in a game where nothing prints CR 508.1g's cost, so null is the ordinary answer
    /// and not a failure. The offer says what the total is and whether it can be paid; a cost
    /// this engine cannot express as mana (CR 508.1h) is an offer that is not payable, never an
    /// exception and never a silently dropped attacker.
    /// </summary>
    [RuleRef("508.1g")]
    AttackTaxOffer? TaxToAttack(GameState state, PlayerId player, ObjectId attacker);
}

/// <summary>
/// The attack-tax half of the legal-actions view, CR 508.1d: the taxed creatures are offered
/// with their price rather than hidden, because paying is the player's choice and a choice
/// cannot be made from a list that does not say what it costs.
/// </summary>
[RuleRef("508.1d")]
public static class AttackTaxes
{
    /// <summary>
    /// One offer per eligible attacker that owes a tax, in the order the attackers were offered
    /// in, CR 508.1d.
    /// <para>
    /// <paramref name="attackers"/> is the view's own <c>EligibleAttackers</c> and nothing else:
    /// CR 508.1d makes a taxed creature able to attack, so it is already in that list, and this
    /// prices what is there rather than deciding again what may be declared. A creature that owes
    /// nothing has no entry, so an untaxed combat is an empty list and no client draws a price
    /// tag on an ordinary attacker. A game composed without the seam offers no prices at all —
    /// the same bargain the view makes when it is composed without a combat system: say nothing
    /// rather than guess at the rule.
    /// </para>
    /// </summary>
    [RuleRef("508.1d")]
    public static ImmutableList<AttackTaxOffer> For(
        IAttackTaxes? taxes,
        GameState state,
        PlayerId player,
        ImmutableList<ObjectId> attackers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attackers);
        if (taxes is null || attackers.IsEmpty)
            return [];

        var offers = ImmutableList.CreateBuilder<AttackTaxOffer>();
        foreach (var attacker in attackers)
        {
            if (taxes.TaxToAttack(state, player, attacker) is { } offer)
                offers.Add(offer with { Attacker = attacker });
        }

        return offers.ToImmutable();
    }
}
