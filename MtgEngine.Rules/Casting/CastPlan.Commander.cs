using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

internal sealed partial record CastPlan
{
    /// <summary>CR 903.8 grants a commander owner permission from the command zone, without changing timing.</summary>
    [RuleRef("903.8")]
    private static bool CommanderRoad(GameObject card, PlayerId caster) =>
        card.IsCommander && card.Owner == caster && card.Zone.Zone == Zone.Command;

    private static bool UsesZonePermission(GameState state, GameObject card, PlayerId caster, IQuery query, ICharacteristics characteristics)
    {
        if (!CommanderRoad(card, caster))
            return true;

        // A plain zone permission is redundant for a commander; a cost rider actually used
        // by Alternative still spends its permission, including a once-per-turn free cast.
        var effect = Allowing(state, card, caster, query, characteristics)?.Effect;
        return effect is { WithoutPayingManaCost: true }
            or { PayLifeEqualToManaValueInstead: true }
            or { AdditionalCost: not null };
    }
}
