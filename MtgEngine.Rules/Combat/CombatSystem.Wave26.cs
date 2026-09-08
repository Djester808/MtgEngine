using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// Two things combat owes the rest of the game: the cost some creatures have to pay to attack
/// (CR 508.1g–508.1j), and the half of the monarch designation that moves on combat damage
/// (CR 725.2).
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>The reason a tax's mana spend is logged under, so a view can tell it from a spell's payment.</summary>
    private const string AttackCost = "attack";

    /// <summary>
    /// CR 508.1h–508.1j — the cost of this declaration, paid.
    /// <para>
    /// A "can't attack unless its controller pays" line is <b>not</b> a restriction: CR 508.1c is
    /// about effects that forbid an attack outright or admit it on a board condition, and CR 508.1d
    /// says in as many words that the player "is not required to pay that cost". The cost belongs to
    /// CR 508.1h, where the active player <i>determines the total cost to attack</i> and it becomes
    /// locked in, and to CR 508.1j, where it is paid in full — "partial payments are not allowed".
    /// </para>
    /// <para>
    /// So the total is determined here against the declaration being made — the amount inside the
    /// cost reads the chosen attackers, which is why it is evaluated against a state the
    /// <see cref="AttackersDeclared"/> has already been folded into — and paid before the creatures
    /// become attacking at CR 508.1k. A cost that cannot be paid refuses the whole declaration
    /// rather than dropping the creature that owed it: CR 508.1's preamble says the declaration is
    /// illegal and CR 733 returns the game to the moment before it. That is the one place this
    /// diverges from the wave-26 brief, which asked for the unpaying attacker to be left out; the
    /// rules text does not offer that, because the creatures were already chosen at CR 508.1a and
    /// the total was locked in at CR 508.1h before any payment is attempted.
    /// </para>
    /// </summary>
    [RuleRef("508.1h")]
    private Outcome PayAttackTax(GameState state, PlayerId player, ImmutableDictionary<ObjectId, CombatTarget> declared)
    {
        var taxes = AttackTaxes(state, player, declared);
        if (taxes.IsEmpty)
            return Outcome.Unchanged(state);

        // CR 508.1h: the amount inside the cost ("{2} for each creature they control that's
        // attacking you") counts the declaration being made, so it is read against the board the
        // declaration would produce and not against the board before it.
        var lockedIn = new AttackersDeclared(player, declared).Apply(state);
        var symbols = ImmutableList<ManaSymbol>.Empty;
        var elsewhere = ImmutableList<Cost>.Empty;
        foreach (var cost in CostPayment.Flatten(taxes))
        {
            if (TaxSymbols(lockedIn, player, cost) is { } mana)
                symbols = symbols.AddRange(mana);
            else
                elsewhere = elsewhere.Add(cost);
        }

        // CR 508.1j — one payment for the whole total, in full or not at all. The mana half is
        // spent out of the pool here; every other cost was found payable at CR 508.1h and is paid
        // through the casting system, the only system that can build the cost context it needs.
        var paid = CostPayment.PaySymbols(state, player, symbols, null, AttackCost).Outcome;
        foreach (var cost in elsewhere)
        {
            var casting = Casting ?? throw new RefusedException(NoCasting, "508.1h");
            paid = paid.Then(s => casting.PayForRule(s, player, cost));
        }

        return paid;
    }

    /// <summary>
    /// CR 508.1h — every attack cost this declaration owes, in the order the attackers were
    /// declared in.
    /// <para>
    /// The cost is read off each attacker exactly as CR 508.1c's restriction is, because that is
    /// how a "creatures can't attack you unless…" effect reaches the creatures it taxes, and it is
    /// totalled <b>once per effect</b> rather than once per attacker: the scaling ("for each
    /// creature they control that's attacking you") lives inside the cost's own amount, so charging
    /// it per attacker would charge the square of what the card says. Two separate permanents that
    /// each print the same tax are two costs, and are counted as such by the number of copies of
    /// that effect any single attacker carries — an effect record knows nothing about which
    /// permanent created it, so that count is the only evidence of the second copy there is.
    /// </para>
    /// </summary>
    [RuleRef("508.1h")]
    private ImmutableList<Cost> AttackTaxes(GameState state, PlayerId player, ImmutableDictionary<ObjectId, CombatTarget> declared)
    {
        var owed = new Dictionary<CantAttack, int>();
        foreach (var (attacker, target) in declared)
        {
            var context = Context(attacker, player);
            var copies = new Dictionary<CantAttack, int>();
            foreach (var restriction in Statics<CantAttack>(_characteristics.Of(state, attacker)))
            {
                if (restriction.UnlessPays is null || !Taxes(state, restriction, target, context))
                    continue;
                copies[restriction] = copies.GetValueOrDefault(restriction) + 1;
            }

            foreach (var (effect, count) in copies)
                owed[effect] = Math.Max(owed.GetValueOrDefault(effect), count);
        }

        return [.. owed.SelectMany(pair => Enumerable.Repeat(pair.Key.UnlessPays!, pair.Value))];
    }

    /// <summary>
    /// Whether this attack owes that cost, CR 508.1g. A tax naming no player is owed by every
    /// attack the creature makes; one naming a player is owed only when that player is the one
    /// being attacked, which is the same reading <see cref="DefendingPlayer"/> gives CR 508.1c's
    /// restrictions — a planeswalker's or a battle's controller stands in for the player, CR 506.1.
    /// </summary>
    [RuleRef("508.1g")]
    private bool Taxes(GameState state, CantAttack restriction, CombatTarget target, EffectContext context)
    {
        if (restriction.Player is null)
            return true;

        var taxing = _query.Players(state, restriction.Player, context);
        return DefendingPlayer(state, target) is { } defender && taxing.Contains(defender);
    }

    /// <summary>
    /// One attack cost as mana symbols, CR 508.1h — or <c>null</c> when it is not mana and pays
    /// through the casting system instead.
    /// <para>
    /// Mana is spent straight out of the pool: CR 508.1i gives the player a chance to activate
    /// mana abilities and CR 508.1j spends what is there. Every other cost needs the cost context
    /// only <see cref="Casting"/> can build, so this asks it whether the payment would go through
    /// (CR 118.6, <see cref="ICastingSystem.CanPayForRule"/>, which changes nothing) at the moment
    /// the total is determined and locked in. An answer that says it would not refuses the whole
    /// declaration <b>before anything at all has been paid</b>: CR 508.1j allows no partial
    /// payment, CR 508.1's preamble makes a declaration a player cannot comply with illegal, and
    /// CR 733 returns the game to the moment before it. With no casting system in the game the
    /// older refusal stands, so a non-mana tax is still never waved through.
    /// </para>
    /// </summary>
    [RuleRef("508.1h")]
    private ImmutableList<ManaSymbol>? TaxSymbols(GameState lockedIn, PlayerId player, Cost cost)
    {
        switch (cost)
        {
            case ManaCost printed:
                return ManaSymbols.Parse(printed.Mana);

            // CR 118.8 — generic mana the game counts, counted here because CR 508.1h is where the
            // total is determined and locked in; a later change to the amount is ignored.
            case PayManaCost counted:
                var generic = _query.Evaluate(lockedIn, counted.Generic, Context(null, player));
                if (generic < 0)
                    throw new RefusedException("An attack cost cannot be less than nothing.", "508.1h");
                return [.. ManaSymbols.Parse(counted.Colored), new ManaSymbol { Generic = generic }];

            // CR 508.1h with 118.6 — the total is determined here, so an unpayable cost is found
            // here too, while nothing has been spent.
            default:
                if (Casting is not { } casting)
                    throw new RefusedException(NoCasting, "508.1h");
                if (casting.CanPayForRule(lockedIn, player, cost) is { } refusal)
                    throw new RefusedException(refusal.Reason, "508.1h");
                return null;
        }
    }

    /// <summary>What a non-mana attack cost is refused with when the game has no casting system to pay it, CR 508.1h.</summary>
    private const string NoCasting = "That cost to attack cannot be paid yet.";

    /// <summary>
    /// CR 725.2 — "whenever a creature deals combat damage to the monarch, its controller becomes
    /// the monarch."
    /// <para>
    /// The road is the turn system's: the moment is taken by the system that performs it rather
    /// than raised as a triggered ability. CR 725.2's two abilities "have no source" and belong to a
    /// player, and nothing in this engine can give a player an ability — an ability is data on a
    /// card and the monarch is not a card — which is the decision already recorded for the end-step
    /// draw in <c>seam-requests/E9r.md</c> and taken in <c>Turn/TurnSystem.Wave24.cs</c>. Taking the
    /// other half the same way keeps one rule on one road. The observable difference from the
    /// printed ability is that this does not use the stack, so nothing can be cast in response
    /// between the damage and the crown moving; a card's own <c>BecomesMonarch</c> trigger still
    /// fires, because the trigger system matches the <see cref="MonarchChanged"/> this emits.
    /// </para>
    /// <para>
    /// It is read per hit, off the damage that was actually dealt: a hit a prevention shield stopped
    /// deals nothing and moves nothing, exactly as CR 702.15b's lifelink reads the same events. The
    /// controller is the one the batch was computed with (CR 510.2), so a creature that dies to
    /// first strike after connecting still crowns the player who attacked with it. CR 725.3 leaves
    /// the monarch alone when their own creature deals the damage: there is at most one monarch and
    /// re-designating the same player changes nothing.
    /// </para>
    /// </summary>
    [RuleRef("725.2")]
    private static Outcome MonarchMoves(GameState state, Hit hit, ImmutableList<GameEvent> dealt)
    {
        if (state.Monarch is not { } monarch)
            return Outcome.Unchanged(state);

        var struck = dealt.OfType<DamageDealt>().Any(d => d.Combat && d.Source == hit.Source && d.ToPlayer == monarch && d.Amount > 0);
        var claimant = hit.Characteristics.Controller;
        if (!struck || claimant == monarch || state.Player(claimant).HasLost)
            return Outcome.Unchanged(state);

        var crowned = new MonarchChanged(claimant);
        return new Outcome(crowned.Apply(state), [crowned]);
    }
}
