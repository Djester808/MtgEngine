using MtgEngine.Cards;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// Two readings the vocabulary's wave-14 pass asked the matcher for: the life an event moved,
/// carried on the firing the way the damage of a damage event already is (CR 603.10), and a step
/// reference that names the main phase without naming which of the two it is (CR 505.1).
/// </summary>
internal sealed partial class TriggerMatching
{
    /// <summary>
    /// CR 603.10 with CR 119: an ability that triggered on life being gained or lost may read how
    /// much life that event moved ("each opponent loses that much life"), and the amount is the
    /// event's rather than the board's — the player's life total has moved on by the time the
    /// ability resolves, and a life total is not a record of the last change to it. The firing
    /// carries the number in the same channel the damage amount uses,
    /// <see cref="TriggerSubject.Chosen"/>, so it reaches the stack entry and the resolution's
    /// context; the readers that parse it there are <c>TriggeringLifeGained</c> and
    /// <c>TriggeringLifeLost</c> (E5k).
    /// <para>
    /// Gain and loss are two keys, not one signed number. A gain writes only
    /// <see cref="TriggerSystem.TriggeringLifeGainedAmount"/> and a loss only
    /// <see cref="TriggerSystem.TriggeringLifeLostAmount"/>, both as a positive magnitude, so
    /// neither reader can answer with the other event's amount; a firing on any other event writes
    /// neither. Life lost is not damage either (a cost, an edict and a burn spell all lose life,
    /// and only one of them is damage), so these keys and the damage key never stand in for one
    /// another.
    /// </para>
    /// </summary>
    [RuleRef("603.10")]
    private static ImmutableList<TriggerSubject> WithTriggeringLife(ImmutableList<TriggerSubject> subjects, Ev.LifeChanged changed)
    {
        if (subjects.IsEmpty || changed.Delta == 0)
            return subjects;
        var key = changed.Delta > 0 ? TriggerSystem.TriggeringLifeGainedAmount : TriggerSystem.TriggeringLifeLostAmount;
        var amount = Math.Abs(changed.Delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [.. subjects.Select(s => s with { Chosen = s.Chosen.SetItem(key, amount) })];
    }

    /// <summary>
    /// CR 505.1: a turn has two main phases, and "the precombat and postcombat main phases are
    /// individually and collectively known as the main phase". <see cref="Step.Main"/> is the
    /// collective reading — "at the beginning of your next main phase" names neither one — so it
    /// matches whichever of the two begins next. Reading it as one of them fires at the wrong time
    /// or never: a delayed ability created during combat would wait for a precombat main phase that
    /// this turn has already had. A card that does name one still says
    /// <see cref="Step.PrecombatMain"/> or <see cref="Step.PostcombatMain"/>, and those two match
    /// only themselves — only the collective reference admits both.
    /// </summary>
    [RuleRef("505.1")]
    private static bool StepAdmits(Step wanted, Step began) =>
        wanted == began || (wanted == Step.Main && began is Step.PrecombatMain or Step.PostcombatMain);
}
