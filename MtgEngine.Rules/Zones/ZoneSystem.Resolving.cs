using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// A permanent spell becoming a permanent, CR 608.3, and the one arm of it no other subsystem can
/// perform: CR 608.3e, "if a permanent spell resolves but its controller can't put it onto the
/// battlefield, that player puts it into its owner's graveyard."
/// <para>
/// The replacement system can only refuse the entry (CR 613.11 with 101.2) — it is handed an
/// <see cref="ObjectMoved"/> and hands back what happens instead, which for a "can't enter the
/// battlefield" effect is nothing at all. That refusal is right for a card returned or searched
/// into play and for a token that is never created (CR 111.5), and it must not invent a second
/// move, because it does not know the move was a resolution. The zone system proposed the move and
/// is the one place that knows, so the follow-up is here.
/// </para>
/// <para>
/// Without it a permanent spell whose entry is refused sits on the stack for the rest of the game:
/// the casting system reads the move, finds no permanent, and finishes the resolution anyway, so
/// the card is in no zone any rule will ever look at again.
/// </para>
/// </summary>
[RuleRef("608.3e")]
public sealed partial class ZoneSystem
{
    /// <summary>
    /// A resolving permanent spell becomes a permanent, CR 608.3a: the ordinary move onto the
    /// battlefield under its controller's control, and then CR 608.3e when that move did not
    /// happen — the card goes to its <i>owner's</i> graveyard, not its controller's (CR 400.3), and
    /// the graveyard move is offered to the replacement effects like any other (CR 614.1).
    /// <para>
    /// A resolution that stopped to ask (CR 616.1) has not decided anything yet, so it is returned
    /// as it is: the answer is what says whether the permanent entered, and asking again on the
    /// same board would replace a question with a wrong graveyard. <paramref name="faceDown"/>
    /// carries CR 702.37c — a spell cast face down enters with the characteristics the spell had.
    /// </para>
    /// </summary>
    [RuleRef("608.3")]
    public Outcome ResolveOntoBattlefield(GameState state, ObjectId what, PlayerId controller, bool faceDown = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Objects.TryGetValue(what, out var spell))
            return Outcome.Unchanged(state);

        var battlefield = ZoneKey.Of(Cards.Zone.Battlefield, controller);
        var landed = Move(state, what, battlefield, Placement.Default, tapped: false, controller: controller, faceIndex: null, faceDown);

        // CR 616.1: nothing else happens until the affected player says which effect applies.
        if (landed.State.Pending is not null)
            return landed;

        // CR 608.3a: the move is what puts the permanent onto the battlefield. Anything else means
        // its controller couldn't, whether an effect refused the entry or the rules never proposed
        // it (CR 400.4a), and CR 608.3e is the same answer for both.
        if (Landed(landed, what))
            return landed;

        return landed.Then(s => Move(s, what, ZoneKey.Of(Cards.Zone.Graveyard, spell.Owner)));
    }

    /// <summary>
    /// Whether the outcome actually moved this object. The move is proposed, not performed
    /// (CR 614.1c), so the only honest answer is the event: an outcome carrying no
    /// <see cref="ObjectMoved"/> for this id is an entry that did not happen.
    /// </summary>
    [RuleRef("614.1c")]
    private static bool Landed(Outcome outcome, ObjectId what) =>
        outcome.Events.Any(e => e is ObjectMoved moved && moved.From == what);
}
