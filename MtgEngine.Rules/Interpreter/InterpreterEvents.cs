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
    /// <summary>
    /// At the FRONT. CR 500.7: "If a player would begin an extra turn... the most recently
    /// created turn will be taken first." TurnState.ExtraTurns is documented as "most recent
    /// first" and TurnSystem.BeginNextTurn takes ExtraTurns[0], so appending made the queue
    /// FIFO where both the contract and the rule are LIFO: two extra turns created in one
    /// window were then taken in the order they were created rather than the reverse.
    /// </summary>
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Turn = state.Turn with { ExtraTurns = state.Turn.ExtraTurns.Insert(0, Player) } };
    }
}
