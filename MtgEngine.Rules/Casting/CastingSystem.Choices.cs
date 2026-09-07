using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// What a question may offer for the costs that name objects, CR 601.2c. <c>Check</c> refuses such
/// a cost by its name; this half says what it is looking for, so the interpreter can ask instead of
/// refusing and does not have to keep its own copy of which costs consume what.
/// </summary>
[RuleRef("601.2c")]
public sealed partial class CastingSystem
{
    /// <summary>
    /// CR 601.2c: what each cost of this command still needs chosen — the objects that could
    /// legally pay it and how many it takes, in the order the costs are written. The legal objects
    /// and not the filter, because a filter alone cannot be offered from: a sacrifice also wants
    /// the battlefield and the player's control, and a tap cost wants an untapped permanent. It
    /// comes from the one table that knows what a cost consumes (<see cref="ObjectCosts"/>). A
    /// command that cannot be announced at all has nothing to offer, so the answer is an empty
    /// list rather than a refusal — <see cref="Check"/> is what says why.
    /// </summary>
    [RuleRef("601.2c")]
    public ImmutableList<CostChoice> CostChoices(GameState state, Command command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return command switch
            {
                CastSpell cast => OfCast(state, cast),
                ActivateAbility activate => OfActivation(state, activate),
                _ => [],
            };
        }
        catch (RefusedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The costs of the plan this cast would announce, CR 601.2b and 601.2c. The plan is read on
    /// CR 608.2g's instructed road, because what a cost consumes is the same whichever road the
    /// cast takes and neither CR 601.3's permission for the zone nor CR 117.1a's timing is what a
    /// cost question asks about — a card a may-cast names in a graveyard has to be able to say what
    /// its sacrifice wants before the answer that casts it. Nothing is authorised here:
    /// <see cref="Check"/> and <see cref="CheckInstructed"/> are what refuse, each on its own road.
    /// </summary>
    [RuleRef("601.2b")]
    private ImmutableList<CostChoice> OfCast(GameState state, CastSpell cast)
    {
        var card = Castable(state, cast.Card, cast.Player);
        var chars = characteristics.Of(state, card.Id);
        var plan = CastPlan.For(state, card, cast, query, instructed: true);
        return Asked(state, new CostContext(card, chars, cast.Player, cast.X, zones, query), plan.NonMana);
    }

    /// <summary>The ability's own costs, CR 602.2a; an index that names no activated ability asks nothing.</summary>
    [RuleRef("602.2a")]
    private ImmutableList<CostChoice> OfActivation(GameState state, ActivateAbility activate)
    {
        var source = Object(state, activate.Source);
        var chars = characteristics.Of(state, activate.Source);
        return activate.AbilityIndex >= 0
            && activate.AbilityIndex < chars.Abilities.Count
            && chars.Abilities[activate.AbilityIndex] is ActivatedAbility ability
            ? Asked(state, new CostContext(source, chars, activate.Player, activate.X, zones, query), ability.Costs)
            : ImmutableList<CostChoice>.Empty;
    }

    /// <summary>Each cost that names objects, in the order they are written, CR 601.2c.</summary>
    [RuleRef("601.2c")]
    private static ImmutableList<CostChoice> Asked(GameState state, CostContext context, IReadOnlyList<Cost> costs)
    {
        var asked = ImmutableList.CreateBuilder<CostChoice>();
        foreach (var cost in CostPayment.Flatten(costs))
        {
            if (ObjectCosts.Choice(state, context, cost) is { } choice)
                asked.Add(choice);
        }

        return asked.ToImmutable();
    }
}
