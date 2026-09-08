using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>The zone-change arm of the matcher, CR 603.6: an object that arrived somewhere, described where it lies or as it left the battlefield.</summary>
internal sealed partial class TriggerMatching
{
    /// <summary>
    /// Any other zone change, CR 603.6. The destination is on the event and a trigger that names none
    /// fires on any arrival ("leaves your graveyard"); a named or excluded source zone is only knowable
    /// for the battlefield today, so "from anywhere other than the battlefield" is answered and any
    /// other exclusion is refused rather than guessed.
    /// </summary>
    [RuleRef("603.6")]
    private ImmutableList<TriggerSubject> Moved(GameState state, ZoneChange trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.ObjectMoved moved || (trigger.To is { } to && moved.To.Zone.Zone != to))
            return [];
        var left = state.History.LeftBattlefield.Any(o => o.Id == moved.From);
        if (trigger.From is { } from)
        {
            if (from != Zone.Battlefield)
                throw new Engine.RefusedException("A zone change's source zone is only knowable for the battlefield today.", "603.6");
            if (!left)
                return [];
        }
        if (trigger.NotFrom is { } notFrom)
        {
            if (notFrom != Zone.Battlefield)
                throw new Engine.RefusedException("A zone change's excluded source zone is only knowable for the battlefield today.", "603.6");
            if (left)
                return [];
        }

        // The object that arrived is the one to describe when it is still there; when the trigger
        // looks back past the battlefield the caller restored the object that left, CR 603.10.
        var subject = left ? moved.From : moved.To.Id;
        // CR 603.6 with 603.10: a card that arrived somewhere other than the battlefield is described
        // where it lies, so the filter is read in that zone rather than the battlefield it defaults to.
        var what = left || trigger.What.In is not null ? trigger.What : trigger.What with { In = moved.To.Zone.Zone };
        return _query.Matches(state, what, subject, ctx) ? [TriggerSubject.Of(subject, Controller(state, subject))] : [];
    }
}
