using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>What the cast itself needs before the effect is read: an Aura's host, and the body a printed cost eats.</summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 704.5m: an Aura on the battlefield attached to nothing is put into its owner's
    /// graveyard, so an Aura cast at nothing never verifies. "Enchant X" is the targeting
    /// restriction (CR 702.5b), and it is in the definition as a keyword ability with a filter —
    /// so the board places what the filter describes and the spell is cast at it. A filter naming
    /// something the board has no card for gets no scenario, rather than one that always fails.
    /// </summary>
    [RuleRef("704.5m")]
    private static bool Enchant(SmokeBoard board, Face face)
    {
        var enchant = face.AbilityList.OfType<KeywordAbility>().FirstOrDefault(k => k.Keyword == KeywordKind.Enchant);
        if (enchant is null)
            return true;
        if (enchant.Of is not { } filter || filter.In is not (null or Zone.Battlefield))
            return false;

        // "Enchant permanent" is the whole filter, and a creature is a permanent (CR 110.1).
        var anyPermanent = filter == new ObjectFilter(In: filter.In, Permanent: true, ControlledBy: filter.ControlledBy);
        if (!anyPermanent && !Simple(filter))
            return false;

        var card = anyPermanent ? ScenarioFiller.Creature : filter.Types switch
        {
            null or CardType.Creature => ScenarioFiller.Creature,
            CardType.Land => ScenarioFiller.Land,
            _ => null,
        };
        if (card is null)
            return false;

        board.Enchants(filter.ControlledBy is null or You, card);
        return true;
    }

    /// <summary>
    /// CR 601.2c: an additional cost that eats an object is chosen as the spell is cast, and the
    /// engine refuses a cast that names nothing for it ("There are not enough permanents to
    /// sacrifice", CR 601.2h). So the board places the body the cost will eat and the step names
    /// it. A cost the board has no card for gets no scenario, rather than one that reports the
    /// generator's own gap as the card failing.
    /// </summary>
    /// <returns>False when a printed cost cannot be paid from what the board can place.</returns>
    [RuleRef("601.2c")]
    private static bool Costs(SmokeBoard board, Face face)
    {
        foreach (var cost in face.AdditionalCosts ?? [])
        {
            switch (cost)
            {
                // CR 701.10: the cost sacrifices one permanent of the caster's, of whatever kind it
                // names — a creature, a land ("as an additional cost, sacrifice a land"), an
                // artifact. The board places whichever filler the cost's own filter admits.
                case SacrificeCost { Count: null or Fixed { N: 1 } } sacrifice
                    when sacrifice.What.ControlledBy is null or You && SmokeFillers.Bodies(sacrifice.What) is [var eaten, ..]:
                    board.Sacrificed(eaten);
                    break;

                // CR 701.8: the cost discards one card, any card, and the hand is filler.
                case DiscardCost { Count: null or Fixed { N: 1 }, What: null, Random: false }:
                    board.Discarded();
                    break;

                // A cost that eats an object the board cannot place is the CR 601.2h refusal
                // itself: no body, no cast. The card gets no scenario rather than one that
                // reports the generator's gap as the card failing.
                case SacrificeCost or DiscardCost:
                    return false;

                // Everything else — life, mana, counters on the spell's own source — is paid
                // without naming an object, so the engine settles it as it did before there was
                // anywhere to name one.
                default:
                    break;
            }
        }

        return true;
    }
}
