using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// The combat damage step, CR 510. Every assignment is computed against the state as the step
/// began and the damage is emitted as one batch, because CR 510.2 deals it simultaneously: a
/// creature that dies to first strike still assigned its damage, and two creatures that kill
/// each other both deal.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// CR 510. With <paramref name="firstStrike"/> only creatures with first strike or double
    /// strike assign damage; without it, everyone who has neither, plus the double strikers
    /// again, CR 510.4.
    /// </summary>
    [RuleRef("510")]
    public Outcome DealCombatDamage(GameState state, bool firstStrike)
    {
        ArgumentNullException.ThrowIfNull(state);
        var combat = state.Combat;
        if (combat is null || !combat.AttackersDeclared)
            return Outcome.Unchanged(state);

        // Damage assigned to each creature so far in this step, so lethal is judged against it, CR 702.19b.
        var assigned = new Dictionary<ObjectId, int>();
        var events = ImmutableList<GameEvent>.Empty;

        foreach (var attackerId in combat.Attackers.Keys.OrderBy(id => id.Value))
        {
            if (Fights(state, attackerId, firstStrike) is { } attacker)
                events = events.AddRange(AttackerDamage(state, combat, attackerId, attacker, assigned));
        }

        foreach (var pair in combat.Blockers.OrderBy(p => p.Key.Value))
        {
            foreach (var blockerId in pair.Value)
            {
                if (Fights(state, blockerId, firstStrike) is { } blocker)
                    events = events.AddRange(BlockerDamage(state, pair.Key, blockerId, blocker));
            }
        }

        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>CR 510.4 — whether this creature assigns damage in this step, and what it is while doing so.</summary>
    [RuleRef("510.4")]
    private Characteristics? Fights(GameState state, ObjectId id, bool firstStrike)
    {
        if (!OnBattlefield(state, id))
            return null;

        var c = _characteristics.Of(state, id);
        if (!c.Types.HasFlag(CardType.Creature))
            return null;

        // CR 702.7b and 702.4b — first strike assigns in the first step, double strike in both.
        var first = c.Has(KeywordKind.FirstStrike);
        var twice = c.Has(KeywordKind.DoubleStrike);
        var fights = firstStrike ? first || twice : twice || !first;
        return fights ? c : null;
    }

    /// <summary>CR 510.1a–c and 702.19b — an attacker's assignment: to the blockers if it is blocked, otherwise to what it attacks.</summary>
    [RuleRef("510.1")]
    private ImmutableList<GameEvent> AttackerDamage(GameState state, CombatState combat, ObjectId attackerId, Characteristics attacker, Dictionary<ObjectId, int> assigned)
    {
        // CR 510.1a — a creature that would assign 0 or less damage assigns none.
        var power = attacker.Power ?? 0;
        if (power <= 0)
            return [];

        var declared = combat.Blockers.TryGetValue(attackerId, out var list) ? list : [];
        var target = combat.Attackers[attackerId];

        // CR 510.1b — an unblocked creature assigns to the player, planeswalker or battle it attacks.
        if (declared.IsEmpty)
            return WithLifelink(state, TargetDamage(state, attackerId, power, target), attacker);

        // CR 510.1c — a blocked creature with nothing currently blocking it assigns no damage.
        var blockers = declared.Where(id => OnBattlefield(state, id)).ToImmutableList();
        if (blockers.IsEmpty)
            return [];

        var events = ImmutableList<GameEvent>.Empty;
        var deathtouch = attacker.Has(KeywordKind.Deathtouch);
        var trample = attacker.Has(KeywordKind.Trample);
        var remaining = power;
        foreach (var blockerId in blockers)
        {
            if (remaining <= 0)
                break;
            var amount = Math.Min(remaining, Lethal(state, blockerId, deathtouch, assigned));
            remaining -= amount;
            assigned[blockerId] = assigned.GetValueOrDefault(blockerId) + amount;
            events = events.Add(Damage(state, attackerId, blockerId, null, amount));
        }

        if (remaining > 0)
        {
            // CR 702.19b — with trample the excess goes to what the creature is attacking; without
            // it, all the damage must be assigned among the blockers, CR 510.1c.
            if (trample)
            {
                events = events.AddRange(TargetDamage(state, attackerId, remaining, target));
            }
            else
            {
                var last = blockers[^1];
                assigned[last] = assigned.GetValueOrDefault(last) + remaining;
                events = events.Add(Damage(state, attackerId, last, null, remaining));
            }
        }

        return WithLifelink(state, events, attacker);
    }

    /// <summary>CR 510.1d — a blocker assigns its damage to the creature it is blocking.</summary>
    [RuleRef("510.1d")]
    private ImmutableList<GameEvent> BlockerDamage(GameState state, ObjectId attackerId, ObjectId blockerId, Characteristics blocker)
    {
        var power = blocker.Power ?? 0;
        if (power <= 0 || !OnBattlefield(state, attackerId))
            return [];

        return WithLifelink(state, [Damage(state, blockerId, attackerId, null, power)], blocker);
    }

    /// <summary>CR 510.1b — damage to what an attacker is attacking, or none when that has gone.</summary>
    [RuleRef("510.1b")]
    private ImmutableList<GameEvent> TargetDamage(GameState state, ObjectId sourceId, int amount, CombatTarget target)
    {
        if (target.Player is { } player)
            return [Damage(state, sourceId, null, player, amount)];
        if (target.Permanent is { } permanent && OnBattlefield(state, permanent))
            return [Damage(state, sourceId, permanent, null, amount)];

        return [];
    }

    /// <summary>
    /// CR 510.1c and 702.19b — how much is lethal to this blocker: its toughness less the damage
    /// already marked on it and the damage assigned to it earlier in this same step, and always
    /// at least 1, or exactly 1 from a source with deathtouch, CR 702.2b.
    /// </summary>
    [RuleRef("702.19b")]
    private int Lethal(GameState state, ObjectId blockerId, bool deathtouch, Dictionary<ObjectId, int> assigned)
    {
        if (deathtouch)
            return 1;

        var toughness = _characteristics.Of(state, blockerId).Toughness ?? 0;
        var marked = state.Objects[blockerId].Damage + assigned.GetValueOrDefault(blockerId);
        return Math.Max(1, toughness - marked);
    }

    /// <summary>CR 702.15b — damage from a source with lifelink gains its controller that much life as well.</summary>
    [RuleRef("702.15b")]
    private static ImmutableList<GameEvent> WithLifelink(GameState state, ImmutableList<GameEvent> damage, Characteristics source)
    {
        if (!source.Has(KeywordKind.Lifelink))
            return damage;

        var total = damage.OfType<DamageDealt>().Sum(d => d.Amount);
        return total <= 0 ? damage : damage.Add(new LifeChanged(source.Controller, total, "lifelink"));
    }

    private DamageDealt Damage(GameState state, ObjectId source, ObjectId? toObject, PlayerId? toPlayer, int amount) =>
        new(
            source,
            toObject,
            toPlayer,
            amount,
            Combat: true,
            FromCommander: toPlayer is not null && IsCommander(state, source),
            // CR 702.2b: the recipient remembers that a deathtouch source dealt it damage, for 704.5h.
            Deathtouch: toObject is not null && _characteristics.Of(state, source).Has(KeywordKind.Deathtouch));

    /// <summary>CR 903.10a — combat damage to a player from a commander is tracked per commander; whether an object is one is the query layer's answer.</summary>
    [RuleRef("903.10a")]
    private bool IsCommander(GameState state, ObjectId id) =>
        _query.Matches(state, new ObjectFilter(IsCommander: true), id, Context(id, _characteristics.Of(state, id).Controller));
}
