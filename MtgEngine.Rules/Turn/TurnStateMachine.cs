using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.SBA;

namespace MtgEngine.Rules.Turn;

/// <summary>
/// Manages turn phase and step transitions.
/// Pure function: (GameState, action) => GameState.
/// </summary>
public static class TurnStateMachine
{
    private const int MaxHandSize = 7;

    /// <summary>
    /// Advance to the next step/phase. Called when all players pass priority
    /// with the stack empty (or in a step with no priority window).
    /// </summary>
    public static GameState AdvanceStep(GameState state)
    {
        var (nextPhase, nextStep) = GetNextStep(state.CurrentPhase, state.CurrentStep);
        state = ExitStep(state);
        state = state with { CurrentPhase = nextPhase, CurrentStep = nextStep };
        state = EnterStep(state);
        return state;
    }

    /// <summary>
    /// End the current turn and begin the next player's turn.
    /// </summary>
    public static GameState AdvanceTurn(GameState state)
    {
        var nextPlayerId = state.OpponentOf(state.ActivePlayerId);
        var nextTurn = state.Turn + 1;

        // Clear "until end of turn" effects would happen here in future
        // Clear mana pools
        state = ClearAllManaPools(state);

        state = state with
        {
            Turn = nextTurn,
            ActivePlayerId = nextPlayerId,
            PriorityPlayerId = nextPlayerId,
            CurrentPhase = Phase.Beginning,
            CurrentStep = Step.Untap,
            IsFirstTurn = false,
            Combat = null,
        };

        return EnterStep(state);
    }

    // =========================================================
    // Step entry / exit handlers
    // =========================================================

    private static GameState EnterStep(GameState state) => state.CurrentStep switch
    {
        Step.Untap          => EnterUntap(state),
        Step.Upkeep         => EnterUpkeep(state),
        Step.Draw           => EnterDraw(state),
        Step.Main           => EnterMain(state),
        Step.BeginningOfCombat => EnterBeginningOfCombat(state),
        Step.DeclareAttackers  => EnterDeclareAttackers(state),
        Step.DeclareBlockers   => EnterDeclareBlockers(state),
        Step.FirstStrikeDamage => EnterFirstStrikeDamage(state),
        Step.CombatDamage      => EnterCombatDamage(state),
        Step.EndOfCombat       => EnterEndOfCombat(state),
        Step.End            => EnterEndStep(state),
        Step.Cleanup        => EnterCleanup(state),
        _ => state
    };

    private static GameState ExitStep(GameState state) => state.CurrentStep switch
    {
        Step.Cleanup => ExitCleanup(state),
        _ => state
    };

    // --- Untap ---
    private static GameState EnterUntap(GameState state)
    {
        // Untap all permanents controlled by active player (no priority in untap step)
        var updated = state;
        foreach (var permanent in state.GetControlledPermanents(state.ActivePlayerId))
        {
            var untapped = permanent.Untap().ClearSummoningSickness();
            updated = updated.UpdatePermanent(untapped);
        }
        // No priority in untap step -- advance automatically
        return updated with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Upkeep ---
    private static GameState EnterUpkeep(GameState state)
    {
        // Give active player priority; upkeep triggered abilities would fire here
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Draw ---
    private static GameState EnterDraw(GameState state)
    {
        // First player skips draw on their very first turn (two-player rule)
        bool skipDraw = state.IsFirstTurn && state.Turn == 1;
        if (!skipDraw)
        {
            var activePlayer = state.ActivePlayer;
            // Drawing from empty library causes a loss -- SBAs will handle life/poison,
            // but draw-loss is handled separately via exception here for now
            if (activePlayer.Library.IsEmpty)
            {
                // Mark result -- active player loses for drawing from empty library
                return state with { Result = GameResult.InProgress }; // TODO: distinguish draw loss
            }
            var updated = activePlayer.DrawCard();
            state = state.UpdatePlayer(updated);
        }
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Main Phase ---
    private static GameState EnterMain(GameState state)
    {
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Beginning of Combat ---
    private static GameState EnterBeginningOfCombat(GameState state)
    {
        state = state with { Combat = new CombatState() };
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Declare Attackers ---
    private static GameState EnterDeclareAttackers(GameState state)
    {
        // Active player will now declare attackers via a game action
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Declare Blockers ---
    private static GameState EnterDeclareBlockers(GameState state)
    {
        return state with { PriorityPlayerId = state.OpponentOf(state.ActivePlayerId) };
    }

    // --- First Strike Damage ---
    private static GameState EnterFirstStrikeDamage(GameState state)
    {
        bool anyFirstStrike = state.Combat?.Attackers
            .Concat(state.Combat.AttackersToBlockers.Values.SelectMany(x => x))
            .Any(id => state.PermanentExists(id) &&
                       (state.GetPermanent(id).HasKeyword(KeywordAbility.FirstStrike) ||
                        state.GetPermanent(id).HasKeyword(KeywordAbility.DoubleStrike)))
            ?? false;

        if (!anyFirstStrike)
        {
            // Skip first strike step if no first strikers
            return AdvanceStep(state);
        }

        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Combat Damage ---
    private static GameState EnterCombatDamage(GameState state)
    {
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- End of Combat ---
    private static GameState EnterEndOfCombat(GameState state)
    {
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- End Step ---
    private static GameState EnterEndStep(GameState state)
    {
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    // --- Cleanup ---
    private static GameState EnterCleanup(GameState state)
    {
        // Active player discards to max hand size
        var activePlayer = state.ActivePlayer;
        while (activePlayer.Hand.Count > MaxHandSize)
        {
            // In a real game this would prompt the player to choose -- for now discard last
            var toDiscard = activePlayer.Hand.Last();
            activePlayer = activePlayer with
            {
                Hand = activePlayer.Hand.Remove(toDiscard),
                Graveyard = activePlayer.Graveyard.Add(toDiscard)
            };
        }
        state = state.UpdatePlayer(activePlayer);

        // Clear damage from all creatures
        foreach (var permanent in state.GetCreatures())
        {
            state = state.UpdatePermanent(permanent.ClearDamage());
        }

        // Clear mana pools
        state = ClearAllManaPools(state);

        // Until end of turn effects would expire here

        // No priority in cleanup unless triggered abilities fire
        return state;
    }

    private static GameState ExitCleanup(GameState state)
    {
        return state;
    }

    // =========================================================
    // Step sequencing
    // =========================================================

    public static (Phase phase, Step step) GetNextStep(Phase phase, Step step)
    {
        return (phase, step) switch
        {
            // Beginning phase
            (Phase.Beginning, Step.Untap)  => (Phase.Beginning, Step.Upkeep),
            (Phase.Beginning, Step.Upkeep) => (Phase.Beginning, Step.Draw),
            (Phase.Beginning, Step.Draw)   => (Phase.PreCombatMain, Step.Main),

            // Pre-combat main
            (Phase.PreCombatMain, Step.Main) => (Phase.Combat, Step.BeginningOfCombat),

            // Combat
            (Phase.Combat, Step.BeginningOfCombat) => (Phase.Combat, Step.DeclareAttackers),
            (Phase.Combat, Step.DeclareAttackers)  => (Phase.Combat, Step.DeclareBlockers),
            (Phase.Combat, Step.DeclareBlockers)   => (Phase.Combat, Step.FirstStrikeDamage),
            (Phase.Combat, Step.FirstStrikeDamage) => (Phase.Combat, Step.CombatDamage),
            (Phase.Combat, Step.CombatDamage)      => (Phase.Combat, Step.EndOfCombat),
            (Phase.Combat, Step.EndOfCombat)       => (Phase.PostCombatMain, Step.Main),

            // Post-combat main
            (Phase.PostCombatMain, Step.Main) => (Phase.Ending, Step.End),

            // Ending
            (Phase.Ending, Step.End)     => (Phase.Ending, Step.Cleanup),
            (Phase.Ending, Step.Cleanup) => (Phase.Beginning, Step.Untap), // signals turn change

            _ => throw new InvalidOperationException($"No step after {phase}/{step}")
        };
    }

    public static bool IsLastStepOfTurn(Phase phase, Step step) =>
        phase == Phase.Ending && step == Step.Cleanup;

    private static GameState ClearAllManaPools(GameState state)
    {
        var updated = state;
        foreach (var player in state.Players)
            updated = updated.UpdatePlayer(player.ClearManaPool());
        return updated;
    }
}
