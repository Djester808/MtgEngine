using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Who may attack, who may block, and how much damage goes where (CR 508–510).
/// </summary>
/// <remarks>
/// Pure: everything here reads state and reports a verdict or a list of events. The engine
/// applies them. Every characteristic it consults — power, toughness, keywords — is asked for
/// through <see cref="Characteristics"/> and so is the value after continuous effects, not the
/// value printed on the card. A creature given flying this turn can be blocked only by flyers and
/// reach; one that lost it can be blocked by anything.
/// </remarks>
public static class CombatRules
{
    /// <summary>Why a creature cannot be declared as an attacker, or null if it can (CR 508.1a).</summary>
    public static string? CannotAttack(
        GameState state, IAbilitySource abilities, GameObject creature, Guid attackingPlayer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(creature);

        var computed = Characteristics.Of(state, abilities, creature);

        if (creature.Zone != Zone.Battlefield || !computed.IsCreature)
            return "only a creature on the battlefield can attack";

        if (creature.ControllerId != attackingPlayer)
            return "you do not control it";

        if (creature.Permanent?.IsTapped == true)
            return "it is tapped (CR 508.1a)";

        // CR 508.1a: haste, or controlled continuously since the turn began (CR 302.6).
        if (creature.Permanent?.HasSummoningSickness == true
            && !computed.Has(KeywordAbility.Haste))
        {
            return "it has summoning sickness and no haste (CR 302.6)";
        }

        // CR 702.3b.
        if (computed.Has(KeywordAbility.Defender))
            return "it has defender (CR 702.3b)";

        return null;
    }

    /// <summary>
    /// Why a creature cannot be declared as a blocker for the given attacker, or null (CR 509.1a).
    /// </summary>
    public static string? CannotBlock(
        GameState state,
        IAbilitySource abilities,
        GameObject blocker,
        GameObject attacker,
        Guid defendingPlayer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(blocker);
        ArgumentNullException.ThrowIfNull(attacker);

        var blocking = Characteristics.Of(state, abilities, blocker);
        var attacking = Characteristics.Of(state, abilities, attacker);

        if (blocker.Zone != Zone.Battlefield || !blocking.IsCreature)
            return "only a creature on the battlefield can block";

        if (blocker.ControllerId != defendingPlayer)
            return "you do not control it";

        if (blocker.Permanent?.IsTapped == true)
            return "it is tapped (CR 509.1a)";

        // CR 702.9b: flying can be blocked only by creatures with flying or reach. An evasion
        // ability is a restriction on the block, not on the attack (CR 509.1b).
        if (attacking.Has(KeywordAbility.Flying)
            && !blocking.Has(KeywordAbility.Flying)
            && !blocking.Has(KeywordAbility.Reach))
        {
            return "it cannot block a creature with flying (CR 702.9b)";
        }

        return null;
    }

    /// <summary>
    /// Why a whole set of blocks is illegal, or null. Checked across the declaration, because
    /// some restrictions are about how many creatures block one attacker (CR 509.1c).
    /// </summary>
    public static string? IllegalBlockSet(
        GameState state,
        IAbilitySource abilities,
        IReadOnlyDictionary<ObjectId, ImmutableListOfBlockers> blocks)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(blocks);

        foreach (var (attackerId, blockers) in blocks)
        {
            var attacker = Characteristics.Of(state, abilities, state.GetObject(attackerId));

            // CR 702.111b: menace means it can't be blocked except by two or more creatures.
            if (attacker.Has(KeywordAbility.Menace) && blockers.Ids.Count == 1)
                return "a creature with menace cannot be blocked by exactly one creature (CR 702.111b)";
        }

        return null;
    }

    /// <summary>
    /// The damage every attacker and blocker deals, as one batch (CR 510.1, 510.2).
    /// </summary>
    /// <remarks>
    /// Assigned and dealt as one simultaneous event, which is why two creatures that kill each
    /// other both die: neither is destroyed before the other assigns.
    /// <para>
    /// <paramref name="firstStrikeOnly"/> selects the first of the two damage steps first strike
    /// creates (CR 510.4). In that step only first and double strikers assign; in the one after,
    /// everything that has not already assigned does, plus double strikers again.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GameEvent> AssignCombatDamage(
        GameState state,
        IAbilitySource abilities,
        bool firstStrikeOnly,
        IReadOnlyDictionary<ObjectId, Dictionary<ObjectId, int>>? division = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var combat = state.Combat;
        var events = new List<GameEvent>();

        foreach (var (attackerId, defendingPlayer) in combat.Attackers)
        {
            if (!state.TryGetObject(attackerId, out var attacker))
                continue;

            var computed = Characteristics.Of(state, abilities, attacker);
            if (!AssignsThisStep(computed, firstStrikeOnly))
                continue;

            var power = computed.Power ?? 0;
            if (power <= 0)
                continue;

            // CR 509.1h: still blocked even if every blocker has gone, so it deals nothing.
            if (combat.Blocked.Contains(attackerId))
                AssignToBlockers(
                    state, abilities, attackerId, computed, power, defendingPlayer,
                    division?.GetValueOrDefault(attackerId), events);
            else
                events.Add(new PlayerDamaged(defendingPlayer, attackerId, power, IsCombat: true));
        }

        foreach (var (attackerId, blockers) in combat.Blockers)
        {
            foreach (var blockerId in blockers)
            {
                if (!state.TryGetObject(blockerId, out var blocker))
                    continue;

                var computed = Characteristics.Of(state, abilities, blocker);
                if (!AssignsThisStep(computed, firstStrikeOnly))
                    continue;

                var power = computed.Power ?? 0;
                if (power <= 0 || !state.TryGetObject(attackerId, out _))
                    continue;

                // CR 510.1d: a blocker assigns its damage to the creature it is blocking.
                events.Add(new DamageMarked(
                    attackerId, power, computed.Has(KeywordAbility.Deathtouch)));
            }
        }

        return events;
    }

    private static void AssignToBlockers(
        GameState state,
        IAbilitySource abilities,
        ObjectId attackerId,
        ComputedCharacteristics computed,
        int power,
        Guid defendingPlayer,
        IReadOnlyDictionary<ObjectId, int>? chosenDivision,
        List<GameEvent> events)
    {
        var remaining = power;
        var deathtouch = computed.Has(KeywordAbility.Deathtouch);

        // CR 510.1c: divided as the attacker's controller chooses. With more than one blocker
        // the division is asked for; with one there is nothing to divide and all of it goes
        // there.
        if (chosenDivision is not null)
        {
            foreach (var (blockerId, amount) in chosenDivision)
            {
                if (amount <= 0 || !state.TryGetObject(blockerId, out _))
                    continue;

                events.Add(new DamageMarked(blockerId, amount, deathtouch));
                remaining -= amount;
            }
        }
        else
        {
            foreach (var blockerId in state.Combat.BlockersOf(attackerId))
            {
                if (remaining <= 0)
                    break;

                if (!state.TryGetObject(blockerId, out var blocker))
                    continue;

                var lethal = LethalDamage(state, abilities, blocker, deathtouch);
                var assigned = Math.Min(remaining, lethal);
                if (assigned <= 0)
                    continue;

                events.Add(new DamageMarked(blockerId, assigned, deathtouch));
                remaining -= assigned;
            }
        }

        // CR 702.19b: trample assigns whatever is left to the player, once every blocker has
        // lethal damage. Without trample the excess is simply not assigned.
        if (remaining > 0 && computed.Has(KeywordAbility.Trample))
            events.Add(new PlayerDamaged(defendingPlayer, attackerId, remaining, IsCombat: true));
    }

    /// <summary>
    /// How much damage is lethal to a creature right now (CR 510.1c): its toughness less damage
    /// already marked, or 1 if the source has deathtouch (CR 702.2b).
    /// </summary>
    private static int LethalDamage(
        GameState state, IAbilitySource abilities, GameObject creature, bool deathtouch)
    {
        if (deathtouch)
            return 1;

        var toughness = Characteristics.ToughnessOf(state, abilities, creature) ?? 0;
        return Math.Max(0, toughness - (creature.Permanent?.DamageMarked ?? 0));
    }

    /// <summary>Which creatures assign damage in this step (CR 510.4).</summary>
    private static bool AssignsThisStep(ComputedCharacteristics computed, bool firstStrikeOnly)
    {
        var first = computed.Has(KeywordAbility.FirstStrike);
        var doubleStrike = computed.Has(KeywordAbility.DoubleStrike);

        // In the first step, only first and double strikers. In the second, everything else —
        // plus double strikers, which assign in both.
        return firstStrikeOnly ? first || doubleStrike : !first || doubleStrike;
    }

    /// <summary>Whether any creature in combat has first or double strike (CR 510.4).</summary>
    public static bool NeedsFirstStrikeStep(GameState state, IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Combat.Attackers.Keys
            .Concat(state.Combat.Blockers.Values.SelectMany(b => b))
            .Where(id => state.TryGetObject(id, out _))
            .Select(id => Characteristics.Of(state, abilities, state.GetObject(id)))
            .Any(c => c.Has(KeywordAbility.FirstStrike) || c.Has(KeywordAbility.DoubleStrike));
    }
}

/// <summary>A declared block: one attacker and the creatures blocking it, in damage order.</summary>
public sealed record ImmutableListOfBlockers(IReadOnlyList<ObjectId> Ids);
