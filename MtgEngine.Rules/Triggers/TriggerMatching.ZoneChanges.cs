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
    /// fires on any arrival ("leaves your graveyard"); the source zone is read from the event for
    /// every zone (<see cref="Ev.ObjectMoved.LeftFrom"/>), and a move that never said which zone it
    /// left is refused rather than guessed.
    /// </summary>
    [RuleRef("603.6")]
    private ImmutableList<TriggerSubject> Moved(GameState state, ZoneChange trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.ObjectMoved moved || (trigger.To is { } to && moved.To.Zone.Zone != to))
            return [];

        // CR 603.10: the battlefield is answered by the turn's record, which is what keeps the
        // object that left readable at all; every other zone is on the event itself, CR 603.6.
        var left = state.History.LeftBattlefield.Any(o => o.Id == moved.From);
        var from = left ? Zone.Battlefield : moved.LeftFrom?.Zone;
        if (trigger.From is not null || trigger.NotFrom is not null)
        {
            if (from is not { } source)
                throw new Engine.RefusedException("That move did not say which zone the object left.", "603.6");
            if (trigger.From is { } wanted && source != wanted)
                return [];
            if (trigger.NotFrom is { } excluded && source == excluded)
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
