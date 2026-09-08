using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// The price on one taxed attacker, asked by the legal-actions view, CR 508.1d with 508.1h.
/// <para>
/// CR 508.1d says the player "is not required to pay that cost", so a taxed creature is offered
/// as an attacker and what it would cost is offered beside it. The reading is here and not in
/// <c>Legal/**</c> or <c>Views/**</c> for one reason: CR 508.1h determines the total against the
/// declaration being made, the amount inside the cost may count that declaration, and only this
/// subsystem knows either. A second reading anywhere else is the drift the <c>CouldAttack</c>
/// seam removed for CR 508.1a — and worse here, because a price the view invents can disagree
/// with the price <see cref="DeclareAttackers"/> actually charges.
/// </para>
/// </summary>
[RuleRef("508.1d")]
public sealed partial class CombatSystem : Views.IAttackTaxes
{
    /// <summary>
    /// The tax this one creature owes to attack now, CR 508.1h, or null when it owes none.
    /// <para>
    /// The total is read exactly the way <see cref="PayAttackTax"/> reads it — the same
    /// <see cref="AttackTaxes"/> over a declaration of this creature alone, evaluated against the
    /// state that declaration's <see cref="AttackersDeclared"/> has been folded into — so the
    /// offer and the charge come from one piece of code and cannot disagree. Nothing is applied:
    /// the folded state is local and discarded, and no cost is paid or asked to be paid.
    /// </para>
    /// <para>
    /// Two things this deliberately does not do. It does not throw: a view that raises a
    /// <see cref="RefusedException"/> hides the attacker instead of pricing it, so the refusal arm
    /// of <see cref="TaxSymbols"/> becomes an offer that is not payable, carrying the cost's own
    /// text. And it does not read CR 508.1i — the window to activate mana abilities between the
    /// total being locked in and the payment — because this engine does not take that step, so
    /// <c>Payable</c> is "not out of the pool you are holding" and never "this attack is illegal".
    /// </para>
    /// </summary>
    [RuleRef("508.1h")]
    public Views.AttackTaxOffer? TaxToAttack(GameState state, PlayerId player, ObjectId attacker)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!OnBattlefield(state, attacker))
            return null;

        var declared = ImmutableDictionary<ObjectId, CombatTarget>.Empty.Add(attacker, PricedAgainst(state, player));
        var owed = AttackTaxes(state, player, declared);
        if (owed.IsEmpty)
            return null;

        // CR 508.1h — the total is determined against the board the declaration would produce.
        var lockedIn = new AttackersDeclared(player, declared).Apply(state);
        var mana = string.Empty;
        var elsewhere = new List<string>();
        var symbols = ImmutableList<ManaSymbol>.Empty;
        var payable = true;
        foreach (var cost in CostPayment.Flatten(owed))
        {
            switch (cost)
            {
                case ManaCost printed:
                    mana += printed.Mana;
                    symbols = symbols.AddRange(ManaSymbols.Parse(printed.Mana));
                    break;

                // CR 118.8 — the generic half the game counts, counted here because CR 508.1h is
                // where the total is determined; a later change to the amount is ignored. An
                // amount that comes out below nothing is no total at all, so it is offered as the
                // cost it is and not payable, where the declaration itself refuses (CR 508.1h).
                case PayManaCost counted:
                    var generic = _query.Evaluate(lockedIn, counted.Generic, Context(null, player));
                    if (generic < 0)
                    {
                        elsewhere.Add(Printed(cost));
                        payable = false;
                        break;
                    }

                    mana += Generic(generic, counted.Colored);
                    symbols = symbols.AddRange(ManaSymbols.Parse(counted.Colored)).Add(new ManaSymbol { Generic = generic });
                    break;

                // CR 508.1h with 118.6 — a cost that is not mana is payable only if the system
                // that would pay it says so, and unpayable outright when the game has none.
                default:
                    elsewhere.Add(Printed(cost));
                    payable = payable && Casting is { } casting && casting.CanPayForRule(lockedIn, player, cost) is null;
                    break;
            }
        }

        // CR 508.1j — the mana half is spent out of the pool, in full or not at all.
        payable = payable && ManaSymbols.Pay(state.Player(player).Mana, symbols).Paid;
        return new Views.AttackTaxOffer(attacker, Total(mana, elsewhere), payable);
    }

    /// <summary>
    /// Whom this creature is priced against, CR 506.2. A tax is owed only by an attack on the
    /// player the taxing effect names (CR 508.1c's "can't attack unless"), so a price without a
    /// defender is not a price at all.
    /// <para>
    /// In a two-player game the defending player is forced — CR 506.2 says the nonactive player
    /// is the defending player — so pricing against them invents nothing. With more than one
    /// opponent the price genuinely depends on which of them is attacked, and this signature does
    /// not carry that choice; rather than guess a defender, it prices against none, which leaves
    /// exactly the taxes CR 508.1g's "any attack" case owes whoever is attacked. Understating a
    /// multiplayer price is the honest failure here, and the seam request asks for the parameter
    /// that removes the case.
    /// </para>
    /// </summary>
    [RuleRef("506.2")]
    private static CombatTarget PricedAgainst(GameState state, PlayerId player)
    {
        CombatTarget? forced = null;
        foreach (var seat in state.Players)
        {
            if (seat.Id == player || seat.HasLost)
                continue;
            if (forced is not null)
                return new CombatTarget(null, null);
            forced = new CombatTarget(seat.Id, null);
        }

        return forced ?? new CombatTarget(null, null);
    }

    /// <summary>
    /// The generic half of a counted mana cost as it is printed, CR 107.4. A cost with coloured
    /// symbols and nothing generic prints those symbols alone, the way a card does; a cost with
    /// no symbols at all still prints its number, so an offer is never an empty string.
    /// </summary>
    [RuleRef("107.4")]
    private static string Generic(int generic, string? colored) =>
        generic > 0 || string.IsNullOrEmpty(colored) ? $"{{{generic}}}{colored}" : colored;

    /// <summary>
    /// A cost this engine cannot express as mana, as text. It is the vocabulary record's own
    /// spelling and not a phrase written here: a card's wording is data (a card is data or it
    /// does not exist), and inventing English for a cost in engine source would be a second,
    /// drifting copy of what the card file already says.
    /// </summary>
    [RuleRef("508.1h")]
    private static string Printed(Cost cost) => cost.ToString() ?? string.Empty;

    /// <summary>The whole total as one string, CR 508.1h: the mana, then everything else it also costs.</summary>
    [RuleRef("508.1h")]
    private static string Total(string mana, IReadOnlyList<string> elsewhere)
    {
        if (elsewhere.Count == 0)
            return mana;
        var rest = string.Join(", ", elsewhere);
        return mana.Length == 0 ? rest : $"{mana} {rest}";
    }
}
