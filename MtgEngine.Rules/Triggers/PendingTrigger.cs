using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Triggers;

/// <summary>An ability detected before priority, retaining its source and controller through choices.</summary>
public sealed record PendingTrigger(
    ObjectId? Source,
    int AbilityIndex,
    PlayerId Controller,
    TriggeredAbility Ability,
    TriggerSubject Subject,
    int? Delayed,
    ImmutableDictionary<string, string> Chosen)
{
    public GameObject? SourceObject { get; init; }
    public Events.GameEvent? Cause { get; init; }
    /// <summary>Position within the detection batch, not a global event-log identifier.</summary>
    public int? CauseIndex { get; init; }
}
