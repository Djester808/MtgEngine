using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>
/// Two more moments of the turn the rest of the engine cannot take for itself: the monarch's draw
/// at the beginning of their end step (CR 724.3) and the cleanup step dropping a delayed triggered
/// ability whose duration has ended (CR 603.7c with CR 514.2).
/// </summary>
public sealed partial class TurnSystem
{
    /// <summary>
    /// CR 724.3: the monarch draws a card at the beginning of their end step.
    /// <para>
    /// The rule gives the *player* the triggered ability "at the beginning of your end step, draw a
    /// card", and nothing in this engine can give a player an ability — a card is data and the
    /// monarch is not a card — so the moment is taken here as a turn-based action, which is the
    /// decision recorded for CR 724.3 in <c>seam-requests/E9r.md</c>. The one observable difference
    /// is that this draw does not use the stack, so nothing can be cast in response to it; the draw
    /// itself, its replacements and its own triggers are the zone system's as they are for every
    /// other draw.
    /// </para>
    /// <para>
    /// "Your end step" is the end step of a turn the monarch is the active player of, so the monarch
    /// draws once a round at most and draws nothing in anyone else's end step. A player who has lost
    /// the game (CR 104.3) draws nothing, whatever the designation still says.
    /// </para>
    /// </summary>
    [RuleRef("724.3")]
    private void EndStep(Log log)
    {
        var state = log.State;
        if (state.Monarch is not { } monarch || monarch != state.Turn.ActivePlayer)
            return;
        if (state.Player(monarch).HasLost)
            return;

        log.Merge(zones.Draw(state, monarch, 1));
    }

    /// <summary>
    /// The delayed triggered abilities the end of this turn ends, CR 603.7c: an ability created with
    /// a duration stops existing when that duration does, whether or not it ever triggered. The
    /// cleanup step is where "until end of turn" runs out, CR 514.2, so that is the duration read
    /// here; a delayed trigger with no duration, or one dated to something else (end of combat,
    /// your next turn, permanently), is left alone — the step that ends *its* duration drops it, and
    /// dropping it here would silently delete an ability that still has a window.
    /// </summary>
    [RuleRef("603.7c")]
    private static ImmutableList<int> DatedDelayedTriggers(GameState state) =>
        [.. state.DelayedTriggers.Where(t => t.Until is UntilEndOfTurn).Select(t => t.Id)];
}
