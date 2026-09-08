using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The retarget road: a spell already on the stack, aimed somewhere, and the card under test moving
/// what it does. Nothing about a spell's own resolution is visible from outside it, so the filler on
/// the stack has to be one that changes something a play can count — the bolt, whose damage lands on
/// whichever player the new target names (CR 115.7, CR 119.3).
/// </summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 115.7 with CR 115.7a: the opponent's bolt is announced at the opponent themselves, the card
    /// under test targets that bolt, and the answer to "choose new targets" is the runner's first
    /// legal option — the seats are offered in seat order behind the objects, and with no creature,
    /// planeswalker or battle on either battlefield the first option is the caster's own seat
    /// (CR 115.4). So the damage moves from the opponent to the caster, and the caster's life total
    /// is the assertion: without the retarget it is untouched, so the scenario cannot always pass.
    /// </summary>
    /// <returns>
    /// False when the filter names a spell the board cannot put on the stack, or when the filler bolt
    /// is not one the filter admits — the card then gets no scenario rather than one aimed at a spell
    /// whose resolution shows nothing.
    /// </returns>
    [RuleRef("115.7")]
    private static bool Retargets(SmokeBoard board, ChangeTarget change)
    {
        // A keyed target is refused: the engine changes nothing at all when the retarget's own
        // target carries a key, so the play would report the card doing nothing (seam-requests/P23.md
        // § 2, CR 115.7 with CR 608.2h). Until that is answered the keyed shape gets no scenario
        // rather than one that always fails.
        if (change.What is not Target { Count: null or Fixed { N: 1 }, UpTo: false, Key: null } aimed
            || !SmokeFillers.Casts(aimed.Filter, ScenarioFiller.Bolt)
            || BoltDamage() is not { } dealt || dealt <= 0)
        {
            return false;
        }

        // The bolt is aimed at its own controller, so the seat the answer takes is a different one:
        // an answer that named the seat the spell already targets would change nothing at all
        // (CR 115.7b) and the play would assert a life total that was never going to move.
        board.SpellOnTheStack(card: ScenarioFiller.Bolt, aimedAt: [Opponent]);
        board.Lives(true, -dealt);
        return true;
    }

    /// <summary>
    /// CR 119.3: how much the filler bolt deals, read out of its own definition rather than repeated
    /// here — a filler whose damage changed would otherwise take every retarget scenario with it.
    /// </summary>
    [RuleRef("119.3")]
    private static int? BoltDamage() =>
        ScenarioFiller.For(ScenarioFiller.Bolt)?.Faces[0].AbilityList
            .OfType<SpellAbility>()
            .Select(ability => ability.Do)
            .OfType<DealDamage>()
            .Select(damage => damage.N)
            .OfType<Fixed>()
            .Select(number => (int?)number.N)
            .FirstOrDefault();
}
