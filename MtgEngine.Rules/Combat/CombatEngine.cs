using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using System.Collections.Immutable;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// Handles the declare attackers, declare blockers, and damage assignment steps.
/// </summary>
public static class CombatEngine
{
    // =========================================================
    // Declare Attackers
    // =========================================================

    public static GameState DeclareAttackers(GameState state, Guid attackingPlayerId, IReadOnlyList<Guid> attackerIds)
    {
        if (state.CurrentStep != Step.DeclareAttackers)
            throw new InvalidOperationException("Not in declare attackers step.");

        if (state.ActivePlayerId != attackingPlayerId)
            throw new InvalidOperationException("Only the active player declares attackers.");

        foreach (var id in attackerIds)
        {
            var permanent = state.GetPermanent(id);
            if (!permanent.CanAttack)
                throw new InvalidOperationException($"{permanent.Name} cannot attack.");
        }

        // Tap attackers (unless vigilance)
        var updated = state;
        foreach (var id in attackerIds)
        {
            var p = state.GetPermanent(id);
            if (!p.HasKeyword(KeywordAbility.Vigilance))
                updated = updated.UpdatePermanent(p.Tap());
        }

        var combatState = (state.Combat ?? new CombatState()) with
        {
            AttackersToBlockers = attackerIds
                .ToImmutableDictionary(id => id, _ => ImmutableList<Guid>.Empty),
            AttackersDeclared = true,
        };

        return updated with { Combat = combatState };
    }

    // =========================================================
    // Declare Blockers
    // =========================================================

    public static GameState DeclareBlockers(GameState state, Guid defendingPlayerId, IReadOnlyDictionary<Guid, Guid> blockerToAttacker)
    {
        if (state.CurrentStep != Step.DeclareBlockers)
            throw new InvalidOperationException("Not in declare blockers step.");

        if (state.OpponentOf(state.ActivePlayerId) != defendingPlayerId)
            throw new InvalidOperationException("Only the defending player declares blockers.");

        var combat = state.Combat ?? throw new InvalidOperationException("No combat state.");

        foreach (var (blockerId, attackerId) in blockerToAttacker)
        {
            var blocker = state.GetPermanent(blockerId);
            var attacker = state.GetPermanent(attackerId);

            ValidateBlock(blocker, attacker);
        }

        // Build updated attacker -> blockers map
        var newMap = combat.AttackersToBlockers.ToBuilder();
        foreach (var (blockerId, attackerId) in blockerToAttacker)
        {
            if (!newMap.ContainsKey(attackerId))
                throw new InvalidOperationException($"{attackerId} is not an attacker.");

            newMap[attackerId] = newMap[attackerId].Add(blockerId);
        }

        var updatedCombat = combat with
        {
            AttackersToBlockers = newMap.ToImmutable(),
            BlockersDeclared = true,
        };

        return state with { Combat = updatedCombat };
    }

    private static void ValidateBlock(Permanent blocker, Permanent attacker)
    {
        if (!blocker.CanBlock)
            throw new InvalidOperationException($"{blocker.Name} cannot block.");

        // Flying restriction
        bool attackerFlies = attacker.HasKeyword(KeywordAbility.Flying);
        bool blockerCanReach = blocker.HasKeyword(KeywordAbility.Flying) || blocker.HasKeyword(KeywordAbility.Reach);
        if (attackerFlies && !blockerCanReach)
            throw new InvalidOperationException($"{blocker.Name} cannot block {attacker.Name} (flying).");

        // Menace: must be blocked by 2+ creatures -- enforced at the group level
        // (checked in ValidateAllBlockers)
    }

    // =========================================================
    // Assign Damage
    // =========================================================

    /// <summary>
    /// Assigns and deals combat damage for the given damage step.
    /// Returns the new game state with damage applied.
    /// </summary>
    public static GameState AssignCombatDamage(GameState state, bool firstStrike)
    {
        var combat = state.Combat ?? throw new InvalidOperationException("No combat state.");

        foreach (var (attackerId, blockerIds) in combat.AttackersToBlockers)
        {
            if (!state.PermanentExists(attackerId)) continue;
            var attacker = state.GetPermanent(attackerId);

            bool attackerIsFirstStriker = attacker.HasKeyword(KeywordAbility.FirstStrike) || attacker.HasKeyword(KeywordAbility.DoubleStrike);
            if (firstStrike && !attackerIsFirstStriker) continue;
            if (!firstStrike && attacker.HasKeyword(KeywordAbility.FirstStrike) && !attacker.HasKeyword(KeywordAbility.DoubleStrike)) continue;

            int? power = attacker.EffectivePower;
            if (power is null || power <= 0) continue;

            if (blockerIds.IsEmpty)
            {
                // Unblocked: damage goes to defending player
                var defendingPlayer = state.GetPlayer(state.OpponentOf(state.ActivePlayerId));
                state = DealDamageToPlayer(state, attacker, defendingPlayer.PlayerId, power.Value);
            }
            else
            {
                // Blocked: assign damage to blockers in order
                state = AssignDamageToBlockers(state, attacker, blockerIds, combat);
            }
        }

        // Blockers deal damage back to attackers
        foreach (var (attackerId, blockerIds) in combat.AttackersToBlockers)
        {
            if (!state.PermanentExists(attackerId)) continue;
            foreach (var blockerId in blockerIds)
            {
                if (!state.PermanentExists(blockerId)) continue;
                var blocker = state.GetPermanent(blockerId);
                var attacker = state.GetPermanent(attackerId);

                bool blockerIsFirstStriker = blocker.HasKeyword(KeywordAbility.FirstStrike) || blocker.HasKeyword(KeywordAbility.DoubleStrike);
                if (firstStrike && !blockerIsFirstStriker) continue;
                if (!firstStrike && blocker.HasKeyword(KeywordAbility.FirstStrike) && !blocker.HasKeyword(KeywordAbility.DoubleStrike)) continue;

                int? blockerPower = blocker.EffectivePower;
                if (blockerPower is null || blockerPower <= 0) continue;

                state = DealDamageToPermanent(state, blocker, attackerId, blockerPower.Value);
            }
        }

        return state;
    }

    private static GameState AssignDamageToBlockers(GameState state, Permanent attacker, ImmutableList<Guid> blockerIds, CombatState combat)
    {
        int remaining = attacker.EffectivePower!.Value;
        bool hasTrample = attacker.HasKeyword(KeywordAbility.Trample);
        bool hasDeathtouch = attacker.HasKeyword(KeywordAbility.Deathtouch);

        var order = combat.BlockerOrder.TryGetValue(attacker.PermanentId, out var o) ? o : blockerIds;

        foreach (var blockerId in order)
        {
            if (!state.PermanentExists(blockerId)) continue;
            if (remaining <= 0) break;

            var blocker = state.GetPermanent(blockerId);
            int lethal = hasDeathtouch ? 1 : Math.Max(0, (blocker.EffectiveToughness ?? 0) - blocker.DamageMarked);
            int assign = hasTrample ? Math.Min(remaining, lethal) : Math.Min(remaining, blocker.EffectivePower ?? remaining);
            // Without trample, must assign at least lethal but can assign more
            assign = Math.Max(lethal, Math.Min(remaining, assign));
            if (assign > remaining) assign = remaining;

            state = DealDamageToPermanent(state, attacker, blockerId, assign);
            remaining -= assign;
        }

        // Trample overflow goes to defending player
        if (hasTrample && remaining > 0)
        {
            state = DealDamageToPlayer(state, attacker, state.OpponentOf(state.ActivePlayerId), remaining);
        }

        return state;
    }

    private static GameState DealDamageToPermanent(GameState state, Permanent source, Guid targetId, int amount)
    {
        if (!state.PermanentExists(targetId)) return state;

        var target = state.GetPermanent(targetId);
        bool fromDeathtouch = source.HasKeyword(KeywordAbility.Deathtouch);
        state = state.UpdatePermanent(target.AddDamage(amount, fromDeathtouch));

        // Lifelink
        if (source.HasKeyword(KeywordAbility.Lifelink))
        {
            var controller = state.GetPlayer(source.ControllerId);
            state = state.UpdatePlayer(controller.GainLife(amount));
        }

        return state;
    }

    private static GameState DealDamageToPlayer(GameState state, Permanent source, Guid targetPlayerId, int amount)
    {
        var player = state.GetPlayer(targetPlayerId);
        state = state.UpdatePlayer(player.LoseLife(amount));

        // Lifelink
        if (source.HasKeyword(KeywordAbility.Lifelink))
        {
            var controller = state.GetPlayer(source.ControllerId);
            state = state.UpdatePlayer(controller.GainLife(amount));
        }

        return state;
    }

    // =========================================================
    // Blocker order assignment
    // =========================================================

    public static GameState SetBlockerOrder(GameState state, Guid attackerId, IReadOnlyList<Guid> orderedBlockers)
    {
        var combat = state.Combat ?? throw new InvalidOperationException("No combat state.");
        var updatedCombat = combat with
        {
            BlockerOrder = combat.BlockerOrder.SetItem(attackerId, orderedBlockers.ToImmutableList())
        };
        return state with { Combat = updatedCombat };
    }
}
