using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// What the rules themselves give a permanent as it enters the battlefield, CR 306.5b: a
/// planeswalker enters with a number of loyalty counters equal to its printed loyalty. This is not
/// a card's replacement effect and no card asks for it — it is part of what entering means — so it
/// belongs on the object the zone system proposes, before that proposal is offered to the
/// replacement effects in force (CR 614.1c, 614.12). Putting it there is what makes "enters with
/// twice that many counters" double a printed number rather than double nothing, and it is the
/// only writer of <see cref="Counters.Loyalty"/> in the engine; the state-based action in
/// CR 704.5i is its only reader.
/// </summary>
[RuleRef("306.5b")]
public sealed partial class ZoneSystem
{
    /// <summary>
    /// The object as it actually enters. Anything the rules put on a permanent as it arrives goes
    /// here; a move to any other zone hands the object back untouched, because loyalty is a
    /// characteristic of the permanent and a planeswalker card in a hand, a library or a graveyard
    /// has no counters at all (CR 306.5a, CR 400.7).
    /// </summary>
    [RuleRef("306.5b")]
    private static GameObject Entering(GameObject arriving)
    {
        if (arriving.Zone.Zone != Cards.Zone.Battlefield)
            return arriving;

        // CR 109.3: the printed loyalty is the face's, so a modal or transforming card that is not
        // showing its planeswalker face brings no loyalty with it.
        var face = arriving.Face;
        if (!face.Types.Types.HasFlag(CardType.Planeswalker) || face.Loyalty is not { } printed)
            return arriving;

        // CR 306.5c: a planeswalker whose printed loyalty is a variable enters with that many
        // counters, and the value is the one chosen as the spell was cast. Nothing carries that
        // choice to the zone change yet, so the shape is refused with its rule rather than guessed
        // at a number — a wrong loyalty is a wrong game, and a crash is never acceptable.
        if (!int.TryParse(printed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var loyalty))
        {
            throw new RefusedException(
                $"A planeswalker whose printed loyalty is \"{printed}\" enters with the value chosen as it was cast, which is not carried to the battlefield yet.",
                "306.5c");
        }

        // CR 122.1a: a permanent never has a counter of zero on it, so a planeswalker printed with
        // 0 arrives with none — and CR 704.5i then puts it into its owner's graveyard, which is
        // what the printed number says should happen.
        return loyalty <= 0
            ? arriving
            : arriving with { Counters = arriving.Counters.SetItem(Counters.Loyalty, loyalty) };
    }
}
