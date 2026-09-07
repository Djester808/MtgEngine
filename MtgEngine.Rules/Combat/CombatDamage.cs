using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
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
        return Deal(state, Batch(state, firstStrike), firstStrike, 0);
    }

    /// <summary>
    /// CR 510.2 and 616.1 — the rest of a batch a replacement-order question stopped. The batch is
    /// a pure function of the board, so it is recomputed rather than kept across the halt (CR 101.4:
    /// nothing is stored between the question and its answer), and the assignments up to and
    /// including <paramref name="from"/> are skipped: the ones before it were dealt when the step
    /// ran, and the one at <paramref name="from"/> is what
    /// <see cref="IReplacementSystem.Resume"/> has just finished.
    /// </summary>
    [RuleRef("510.2")]
    public Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Deal(state, Batch(state, firstStrike), firstStrike, Math.Max(0, from + 1));
    }

    /// <summary>
    /// CR 510.1 and 510.2 — every hit of this step, in the order it is offered. Attackers first, in
    /// object order, then blockers by the attacker they blocked; each hit carries what its source
    /// was while assigning it, because CR 510.2 computes the whole batch against the board the step
    /// began on and the source may be dead by the time the hit lands.
    /// </summary>
    [RuleRef("510.1")]
    private List<Hit> Batch(GameState state, bool firstStrike)
    {
        var combat = state.Combat;
        var batch = new List<Hit>();
        if (combat is null || !combat.AttackersDeclared)
            return batch;

        // Damage assigned to each creature so far in this step, so lethal is judged against it, CR 702.19b.
        var assigned = new Dictionary<ObjectId, int>();

        foreach (var attackerId in combat.Attackers.Keys.OrderBy(id => id.Value))
        {
            if (Fights(state, attackerId, firstStrike) is { } attacker)
                batch.AddRange(AttackerDamage(state, combat, attackerId, attacker, assigned).Select(d => new Hit(attackerId, attacker, d)));
        }

        foreach (var pair in combat.Blockers.OrderBy(p => p.Key.Value))
        {
            foreach (var blockerId in pair.Value)
            {
                if (Fights(state, blockerId, firstStrike) is { } blocker)
                    batch.AddRange(BlockerDamage(state, pair.Key, blockerId, blocker).Select(d => new Hit(blockerId, blocker, d)));
            }
        }

        return batch;
    }

    /// <summary>One hit of the batch: what it would deal, and what its source was while assigning it.</summary>
    private sealed record Hit(ObjectId Source, Characteristics Characteristics, DamageDealt Damage);

    /// <summary>
    /// The batch, dealt. CR 510.2 deals it simultaneously, so every assignment above was computed
    /// against the state as the step began; here each event is offered to the replacement and
    /// prevention effects in force (CR 614.1a) and what comes back is folded before the next is
    /// offered — CR 616.1 read per event, not per batch, because each effect is offered one event.
    /// <para>
    /// CR 616.1 also stops the game when the affected player must say which of several effects
    /// applies first. The question stands and nothing else happens until it is answered (CR 101.4),
    /// so the halt marks the question with this step and the index of the hit it stopped on and
    /// <see cref="ResumeCombatDamage"/> deals the rest once the answer has finished that one event.
    /// </para>
    /// </summary>
    [RuleRef("510.2")]
    private Outcome Deal(GameState state, IReadOnlyList<Hit> batch, bool firstStrike, int from)
    {
        var working = state;
        var events = ImmutableList<GameEvent>.Empty;

        for (var index = from; index < batch.Count; index++)
        {
            var hit = batch[index];
            var happened = Offer(working, hit.Damage, Affected(state, hit.Damage));
            working = happened.State;
            events = events.AddRange(happened.Events);
            if (working.Pending is not null)
                return Halted(working, events, firstStrike, index);

            // CR 702.15b — lifelink gains its controller as much life as the source actually dealt,
            // so a hit a prevention shield stopped gains nothing and a doubled hit gains double. It
            // follows each hit rather than each creature, because a creature that hits twice (lethal
            // to a blocker and the rest elsewhere, CR 510.1c and 702.2b) may be stopped between the
            // two, and the gain has to belong to the hit it came from for the resumption to be exact.
            var dealt = happened.Events.OfType<DamageDealt>().Where(d => d.Source == hit.Source).Sum(d => d.Amount);
            if (dealt <= 0 || !hit.Characteristics.Has(KeywordKind.Lifelink))
                continue;

            var gain = new LifeChanged(hit.Characteristics.Controller, dealt, "lifelink");
            var gained = Offer(working, gain, hit.Characteristics.Controller);
            working = gained.State;
            events = events.AddRange(gained.Events);

            // A halt on the life gain marks the same index: the gain is the event the replacement
            // system resumes, and the hit that earned it has already been dealt.
            if (working.Pending is not null)
                return Halted(working, events, firstStrike, index);
        }

        return new Outcome(working, events);
    }

    /// <summary>
    /// CR 616.1 with 101.4 — the question the batch stopped on, told where it stopped. The marks go
    /// on the pending choice and on the <see cref="ChoiceRequested"/> that raised it, so a game
    /// rebuilt by replaying its log stops on the same marked question: nothing is held anywhere but
    /// the state, and resumption is a re-run of this step and not a captured continuation.
    /// </summary>
    [RuleRef("616.1")]
    private static Outcome Halted(GameState state, ImmutableList<GameEvent> events, bool firstStrike, int index)
    {
        if (state.Pending is not { } pending)
            return new Outcome(state, events);

        var marked = pending with
        {
            Context = pending.Context with
            {
                Extra = pending.Context.Extra
                    .SetItem(ChoiceKinds.CombatDamageStep, firstStrike ? ChoiceKinds.FirstStrikeDamage : ChoiceKinds.RegularDamage)
                    .SetItem(ChoiceKinds.CombatDamageIndex, index.ToString(CultureInfo.InvariantCulture)),
            },
        };

        var requested = new ChoiceRequested(marked);
        var at = events.FindLastIndex(e => e is ChoiceRequested asked && asked.Choice.Id == pending.Id);
        return new Outcome(requested.Apply(state), at < 0 ? events.Add(requested) : events.SetItem(at, requested));
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
