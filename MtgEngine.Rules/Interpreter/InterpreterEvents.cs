using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The three events a resolving effect makes that no wave-1 subsystem had a spelling for. They
/// live here rather than in <c>Events/GameEvent.cs</c> because an event belongs to the subsystem
/// that produces it; <c>events.txt</c> names them all and is the coordinator's to update.
/// </summary>
[RuleRef("611.2")]
public sealed record ContinuousEffectCreated(ActiveEffect Effect) : GameEvent
{
    /// <summary>CR 611.2: the effect starts existing when the spell or ability resolves, with the timestamp it got then.</summary>
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(Effect);
        return state with
        {
            ActiveEffects = state.ActiveEffects.Add(Effect),
            NextTimestamp = Math.Max(state.NextTimestamp, Effect.Timestamp + 1),
        };
    }
}

/// <summary>A delayed triggered ability was created by a resolving effect, CR 603.7.</summary>
[RuleRef("603.7")]
public sealed record DelayedTriggerCreated(DelayedTriggerState Trigger) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(Trigger);
        return state with
        {
            DelayedTriggers = state.DelayedTriggers.Add(Trigger),
            NextTimestamp = Math.Max(state.NextTimestamp, Trigger.Id + 1),
        };
    }
}

/// <summary>An extra turn was created after this one, CR 500.7. The turn system takes it with <c>ExtraTurnTaken</c>.</summary>
[RuleRef("500.7")]
public sealed record ExtraTurnGranted(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Turn = state.Turn with { ExtraTurns = state.Turn.ExtraTurns.Add(Player) } };
    }
}
