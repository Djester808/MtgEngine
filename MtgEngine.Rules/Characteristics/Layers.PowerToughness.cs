using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// Layer 7, CR 613.1g, and the one place the layer system turns an <see cref="Amount"/> into a
/// number. Two kinds of effect reach it and the rules read their numbers differently: the effect
/// of a resolving spell or ability locked its numbers in as it resolved (CR 611.2c), so the
/// interpreter froze them to <see cref="Fixed"/> and anything else here is a shape the
/// interpreter did not build; a static ability's effect is re-read as the game changes
/// (CR 611.3a) and a characteristic-defining ability's value is read continuously (CR 604.3),
/// so their amounts are evaluated through the query every time the characteristics are read,
/// with the ability's source as the context.
/// </summary>
public sealed partial class LayerSystem
{
    /// <summary>
    /// The query this system evaluates a static ability's amount through. Built on this very
    /// object, because a count of "lands you control" is a count over the characteristics the
    /// layers produce; the recursion that invites is cut by <see cref="_evaluating"/>.
    /// </summary>
    private IQuery? _query;

    private IQuery Query => _query ??= new Query.Query(this);

    /// <summary>
    /// The objects whose layer-7 amounts are being evaluated on this thread. Counting "lands you
    /// control" reads every object's characteristics, and one of them is the object whose count
    /// this is; CR 613.8's dependency rules are not built, so the second visit reads that object
    /// without its own amounts rather than recurring without end.
    /// </summary>
    [ThreadStatic]
    private static HashSet<ObjectId>? _evaluating;

    /// <summary>
    /// Layer 7, CR 613.1g, in the sublayers of CR 613.4: 7a characteristic-defining power and
    /// toughness, 7b effects that set them, 7c effects and counters that modify them, 7d
    /// effects that switch them.
    /// </summary>
    [RuleRef("613.4")]
    private void Layer7PowerAndToughness(GameState state, GameObject obj, Draft draft, ImmutableList<Layered> effects)
    {
        var evaluating = _evaluating ??= [];
        var outermost = evaluating.Add(obj.Id);
        try
        {
            // 7a, CR 613.4a with 604.3: a characteristic-defining ability that defines power and
            // toughness, its value read now rather than when the ability began.
            foreach (var layered in effects.Where(l => l.CharacteristicDefining))
            {
                if (layered.Effect is SetPt cda && Value(state, obj, layered, cda.Power, "a characteristic-defining power", outermost) is { } power
                    && Value(state, obj, layered, cda.Toughness, "a characteristic-defining toughness", outermost) is { } toughness)
                {
                    draft.Power = power;
                    draft.Toughness = toughness;
                }
            }

            // 7b, CR 613.4b: effects that set power and toughness to a specific number.
            foreach (var layered in effects.Where(l => !l.CharacteristicDefining))
            {
                if (layered.Effect is SetPt set && Value(state, obj, layered, set.Power, "a power-setting effect", outermost) is { } power
                    && Value(state, obj, layered, set.Toughness, "a toughness-setting effect", outermost) is { } toughness)
                {
                    draft.Power = power;
                    draft.Toughness = toughness;
                }
            }

            // 7c, CR 613.4c: effects and counters that modify power and toughness. Addition
            // commutes, so the timestamp order the list is already in changes nothing here.
            var powerDelta = 0;
            var toughnessDelta = 0;
            foreach (var layered in effects)
            {
                if (layered.Effect is ModifyPt modify)
                {
                    powerDelta += Value(state, obj, layered, modify.Power, "a power-modifying effect", outermost) ?? 0;
                    toughnessDelta += Value(state, obj, layered, modify.Toughness, "a toughness-modifying effect", outermost) ?? 0;
                }
            }

            foreach (var counter in obj.Counters)
            {
                var delta = PowerToughnessCounter(counter.Key);
                if (counter.Value != 0 && delta is not null)
                {
                    powerDelta += delta.Value.Power * counter.Value;
                    toughnessDelta += delta.Value.Toughness * counter.Value;
                }
            }

            if (draft.Power is not null)
                draft.Power += powerDelta;
            if (draft.Toughness is not null)
                draft.Toughness += toughnessDelta;

            // 7d, CR 613.4d: switches. Two switches cancel each other out.
            if (effects.Count(l => l.Effect is SwitchPt) % 2 == 1)
                (draft.Power, draft.Toughness) = (draft.Toughness, draft.Power);
        }
        finally
        {
            if (outermost)
                evaluating.Remove(obj.Id);
        }
    }

    /// <summary>
    /// What one amount of a layer-7 effect is worth now. A literal is itself. A resolved effect's
    /// amount was fixed as it resolved (CR 611.2c), so any other shape there is one the interpreter
    /// did not build and is refused with the rule. A static ability's amount is evaluated through
    /// the query with the ability's source and controller as its context (CR 611.3a, 604.3); null
    /// is the re-entrant visit, where the object is being read to count itself and its own amounts
    /// are left out.
    /// </summary>
    [RuleRef("611.2c")]
    private int? Value(GameState state, GameObject obj, Layered layered, Amount amount, string what, bool outermost)
    {
        if (amount is Fixed fixedAmount)
            return fixedAmount.N;

        if (!layered.FromStatic)
        {
            throw new RefusedException(
                $"{what} carried the amount {amount.GetType().Name}; a resolved effect's numbers are fixed as it resolves (CR 611.2c), so the interpreter must evaluate it before the effect is created.",
                "611.2c");
        }

        if (!outermost)
            return null;

        return Query.Evaluate(state, amount, ContextOf(state, layered.Source ?? obj.Id, layered.Controller));
    }

    /// <summary>"+1/+1", "-1/-1", "+2/+0" and the rest: what one counter of that kind modifies.</summary>
    [RuleRef("613.4c")]
    private static (int Power, int Toughness)? PowerToughnessCounter(string counter)
    {
        var parts = counter.Split('/');
        if (parts.Length != 2)
            return null;
        if (!int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var power))
            return null;
        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var toughness))
            return null;
        return (power, toughness);
    }
}
