namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// What a discard leaves a play able to say. A discard the player chooses is answered with the
/// first legal option, so the card that left the hand is known; a discard at random is made by the
/// game (CR 701.9b) and the card that left is not — the counts hold either way, and the card a
/// search had just put in the hand is exactly what the random discard may take back out.
/// </summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 701.8a: how many cards a discard takes — one when the record names no number, and
    /// nothing the board can count when the number is an amount it would have to work out.
    /// </summary>
    [RuleRef("701.8a")]
    private static int? Discarded(Discard discard) => discard.N switch
    {
        null => 1,
        Fixed { N: > 0 and var n } => n,
        _ => null,
    };

    /// <summary>
    /// CR 701.9b: whether this play discards at random, read over every effect before any of them
    /// is asserted — the discard is written after the search that stocked the hand, and what the
    /// search may say about the card it found depends on the discard that follows it.
    /// </summary>
    [RuleRef("701.9b")]
    private static void AtRandom(SmokeBoard board, IEnumerable<Effect> effects)
    {
        foreach (var effect in effects)
        {
            if (effect is Discard { By: DiscardChooser.Random, Who: null or You })
                board.DiscardsAtRandom = true;
        }
    }
}
