using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Excess damage, CR 120.4a. "Excess damage is dealt to that creature's controller instead" is not
/// a second damage instruction the card prints twice: it modifies the one damage event, so the
/// permanent is dealt what would have been lethal and the remainder is dealt where the effect
/// names. The excess is read as the damage is dealt, from the permanent as it stands then — its
/// toughness less the damage already marked on it (CR 120.6) — and the gate the card puts on the
/// redirection ("if it has trample") is read at the same moment, not when the spell was cast.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// How much of this damage is excess, CR 120.4a: nothing at all unless the effect says where
    /// excess goes, and nothing unless the condition the card gates it on holds now (CR 608.2:
    /// the resolution reads its own conditions as it performs the instruction). A permanent with
    /// nothing to read a lethal amount from — neither creature, planeswalker nor battle — has no
    /// excess, so the whole amount stays on it.
    /// </summary>
    [RuleRef("120.4a")]
    private int Excess(EffectRun run, DealDamage damage, ObjectId id, int amount, bool deathtouch)
    {
        if (damage.ExcessTo is null)
            return 0;

        if (damage.ExcessIf is { } gate && !query.Holds(run.State, gate, run.Context))
            return 0;

        return Lethal(run, id, deathtouch) is { } lethal ? Math.Max(0, amount - lethal) : 0;
    }

    /// <summary>
    /// What would be lethal to that permanent, CR 120.4a. A creature reads its toughness less the
    /// damage already marked on it (CR 120.6); a source with deathtouch makes any amount over 1
    /// excess, because one point of its damage is already lethal (CR 702.2c). A planeswalker reads
    /// its loyalty and a battle its defence, both of which the game keeps as counters. A permanent
    /// that is more than one of the three gives the greatest excess, which is the smallest of those
    /// readings. Null is "no reading at all", which is not the same as a lethal amount of zero.
    /// </summary>
    [RuleRef("120.4a")]
    private int? Lethal(EffectRun run, ObjectId id, bool deathtouch)
    {
        if (!run.State.Objects.TryGetValue(id, out var obj))
            return null;

        var what = characteristics.Of(run.State, id);
        var readings = new List<int>();
        if (what.Types.HasFlag(CardType.Creature) && what.Toughness is { } toughness)
            readings.Add(deathtouch ? 1 : Math.Max(0, toughness - obj.Damage));
        if (what.Types.HasFlag(CardType.Planeswalker))
            readings.Add(Math.Max(0, obj.Counters.GetValueOrDefault(Cards.Counters.Loyalty)));
        if (what.Types.HasFlag(CardType.Battle))
            readings.Add(Math.Max(0, obj.Counters.GetValueOrDefault(Cards.Counters.Defense)));

        return readings.Count == 0 ? null : readings.Min();
    }

    /// <summary>
    /// The excess, dealt where the effect names it, CR 120.4a. It is the same source and the same
    /// damage event's other half, so it is offered to the replacement system like any damage
    /// (CR 616) and the player it reaches is the one who could replace it.
    /// </summary>
    [RuleRef("120.4a")]
    private EffectRun Redirected(EffectRun run, DealDamage damage, ObjectId source, int excess, bool commander, bool deathtouch)
    {
        foreach (var player in Players(run, damage.ExcessTo))
        {
            run = Propose(
                run,
                new DamageDealt(source, null, player, excess, Combat: false, commander, deathtouch),
                player);
            if (run.Halted)
                return run;
        }

        return run;
    }
}
