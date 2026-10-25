using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The range half of CR 601.2c: "one or two target creatures" is a spell with a variable number of
/// targets, and the number is announced with the cast. Everything else about targets in this folder
/// is about <em>which</em> objects were named; this file is about <em>how many</em>.
/// </summary>
public sealed partial class CastingSystem
{
    /// <summary>
    /// CR 601.2c: "If the spell has a variable number of targets, the player announces how many
    /// targets they will choose before they announce those targets." <see cref="Target.Min"/> is
    /// the low end of that range and <see cref="Target.Count"/> the high end, so an announcement
    /// naming fewer than the low end is refused here — before CR 601.2a moves anything — rather
    /// than cast and quietly resolved for the targets it did name.
    /// <para>
    /// Only a selector that carries a <c>Min</c> is checked. A target with no range is the shape
    /// every stored card has today and nothing in this folder counted it before, so a spell that
    /// prints no range casts exactly as it always did; reading a bare <c>Count</c> as a required
    /// number here would refuse casts the rest of the engine has always allowed.
    /// </para>
    /// <para>
    /// The slots are walked the way the interpreter fills them
    /// (<c>EffectInterpreter.RememberTargets</c>): the announced list is read in
    /// <see cref="EffectWalk.TargetsIn"/> order, each selector taking as many as its count works
    /// out to now, and the last taking everything left. That is what makes the range announceable
    /// at all — the last selector's size <em>is</em> the number the player announced.
    /// </para>
    /// </summary>
    [RuleRef("601.2c")]
    private void RequireTargetRange(GameState state, CastSpell command, CastPlan plan)
    {
        var announced = command.Targets?.Count ?? 0;
        var context = CostPayment.Context(command.Card, command.Player, command.X);
        foreach (var spell in plan.Printed.AbilityList.OfType<SpellAbility>())
        {
            var selectors = EffectWalk.TargetsIn(spell.Do);
            if (!selectors.OfType<Cards.Target>().Any(t => t.Min is not null))
                continue;

            var at = 0;
            for (var i = 0; i < selectors.Count; i++)
            {
                var last = i == selectors.Count - 1;
                var target = selectors[i] as Cards.Target;

                // CR 601.2b: a count the card does not write is ONE. Only the last selector takes
                // what is left, which is the announcement itself, so its size is not read off
                // Count. Giving a non-last countless selector the whole remainder - which this
                // did, against its own rule here - left the last one with nothing and refused a
                // legal announcement as "needs more targets than were announced".
                var remaining = Math.Max(0, announced - at);
                var wanted = target?.Count is null ? 1 : Math.Max(0, query.Evaluate(state, target.Count, context));
                var slot = last ? remaining : Math.Min(wanted, remaining);
                if (target?.Min is { } min)
                    RequireInRange(state, target, min, slot, context);
                at += last ? slot : wanted;
            }
        }
    }

    /// <summary>
    /// CR 601.2c: the announced number is within the range the spell prints. Fewer than the low end
    /// is not a legal announcement — a spell that may take none says so with <c>UpTo</c>, which the
    /// vocabulary refuses beside a range — and more than the high end targets what the spell never
    /// offered. The refusal names no number so nothing here has to be formatted for a locale.
    /// </summary>
    [RuleRef("601.2c")]
    private void RequireInRange(GameState state, Cards.Target target, Amount min, int slot, EffectContext context)
    {
        var least = Math.Max(0, query.Evaluate(state, min, context));
        var most = target.Count is { } count ? Math.Max(0, query.Evaluate(state, count, context)) : least;
        if (slot < least)
            throw new RefusedException("That spell needs more targets than were announced.", "601.2c");
        if (slot > most)
            throw new RefusedException("That spell takes fewer targets than were announced.", "601.2c");
    }
}
