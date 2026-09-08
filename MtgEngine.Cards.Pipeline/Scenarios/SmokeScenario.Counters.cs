using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The counterspell road: a spell of the opponent's on the stack, the card under test aimed at it,
/// and — the point of the road — a spell whose resolution would have shown itself, so the play can
/// say it never resolved.
/// </summary>
/// <remarks>
/// This road used to stand on the graveyard alone: the filler that does nothing was put on the
/// stack and the scenario asserted it in its owner's graveyard afterwards. That assertion holds
/// over a counter that did nothing at all — a resolving spell reaches the same graveyard under
/// CR 608.2m — so it was a check that always passed, and E9t found two cards badged Verified by it
/// while their own filter matched the empty set (seam-requests/E9t.md). The bolt is what the road
/// stands on now: it is the only filler whose resolution moves a number, and the number not moving
/// is the whole of what "countered" means (CR 701.5 with CR 608.2b).
/// </remarks>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 701.5: the countered spell is put into its owner's graveyard, and what it was going to do
    /// never happens. The bolt is cast by the opponent at the opponent, so the life total the play
    /// asserts is theirs and the caster's own is left to whatever the card under test does to it.
    /// </summary>
    /// <param name="withNothingLeftToPay">
    /// CR 118.6: stock the opponent's pool with exactly the bolt's own cost, so an "unless its
    /// controller pays" is asked of a player who has nothing left and the counter is what the play
    /// shows.
    /// </param>
    /// <returns>
    /// False when the filter describes a spell the board cannot put on the stack, or one that the
    /// bolt does not answer — the card then gets no scenario rather than one whose every
    /// expectation would hold over a counter that reached nothing.
    /// </returns>
    [RuleRef("701.5")]
    private static bool Counters(SmokeBoard board, Target countered, bool withNothingLeftToPay)
    {
        if (countered is not { Count: null or Fixed { N: 1 }, UpTo: false }
            || !SmokeFillers.Casts(countered.Filter, ScenarioFiller.Bolt)
            || BoltDamage() is not > 0)
        {
            return false;
        }

        var spell = board.SpellOnTheStack(withNothingLeftToPay, ScenarioFiller.Bolt, [Opponent]);
        board.Expect(new InZone(spell, Zone.Graveyard, Opponent));

        // CR 119.3: the bolt would have taken life off the player it was aimed at, so the total
        // standing where the game started is what says the counter reached it. Without this the
        // graveyard alone is asserted, and a spell that resolved is in that graveyard too.
        board.LifeHolds(mine: false);
        return true;
    }
}
