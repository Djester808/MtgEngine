using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

/// <summary>
/// The three events the turn system needs that the shared vocabulary did not have. They live
/// in the turn folder because the turn system is the only thing that raises them, and in the
/// events namespace because every reader of the log looks there.
/// </summary>
/// <remarks>
/// Each one is listed in <c>tests/MtgEngine.Rules.Tests/events.txt</c>: see
/// <c>seam-requests/E2.md</c>, which asks for those lines.
/// </remarks>
[RuleRef("514.2")]
public sealed record EndOfTurnEffectsEnded(ImmutableList<int> Effects) : GameEvent
{
    /// <summary>
    /// "Until end of turn" and "this turn" effects end in the cleanup step, CR 514.2, and are
    /// effects with a duration of CR 611.2a. The ids are carried so a replay removes exactly
    /// the effects the game removed rather than re-deciding which ones qualified.
    /// </summary>
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { ActiveEffects = [.. state.ActiveEffects.Where(e => !Effects.Contains(e.Id))] };
    }
}

/// <summary>An extra turn left the queue as it began, CR 500.7. Without this the same extra turn is taken forever.</summary>
[RuleRef("500.7")]
public sealed record ExtraTurnTaken(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var extras = state.Turn.ExtraTurns;
        var at = extras.IndexOf(Player);
        return at < 0 ? state : state with { Turn = state.Turn with { ExtraTurns = extras.RemoveAt(at) } };
    }
}

/// <summary>
/// Priority moved to the next player in turn order because the player before them passed,
/// CR 117.3d. This is not <see cref="PriorityGranted"/>: that starts a fresh round of priority
/// (a step began, CR 117.3a, or something resolved, CR 117.3b) and so clears the record of who
/// has passed, while a handoff inside one round has to keep it, because CR 117.4 ends the step
/// only when every player passes in succession. See <c>seam-requests/E2.md</c>.
/// </summary>
[RuleRef("117.3d")]
public sealed record PriorityHandedTo(PlayerId Player) : GameEvent
{
    public override GameState Apply(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Priority = state.Priority with { Holder = Player } };
    }
}
