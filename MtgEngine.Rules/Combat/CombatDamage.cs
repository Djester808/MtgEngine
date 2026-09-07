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
        var batch = new List<Assignment>();

        foreach (var attackerId in combat.Attackers.Keys.OrderBy(id => id.Value))
        {
            if (Fights(state, attackerId, firstStrike) is { } attacker)
                batch.Add(new Assignment(attackerId, attacker, AttackerDamage(state, combat, attackerId, attacker, assigned)));
        }

        foreach (var pair in combat.Blockers.OrderBy(p => p.Key.Value))
        {
            foreach (var blockerId in pair.Value)
            {
                if (Fights(state, blockerId, firstStrike) is { } blocker)
                    batch.Add(new Assignment(blockerId, blocker, BlockerDamage(state, pair.Key, blockerId, blocker)));
            }
        }

        return Deal(state, batch);
    }

    /// <summary>What one creature assigned in this step, and what it was while assigning it.</summary>
    private sealed record Assignment(ObjectId Source, Characteristics Characteristics, ImmutableList<DamageDealt> Damage);

    /// <summary>
    /// The batch, dealt. CR 510.2 deals it simultaneously, so every assignment above was computed
    /// against the state as the step began; here each event is offered to the replacement and
    /// prevention effects in force (CR 614.1a) and what comes back is folded before the next is
    /// offered — CR 616.1 read per event, not per batch, because each effect is offered one event.
    /// <para>
    /// CR 616.1 also stops the game when the affected player must say which of several effects
    /// applies first. The question stands and the rest of the batch is not dealt: nothing else
    /// happens until it is answered (CR 101.4), and re-entering this step to deal the remainder
    /// needs a resumption the choice system does not have — see <c>seam-requests/E4b.md</c>.
    /// </para>
    /// </summary>
    [RuleRef("510.2")]
    private Outcome Deal(GameState state, IReadOnlyList<Assignment> batch)
    {
        var working = state;
        var events = ImmutableList<GameEvent>.Empty;

        foreach (var assignment in batch)
        {
            var dealt = 0;
            foreach (var proposed in assignment.Damage)
            {
                var happened = Offer(working, proposed, Affected(state, proposed));
                working = happened.State;
                events = events.AddRange(happened.Events);
                dealt += happened.Events.OfType<DamageDealt>().Where(d => d.Source == assignment.Source).Sum(d => d.Amount);
                if (working.Pending is not null)
                    return new Outcome(working, events);
            }

            // CR 702.15b — lifelink gains its controller as much life as the source actually dealt,
            // so a hit a prevention shield stopped gains nothing and a doubled hit gains double.
            if (dealt <= 0 || !assignment.Characteristics.Has(KeywordKind.Lifelink))
                continue;

            var gain = new LifeChanged(assignment.Characteristics.Controller, dealt, "lifelink");
            var gained = Offer(working, gain, assignment.Characteristics.Controller);
            working = gained.State;
            events = events.AddRange(gained.Events);
            if (working.Pending is not null)
                return new Outcome(working, events);
        }

        return new Outcome(working, events);
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
    private ImmutableList<DamageDealt> AttackerDamage(GameState state, CombatState combat, ObjectId attackerId, Characteristics attacker, Dictionary<ObjectId, int> assigned)
    {
        // CR 510.1a — a creature that would assign 0 or less damage assigns none.
        var power = attacker.Power ?? 0;
        if (power <= 0)
            return [];

        var declared = combat.Blockers.TryGetValue(attackerId, out var list) ? list : [];
        var target = combat.Attackers[attackerId];

        // CR 510.1b — an unblocked creature assigns to the player, planeswalker or battle it attacks.
        if (declared.IsEmpty)
            return TargetDamage(state, attackerId, power, target);

        // CR 510.1c — a blocked creature with nothing currently blocking it assigns no damage.
        var blockers = declared.Where(id => OnBattlefield(state, id)).ToImmutableList();
        if (blockers.IsEmpty)
            return [];

        var events = ImmutableList<DamageDealt>.Empty;
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

        return events;
    }

    /// <summary>CR 510.1d — a blocker assigns its damage to the creature it is blocking.</summary>
    [RuleRef("510.1d")]
    private ImmutableList<DamageDealt> BlockerDamage(GameState state, ObjectId attackerId, ObjectId blockerId, Characteristics blocker)
    {
        var power = blocker.Power ?? 0;
        if (power <= 0 || !OnBattlefield(state, attackerId))
            return [];

        return [Damage(state, blockerId, attackerId, null, power)];
    }

    /// <summary>CR 510.1b — damage to what an attacker is attacking, or none when that has gone.</summary>
    [RuleRef("510.1b")]
    private ImmutableList<DamageDealt> TargetDamage(GameState state, ObjectId sourceId, int amount, CombatTarget target)
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
