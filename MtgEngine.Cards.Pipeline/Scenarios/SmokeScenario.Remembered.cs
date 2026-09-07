namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The roads that read a step back: a card a step remembered under a key is what a later step
/// moves or measures. The board keeps the alias and the filler under the key, so the expectation
/// can name the very card rather than settle for a count.
/// </summary>
/// <remarks>
/// A reveal of the top X with a choice among the revealed cards (<see cref="Reveal"/> of a
/// <see cref="Top"/>, then a <see cref="Move"/> of a <see cref="Choose"/> among the remembered set)
/// was staged here in wave 17 and played: the engine put the chosen card into the graveyard with
/// the rest, so the road is withheld until the engine reads a permanent card in a library
/// (seam-requests/P19.md). The board's <see cref="SmokeBoard.Reveals"/> and
/// <see cref="SmokeFillers.Unfindable"/> are the half that is ready for it.
/// </remarks>
public static partial class SmokeScenario
{
    /// <summary>The effects that read a remembered step, or nothing when the record says something the board cannot stage.</summary>
    private static bool Remembers(SmokeBoard board, Effect effect)
    {
        switch (effect)
        {
            // CR 404.1 with CR 110.1: "put target creature card from a graveyard onto the
            // battlefield under your control" — the graveyard is stocked with the creature, the
            // spell is aimed at it, and it is remembered so "that card's mana value" can be read.
            case Move { What: Target dug, To: Zone.Battlefield, Where: Placement.Default, UnderControlOf: null or You, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null, RememberAs: var key }
                when dug is { Count: null or Fixed { N: 1 }, UpTo: false } && dug.Filter.OwnedBy is null or You
                    && SmokeFillers.Buried(dug.Filter, board.AnnouncedX) is { } buried:
                {
                    var alias = board.Buries(buried);
                    if (key is not null)
                        board.Remember(key, alias, buried);
                    board.Expect(new InZone(alias, Zone.Battlefield));
                    board.Caster(graveyard: -1);
                    board.Arrives(1);
                }

                break;

            default:
                break;
        }

        return true;
    }
}
