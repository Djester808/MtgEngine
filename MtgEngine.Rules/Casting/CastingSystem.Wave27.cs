using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Two readings this system owns that nothing outside it could make: the timing a granted flash
/// permission changes (CR 702.8b) and what it means to pay a cost the rules impose rather than a
/// spell or an ability (CR 118.6 with 601.2h). Both are here because the cost machinery and the
/// zone system it needs are this system's, and a second spelling of either elsewhere would drift.
/// </summary>
public sealed partial class CastingSystem
{
    /// <summary>
    /// CR 702.8b: "any time they could cast an instant" is what a flash permission grants, and
    /// nothing more — the cost is unchanged and the cast is an ordinary cast, so everything
    /// watching casts still sees it. The permission is the <see cref="MayCastAsThoughFlash"/> the
    /// interpreter creates on a player (E9u), read out of <see cref="GameState.ActiveEffects"/>:
    /// the players it applies to hold this caster, or its own <c>Who</c> resolves to them.
    /// <para>
    /// A null <c>What</c> admits every spell. A filter is asked about the card **where the card is
    /// now** — the permission is checked before CR 601.2a moves anything, so there is no spell on
    /// the stack yet to match, and a filter that names no zone would otherwise be read against the
    /// battlefield and admit nothing at all. The zone is therefore set to the card's own before the
    /// filter is asked; every other half of the filter is the query's one vocabulary.
    /// </para>
    /// </summary>
    [RuleRef("702.8b")]
    private bool MayCastAsThoughItHadFlash(GameState state, PlayerId player, GameObject card)
    {
        var context = CostPayment.Context(null, player, null);
        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is not MayCastAsThoughFlash flash)
                continue;

            var whose = active.AppliesToPlayers.Contains(player)
                || (active.AppliesToPlayers.IsEmpty && query.Players(state, flash.Who, context).Contains(player));
            if (!whose)
                continue;
            if (flash.What is not { } what || query.Matches(state, what with { In = card.Zone.Zone }, card.Id, context))
                return true;
        }

        return false;
    }

    /// <summary>
    /// CR 118.6 with 601.2h: a cost the rules or a resolving effect impose on a player, paid in
    /// full or not at all. This is the entry point a caller outside this folder needs when the
    /// payer is not casting anything — an "unless that player pays" offered while a spell resolves
    /// (CR 118.12a), an attack tax totalled by CR 508.1h — because paying needs a
    /// <see cref="CostContext"/>, and that needs the zone system only this system is constructed
    /// with (E4i § 2).
    /// <para>
    /// It throws <see cref="RefusedException"/> naming the rule when the cost cannot be paid, and
    /// the state it returns is the one the payment left; nothing is ever half paid, because
    /// <see cref="CostPayment.Pay"/> refuses before it emits (CR 118.3a: "a player can't pay a
    /// cost unless they have the necessary resources").
    /// </para>
    /// </summary>
    [RuleRef("118.6")]
    public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cost);
        var paying = source is { } id && state.Objects.TryGetValue(id, out var obj) ? obj : null;
        var context = new CostContext(
            paying,
            paying is null ? null : characteristics.Of(state, paying.Id),
            player,
            x,
            zones,
            query);
        return CostPayment.Pay(state, context, [cost]);
    }

    /// <summary>
    /// Whether <see cref="PayForRule"/> would go through, with the rule when it would not, and
    /// without changing anything: CR 118.6's "unless a player pays" has to be answerable before
    /// the answer is taken, or a player with nothing pays anyway and the unpaid half never runs.
    /// The same code performs the payment and answers this, so the two can never disagree — the
    /// trial payment is made against the state handed in and its result is discarded.
    /// </summary>
    [RuleRef("118.6")]
    public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
    {
        try
        {
            _ = PayForRule(state, player, cost, source, x);
            return null;
        }
        catch (RefusedException refused)
        {
            return new Refusal(refused.Reason, refused.Rule);
        }
    }
}
