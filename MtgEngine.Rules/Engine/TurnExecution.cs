using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Engine;

public enum ExecutionBoundary { TurnAction, Checkpoint, Priority, Resolution, Finished }

/// <summary>
/// Immutable, server-only continuation. Keep this together with State; GameState alone
/// cannot resume execution. Callers must serialize operations on one authoritative branch.
/// </summary>
public sealed class TurnExecution
{
    private TurnExecution(GameState state, ExecutionBoundary boundary, PlayerId? recipient = null,
        ObjectId? resolving = null)
    {
        State = state;
        Boundary = boundary;
        PriorityRecipient = recipient;
        Resolving = resolving;
    }

    public GameState State { get; }
    public ExecutionBoundary Boundary { get; }
    public PlayerId? PriorityRecipient { get; }
    public ObjectId? Resolving { get; }
    public Phase Phase => State.Turn.Step switch
    {
        Step.Untap or Step.Upkeep or Step.Draw => Phase.Beginning,
        Step.PrecombatMain => Phase.PrecombatMain,
        Step.BeginningOfCombat or Step.DeclareAttackers or Step.DeclareBlockers
            or Step.FirstStrikeCombatDamage or Step.CombatDamage or Step.EndOfCombat => Phase.Combat,
        Step.PostcombatMain => Phase.PostcombatMain,
        Step.End or Step.Cleanup => Phase.Ending,
        _ => throw new NotSupportedException("Expected a concrete step or main phase."),
    };

    /// <summary>Starts after setup/opening hands. Only ordinary individual turns are supported.</summary>
    public static TurnExecution Start(GameState state, PlayerId startingPlayer)
    {
        Check(state);
        _ = state.Player(startingPlayer);
        if (state.Turn.Number != 0 || state.Outcome is not null || state.OpeningHands is not null || state.Pending is not null
            || state.Priority.Holder is not null || !state.In(ZoneKey.Shared(Zone.Stack)).IsEmpty)
            throw new InvalidOperationException("Finish setup before starting turns.");
        return BeginTurn(state, startingPlayer, 1);
    }

    /// <summary>Checks timing ownership only; card legality, costs and targets belong to the action provider.</summary>
    public void RequirePriority(PlayerId actor)
    {
        Require(ExecutionBoundary.Priority);
        if (State.Priority.Holder != actor)
            throw new InvalidOperationException("Only the priority holder may act.");
    }

    /// <summary>
    /// Executes an already validated priority action atomically (including immediate mana abilities). The provider receives
    /// a state without priority; success resets consecutive passes and checkpoints the same actor.
    /// No-op actions must be rejected by the provider. Exceptions leave this continuation intact.
    /// </summary>
    public TurnExecution Act(PlayerId actor, Func<GameState, GameState> action)
    {
        RequirePriority(actor);
        var result = Apply(action);
        return Checkpoint(result, actor);
    }

    public TurnExecution Pass(PlayerId actor)
    {
        RequirePriority(actor);
        var passes = State.Priority.PassedInSuccession.Add(actor);
        if (passes.Count < State.Players.Count)
        {
            var next = Next(State, actor);
            return new(State with { Priority = new(null, passes) }, ExecutionBoundary.Checkpoint, next);
        }
        var state = State with { Priority = PriorityState.None };
        var stack = state.In(ZoneKey.Shared(Zone.Stack));
        return stack.IsEmpty ? Advance(state, repeatCleanup: true)
            : new(state, ExecutionBoundary.Resolution, resolving: stack[^1]);
    }

    /// <summary>
    /// Trusted CR 117.5 provider must finish the SBA/trigger fixed point before returning.
    /// No built-in provider exists. In cleanup, report whether any SBA or waiting trigger
    /// occurred, even if the final stack is empty (CR 514.3a).
    /// </summary>
    public TurnExecution CompleteCheckpoint(Func<GameState, GameState> process, bool cleanupActivity = false)
    {
        Require(ExecutionBoundary.Checkpoint);
        if (cleanupActivity && State.Turn.Step != Step.Cleanup)
            throw new ArgumentException("Cleanup activity applies only in cleanup.", nameof(cleanupActivity));
        var result = Apply(process);
        if (result.Outcome is not null)
            return new(result, ExecutionBoundary.Finished);
        if (result.Turn.Step == Step.Cleanup && PriorityRecipient is null)
        {
            if (!cleanupActivity && result.In(ZoneKey.Shared(Zone.Stack)).IsEmpty)
                return Advance(result);
            return new(result with { Priority = new(result.Turn.ActivePlayer, []) }, ExecutionBoundary.Priority);
        }
        var passes = result.In(ZoneKey.Shared(Zone.Stack)).SequenceEqual(State.In(ZoneKey.Shared(Zone.Stack)))
            ? result.Priority.PassedInSuccession : [];
        return new(result with { Priority = new(PriorityRecipient, passes) },
            ExecutionBoundary.Priority);
    }

    /// <summary>
    /// Completes exactly the selected top object. The provider executes its effects and final
    /// disposition; other stack entries must remain in order. No resolver is inferred from text.
    /// </summary>
    public TurnExecution Resolve(Func<GameState, ObjectId, GameState> resolve)
    {
        Require(ExecutionBoundary.Resolution);
        ArgumentNullException.ThrowIfNull(resolve);
        var result = Apply(s => resolve(s, Resolving!.Value));
        var before = State.In(ZoneKey.Shared(Zone.Stack));
        if (!result.In(ZoneKey.Shared(Zone.Stack)).SequenceEqual(before.RemoveAt(before.Count - 1)))
            throw new NotSupportedException("Resolution must remove only the selected top stack entry.");
        return Checkpoint(result, State.Turn.ActivePlayer);
    }

    /// <summary>
    /// Mandatory trusted turn-based-action provider: untap (including phasing/day-night),
    /// draw, declare attackers, or cleanup (discard, then damage/effect expiration).
    /// This coordinator implements ordering, not these actions. Combat with attackers is unsupported.
    /// </summary>
    public TurnExecution CompleteTurnAction(Func<GameState, GameState> perform)
    {
        Require(ExecutionBoundary.TurnAction);
        var result = Apply(perform);
        if (result.Outcome is not null)
            return new(result, ExecutionBoundary.Finished);
        if (State.Turn.Step == Step.Untap)
            return Enter(EndStep(result), Step.Upkeep);
        if (State.Turn.Step == Step.DeclareAttackers && (result.Combat is not { AttackersDeclared: true } combat
            || !combat.Attackers.IsEmpty))
            throw new NotSupportedException("A combat provider must certify an empty attack declaration; combat execution is not implemented.");
        return Checkpoint(result, State.Turn.Step == Step.Cleanup ? null : State.Turn.ActivePlayer);
    }

    /// <summary>
    /// Opt-in ordinary turn actions for a rules subset without replacement/continuous effects,
    /// phasing, day/night or untap restrictions. Printed text is not interpreted. Cleanup that
    /// requires discarding and all combat declarations require a separate provider.
    /// </summary>
    public TurnExecution CompleteBasicTurnAction() => CompleteTurnAction(state =>
    {
        var active = state.Turn.ActivePlayer;
        var player = state.Player(active);
        switch (state.Turn.Step)
        {
            case Step.Untap:
                if (state.Objects.Values.Any(o => o.IsPermanent && o.PhasedOut))
                    throw new NotSupportedException("Phasing requires a turn-action provider.");
                return state with
                {
                    Objects = state.Objects.ToImmutableDictionary(pair => pair.Key, pair =>
                        pair.Value.IsPermanent && pair.Value.Controller == active
                            ? pair.Value with { Tapped = false, SummoningSick = false } : pair.Value),
                };
            case Step.Draw:
                var library = state.In(ZoneKey.Of(Zone.Library, active));
                if (library.IsEmpty)
                    return state with { Players = state.Players.SetItem(active.Value, player with { DrewFromEmptyLibrary = true }) };
                state = ZoneTransitions.Move(state, new(library[0], ZoneKey.Of(Zone.Hand, active))).State;
                return state with
                {
                    Players = state.Players.SetItem(active.Value, player with
                    {
                        ObjectsDrawnThisStep = checked(player.ObjectsDrawnThisStep + 1),
                        ObjectsDrawnThisTurn = checked(player.ObjectsDrawnThisTurn + 1),
                    }),
                };
            case Step.Cleanup:
                if (player.MaxHandSize < 0 || state.In(ZoneKey.Of(Zone.Hand, active)).Count > player.MaxHandSize)
                    throw new NotSupportedException("Cleanup discarding or modified hand limits require a turn-action provider.");
                return state with
                {
                    Objects = state.Objects.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.IsPermanent
                        ? pair.Value with { Damage = 0, DealtDeathtouchDamage = false } : pair.Value),
                };
            default:
                throw new NotSupportedException("This turn-based action requires an explicit provider.");
        }
    });

    private GameState Apply(Func<GameState, GameState> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var input = State with { Priority = new(null, State.Priority.PassedInSuccession) };
        var result = operation(input);
        Check(result);
        if (result.Turn != State.Turn || !result.Players.Select(p => p.Id).SequenceEqual(State.Players.Select(p => p.Id))
            || result.Priority != input.Priority || result.Pending is not null)
            throw new NotSupportedException("Providers cannot change turn, priority, seats, or suspend for a choice yet.");
        return result;
    }

    private void Require(ExecutionBoundary boundary)
    {
        Check(State);
        if (Boundary != boundary || State.Pending is not null || State.Outcome is not null)
            throw new InvalidOperationException("The requested operation is unavailable at this execution boundary.");
    }

    private static void Check(GameState state)
    {
        state.Validate();
        if (state.Players.Any(p => p.HasLost) || state.Turn.IsExtraTurn || !state.Turn.ExtraTurns.IsEmpty)
            throw new NotSupportedException("Player departure and extra turns require future scheduling support.");
    }

    private static PlayerId Next(GameState state, PlayerId player) => new((player.Value + 1) % state.Players.Count);

    private static TurnExecution Checkpoint(GameState state, PlayerId? recipient) =>
        state.Outcome is not null ? new(state with { Priority = PriorityState.None }, ExecutionBoundary.Finished)
            : new(state with { Priority = PriorityState.None }, ExecutionBoundary.Checkpoint, recipient);

    private static GameState EndStep(GameState state) => state with
    {
        Priority = PriorityState.None,
        Players = state.Players.Select(p => p with { Mana = ManaPool.Empty, ObjectsDrawnThisStep = 0 }).ToImmutableList(),
    };

    private static TurnExecution BeginTurn(GameState state, PlayerId active, int number) => new(state with
    {
        Turn = new(number, active, Step.Untap, active.Value),
        Priority = PriorityState.None,
        Combat = null,
        Players = state.Players.Select(p => p with { LandsPlayedThisTurn = 0, ObjectsDrawnThisTurn = 0 }).ToImmutableList(),
    }, ExecutionBoundary.TurnAction);

    private static TurnExecution Enter(GameState state, Step step)
    {
        state = state with { Turn = state.Turn with { Step = step } };
        return step is Step.Draw or Step.DeclareAttackers or Step.Cleanup
            ? new(state, ExecutionBoundary.TurnAction) : Checkpoint(state, state.Turn.ActivePlayer);
    }

    private static TurnExecution Advance(GameState state, bool repeatCleanup = false)
    {
        var next = state.Turn.Step switch
        {
            Step.Upkeep => state.Turn.Number == 1 && state.Players.Count == 2 ? Step.PrecombatMain : Step.Draw,
            Step.Draw => Step.PrecombatMain,
            Step.PrecombatMain => Step.BeginningOfCombat,
            Step.BeginningOfCombat => Step.DeclareAttackers,
            Step.DeclareAttackers when state.Combat is { AttackersDeclared: true } combat && combat.Attackers.IsEmpty => Step.EndOfCombat,
            Step.EndOfCombat => Step.PostcombatMain,
            Step.PostcombatMain => Step.End,
            Step.End => Step.Cleanup,
            Step.Cleanup => Step.Cleanup,
            _ => throw new NotSupportedException("This step requires combat or additional turn scheduling support."),
        };
        // Cleanup priority always repeats cleanup. A quiet cleanup checkpoint begins the next turn.
        var quietCleanup = state.Turn.Step == Step.Cleanup && !repeatCleanup;
        state = EndStep(state);
        if (quietCleanup)
            return BeginTurn(state, Next(state, state.Turn.ActivePlayer), checked(state.Turn.Number + 1));
        if (state.Turn.Step == Step.EndOfCombat)
            state = state with { Combat = null };
        return Enter(state, next);
    }
}
