using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Combat;
using MtgEngine.Rules.SBA;
using MtgEngine.Rules.Turn;
using System.Collections.Immutable;

namespace MtgEngine.Rules;

/// <summary>
/// Top-level orchestrator for a game. Accepts player actions and produces
/// a new GameState after applying rules, SBAs, and triggered abilities.
///
/// All methods are pure: (GameState, args) => GameState.
/// The caller is responsible for persisting/distributing the returned state.
/// </summary>
public static class GameEngine
{
    // =========================================================
    // Game setup
    // =========================================================

    /// <summary>
    /// Creates the initial game state from two player decks.
    /// Shuffles libraries, determines first player, draws opening hands.
    /// </summary>
    public static GameState CreateGame(
        Guid player1Id, string player1Name, IReadOnlyList<Card> player1Deck,
        Guid player2Id, string player2Name, IReadOnlyList<Card> player2Deck,
        Guid? firstPlayerId = null,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        firstPlayerId ??= rng.Next(2) == 0 ? player1Id : player2Id;

        var p1 = new PlayerState
        {
            PlayerId = player1Id,
            Name = player1Name,
            Library = player1Deck.ToImmutableList(),
        }.ShuffleLibrary(rng);

        var p2 = new PlayerState
        {
            PlayerId = player2Id,
            Name = player2Name,
            Library = player2Deck.ToImmutableList(),
        }.ShuffleLibrary(rng);

        // Draw opening hands (7 cards each)
        for (int i = 0; i < 7; i++)
        {
            p1 = p1.DrawCard();
            p2 = p2.DrawCard();
        }

        // Start directly in pre-combat main so the active player can immediately
        // play lands and spells without having to pass through untap/upkeep/draw.
        return new GameState
        {
            Players = ImmutableList.Create(p1, p2),
            ActivePlayerId = firstPlayerId.Value,
            PriorityPlayerId = firstPlayerId.Value,
            CurrentPhase = Phase.PreCombatMain,
            CurrentStep = Step.Main,
            Turn = 1,
            IsFirstTurn = true,
        };
    }

    // =========================================================
    // Priority actions
    // =========================================================

    /// <summary>
    /// Player plays a land from hand.
    /// </summary>
    public static GameState PlayLand(GameState state, Guid playerId, Guid cardId)
    {
        state = ZoneManager.PlayLand(state, playerId, cardId);
        return RunSBAs(state);
    }

    /// <summary>
    /// Player casts a spell from hand, optionally declaring targets.
    /// </summary>
    public static GameState CastSpell(GameState state, Guid playerId, Guid cardId, IReadOnlyList<Target>? targets = null)
    {
        state = ZoneManager.CastSpell(state, playerId, cardId, targets);
        return RunSBAs(state);
    }

    /// <summary>
    /// Player taps a land or permanent for mana.
    /// </summary>
    public static GameState ActivateMana(GameState state, Guid playerId, Guid permanentId)
    {
        state = ZoneManager.TapLandForMana(state, playerId, permanentId);
        return state; // Mana abilities don't use the stack, no SBAs needed
    }

    /// <summary>
    /// Untap a land and remove the mana it produced (undo mana activation).
    /// Only legal while the mana is still floating (unspent) in the pool.
    /// </summary>
    public static GameState UntapLand(GameState state, Guid playerId, Guid permanentId)
    {
        return ZoneManager.UntapLand(state, playerId, permanentId);
    }

    /// <summary>
    /// Player passes priority.
    /// If both players pass with an empty stack, the step advances.
    /// If both players pass with a non-empty stack, the top resolves.
    /// </summary>
    public static GameState PassPriority(GameState state, Guid playerId)
    {
        if (state.PriorityPlayerId != playerId)
            throw new InvalidOperationException("You do not have priority.");

        var opponentId = state.OpponentOf(playerId);

        if (state.IsStackEmpty)
        {
            // Both players passed with empty stack -- advance step
            if (TurnStateMachine.IsLastStepOfTurn(state.CurrentPhase, state.CurrentStep))
                state = TurnStateMachine.AdvanceTurn(state);
            else
                state = TurnStateMachine.AdvanceStep(state);

            return RunSBAs(state);
        }
        else
        {
            // Active player passes → give opponent a priority window.
            // Opponent passes back → both have passed, resolve top of stack.
            if (playerId == state.ActivePlayerId)
            {
                return state with { PriorityPlayerId = opponentId };
            }
            else
            {
                state = ZoneManager.ResolveTopOfStack(state);
                state = RunSBAs(state);
                return state with { PriorityPlayerId = state.ActivePlayerId };
            }
        }
    }

    // =========================================================
    // Combat actions
    // =========================================================

    /// <summary>
    /// Active player declares attackers.
    /// </summary>
    public static GameState DeclareAttackers(GameState state, Guid playerId, IReadOnlyList<Guid> attackerIds)
    {
        state = CombatEngine.DeclareAttackers(state, playerId, attackerIds);
        state = RunSBAs(state);
        // After declaring attackers, give defending player priority
        var defendingPlayerId = state.OpponentOf(playerId);
        return state with { PriorityPlayerId = defendingPlayerId };
    }

    /// <summary>
    /// Defending player declares blockers.
    /// </summary>
    public static GameState DeclareBlockers(GameState state, Guid playerId, IReadOnlyDictionary<Guid, Guid> blockerToAttacker)
    {
        state = CombatEngine.DeclareBlockers(state, playerId, blockerToAttacker);
        state = RunSBAs(state);
        // After blockers declared, active player orders blockers then gets priority
        return state with { PriorityPlayerId = state.ActivePlayerId };
    }

    /// <summary>
    /// Active player sets the damage assignment order for an attacker blocked by multiple creatures.
    /// </summary>
    public static GameState SetBlockerOrder(GameState state, Guid playerId, Guid attackerId, IReadOnlyList<Guid> orderedBlockers)
    {
        if (state.ActivePlayerId != playerId)
            throw new InvalidOperationException("Only the active player sets blocker order.");

        return CombatEngine.SetBlockerOrder(state, attackerId, orderedBlockers);
    }

    /// <summary>
    /// Assigns and applies combat damage for the current damage step.
    /// Called automatically when both players pass priority in the damage step.
    /// </summary>
    public static GameState ApplyCombatDamage(GameState state)
    {
        bool isFirstStrike = state.CurrentStep == Step.FirstStrikeDamage;
        state = CombatEngine.AssignCombatDamage(state, isFirstStrike);
        return RunSBAs(state);
    }

    // =========================================================
    // Direct removal (used by spell effects)
    // =========================================================

    public static GameState DestroyPermanent(GameState state, Guid permanentId)
    {
        state = ZoneManager.DestroyPermanent(state, permanentId);
        return RunSBAs(state);
    }

    public static GameState ExilePermanent(GameState state, Guid permanentId)
    {
        state = ZoneManager.ExilePermanent(state, permanentId);
        return RunSBAs(state);
    }

    public static GameState BounceToHand(GameState state, Guid permanentId)
    {
        state = ZoneManager.BounceToHand(state, permanentId);
        return RunSBAs(state);
    }

    // =========================================================
    // SBA loop
    // =========================================================

    /// <summary>
    /// Runs state-based actions to a fixed point. Called after every game action.
    /// </summary>
    private static GameState RunSBAs(GameState state)
    {
        var (next, _) = StateBasedActions.Apply(state);
        return next;
    }

    // =========================================================
    // Helpers
    // =========================================================

    private static Guid GetOpponent(GameState state, Guid playerId) =>
        state.Players.First(p => p.PlayerId != playerId).PlayerId;
}
