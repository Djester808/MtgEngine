using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Triggers;

/// <summary>Observe taps and token creation before later movements remove their subjects or observers.</summary>
internal sealed record TriggerObservation(ImmutableList<GameEvent> Remaining,
    ImmutableList<PendingTrigger> Triggers)
{
    public static TriggerObservation Capture(ITriggerSystem triggers, GameState before,
        IReadOnlyList<GameEvent> events)
    {
        var remaining = ImmutableList.CreateBuilder<GameEvent>();
        var detected = ImmutableList.CreateBuilder<PendingTrigger>();
        var state = before;
        for (var index = 0; index < events.Count; index++)
        {
            var occurrence = events[index];
            state = occurrence.Apply(state);
            if (occurrence is ObjectChanged { Tapped: not null } or ObjectCreated { Object.IsToken: true })
            {
                detected.AddRange(triggers.Detect(state, [occurrence]).Select(trigger => trigger with
                {
                    CauseIndex = index,
                }));
            }
            else
            {
                remaining.Add(occurrence);
            }
        }
        return new(remaining.ToImmutable(), detected.ToImmutable());
    }
}
