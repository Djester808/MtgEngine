using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// The arms the vocabulary's wave-22 and wave-19 additions asked the matcher for: the declaration
/// of attackers as one event (CR 508.1), the draw a player's own draw step spares (CR 121.1 with
/// CR 504.1), and the damage a firing carries so the ability can read it as it resolves
/// (CR 603.10). Every trigger kind no arm of <see cref="TriggerMatching.Match"/> answers arrives
/// here, and one this class does not know either matches nothing rather than throwing.
/// </summary>
internal sealed partial class TriggerMatching
{
    /// <summary>The trigger kinds added after the first pass of the matcher, CR 603.2.</summary>
    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> Wave23(GameState state, Trigger trigger, Ev.GameEvent happened, EffectContext ctx) =>
        trigger switch
        {
            AttacksWithCreatures attack => AttackedWithCreatures(state, attack, happened, ctx),
            _ => Wave25(state, trigger, happened, ctx),
        };

    /// <summary>
    /// CR 508.1: attackers are declared once, as a single turn-based action, so an ability that
    /// triggers on a player attacking with creatures fires once for that declaration however many
    /// creatures it names. Firing per creature is CR 508.2, which is what <c>Attacks</c> does, and
    /// which would draw three cards for a three-creature attack.
    /// <para>
    /// The firing is about the player who declared, and it carries every creature of that
    /// declaration as the objects it triggered on, so "those creatures" is the declaration and not
    /// whatever is attacking by the time the ability resolves. A declaration with no attacker is not
    /// an attack (CR 508.1a leaves the step with nothing declared), so nothing fires for it.
    /// </para>
    /// </summary>
    [RuleRef("508.1")]
    private ImmutableList<TriggerSubject> AttackedWithCreatures(GameState state, AttacksWithCreatures trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.AttackersDeclared declared || declared.Attackers.Count == 0)
            return [];
        if (!_query.Players(state, trigger.By, ctx).Contains(declared.Player))
            return [];
        return [new TriggerSubject([.. Ordered(declared.Attackers).Select(a => a.Key)], declared.Player)];
    }

    /// <summary>
    /// CR 121.1 with CR 504.1: "except the first card they draw in each of their draw steps". The
    /// spared draw is one per draw step and not one per turn — an extra draw step spares one more —
    /// and it is spared only in that player's <em>own</em> draw step, so a draw taken during someone
    /// else's draw step is spared nothing. A player who draws two cards as the first action of their
    /// draw step has only the first of them spared, and every draw outside a draw step fires.
    /// </summary>
    [RuleRef("121.1")]
    private static bool SparedByTheirDrawStep(PlayerId player, TriggerWindow window) =>
        window.Step == Step.Draw
        && window.State.Turn.ActivePlayer == player
        && DrawsInThisStep(window, player) == 1;

    /// <summary>
    /// How many cards this player has drawn since this step began, as of this event. The state keeps
    /// the count — <c>PlayerState.ObjectsDrawnThisStep</c>, raised by <c>ObjectDrawn</c> and cleared
    /// by <c>StepBegan</c> (CR 121.1 with CR 500.1) — so nothing walks the batch for a step boundary
    /// any more. <c>== 1</c> at the call above is the draw being matched: <see cref="TriggerWindow"/>
    /// carries the state after the whole batch, so this draw is folded in, and the draws the batch
    /// took after it come back off, which is the same subtraction
    /// <see cref="TriggerWindow.DrawsSoFar"/> makes for the turn's count.
    /// <para>
    /// Reading the state rather than the batch closes the case the batch walk knowingly got wrong: a
    /// draw step whose own draw was skipped (CR 614.5's replacement, or the starting player's first
    /// turn under CR 103.7a) leaves the count at 0, so a draw taken later in that step — in a batch
    /// of its own, carrying no <c>StepBegan</c> for a walk to find — is the first of that step and is
    /// spared. It also keeps a draw taken in the upkeep out of the reading, which the turn's count
    /// cannot do.
    /// </para>
    /// </summary>
    [RuleRef("504.1")]
    private static int DrawsInThisStep(TriggerWindow window, PlayerId player)
    {
        var later = 0;
        for (var i = window.Index + 1; i < window.Since.Count; i++)
        {
            if (window.Since[i] is Ev.ObjectDrawn draw && draw.Player == player)
                later++;
        }

        return Math.Max(0, window.State.Player(player).ObjectsDrawnThisStep - later);
    }

    /// <summary>
    /// CR 603.10: an ability that triggered on damage may read how much damage that event dealt
    /// ("draw that many cards"), and the amount is the event's rather than the board's — the object
    /// damaged may be dead and its marked damage gone by the time the ability resolves. A number is
    /// a value and not an object, so it cannot travel in <see cref="TriggerSubject.Objects"/>; the
    /// firing carries it in the channel a firing's values already use,
    /// <see cref="TriggerSubject.Chosen"/>, under <see cref="TriggerSystem.TriggeringDamageAmount"/>,
    /// which reaches the stack entry and so the resolution's context. The amount reader that parses
    /// it there is asked for in <c>seam-requests/E6g.md</c>.
    /// </summary>
    [RuleRef("603.10")]
    private static ImmutableList<TriggerSubject> WithTriggeringDamage(ImmutableList<TriggerSubject> subjects, Ev.DamageDealt damage) =>
        subjects.IsEmpty
            ? subjects
            : [.. subjects.Select(s => s with
            {
                Chosen = s.Chosen.SetItem(
                    TriggerSystem.TriggeringDamageAmount,
                    damage.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            })];
}
