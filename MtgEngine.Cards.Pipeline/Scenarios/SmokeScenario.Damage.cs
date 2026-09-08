using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The roads whose assertion is a body dying of the damage it was dealt, and the two amounts a
/// board can put a number to that are read off a player rather than off a permanent.
/// </summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 119.3 with CR 704.5g: damage marked on a creature that equals or exceeds its toughness
    /// is lethal, and the state-based action that reads it puts the body in its owner's graveyard.
    /// The filler's printed toughness is the number the damage is measured against — nothing on it
    /// can change that number, so the scenario can say the body dies rather than settle for an
    /// event. Damage the card says cannot be prevented is the same damage here; a board with no
    /// prevention effect on it shows the same graveyard either way.
    /// </summary>
    /// <returns>False when the damage is not lethal to anything the filter admits, so the card is
    /// read by the arms after this one rather than promised a death that will not happen.</returns>
    [RuleRef("704.5g")]
    private static bool Lethal(SmokeBoard board, DealDamage damage, Target hit)
    {
        if (Number(board, damage.N) is not { } marked || marked <= 0)
            return false;
        if (Aimable(hit) is not [var body, ..] bodies)
            return false;
        if (SmokeFillers.ToughnessOf(body) is not { } toughness || toughness <= 0 || marked < toughness)
            return false;

        board.Expect(new InZone(board.AimedAmong(Mine(hit), bodies), Zone.Graveyard, Owner(hit)));
        return true;
    }

    /// <summary>
    /// CR 121.1 with CR 119.4: "target player draws cards equal to half the number of cards in
    /// their library and loses half their life", both rounded up. The board knows both numbers for
    /// the opponent — the library the setup stocked and the twenty they started at (CR 103.3) — so
    /// the play can name the hand, the library and the life rather than assert that something
    /// happened. Only the opponent: the caster's own library holds the cards a search or a reveal
    /// may have put on top, and half of a number another road is still moving is not a number.
    /// </summary>
    /// <returns>False when the amount is one the board cannot halve, or the player is not the one
    /// the step aims at.</returns>
    [RuleRef("121.1")]
    private static bool Drains(SmokeBoard board, Effect effect, string key)
    {
        switch (effect)
        {
            case Draw { N: Half { Value: CardsInZoneOf { Zone: Zone.Library, Matching: null } counted, RoundUp: true } } draw
                when Aimed(counted.Who, key) && draw.Who is TargetPlayer { Key: { } announced } && string.Equals(announced, key, StringComparison.Ordinal):
                {
                    var drawn = Halved(LibrarySize);
                    board.Opponent(hand: drawn, library: -drawn);
                    return true;
                }

            case LoseLife { N: Half { Value: LifeOf life, RoundUp: true } } lost when Aimed(lost.Who, key) && Aimed(life.Who, key):
                board.Lives(false, -Halved(StartingLife));
                return true;

            default:
                return false;
        }
    }

    /// <summary>CR 103.3: each player begins at twenty, and nothing before the play has moved it.</summary>
    [RuleRef("103.3")]
    private const int StartingLife = 20;

    /// <summary>CR 107.1b: "half, rounded up" of a number the board knows.</summary>
    [RuleRef("107.1b")]
    private static int Halved(int n) => (n + 1) / 2;

    /// <summary>
    /// CR 601.2c with CR 608.2h: the player this amount is about is the one the spell announced,
    /// read back under the key the target carried. Anyone else is a player the board is not
    /// standing the opponent in for.
    /// </summary>
    [RuleRef("608.2h")]
    private static bool Aimed(PlayerRef? who, string key) =>
        who is RememberedPlayer remembered && string.Equals(remembered.Key, key, StringComparison.Ordinal);

    /// <summary>
    /// CR 601.2c: the key one targeted player was announced under, when the whole ability is about
    /// that one player and no other. Null when the card names none, or names more than one.
    /// </summary>
    [RuleRef("601.2c")]
    internal static string? OneTargetedPlayer(CardDefinition definition) =>
        Linter.Walk(definition).OfType<TargetPlayer>().ToList() is [{ Filter: { Opponent: null, IsYou: null, Also: null }, UpTo: false, Key: { } key }]
            ? key
            : null;

    /// <summary>
    /// CR 208.2 with CR 121.1: "draw cards equal to the greatest toughness among creatures you
    /// control" reads the printed toughness of the one body the board gives it to look at, the way
    /// the power road reads its power. Both are the same shape and differ only in which stat the
    /// record names, so they are answered by one reading.
    /// </summary>
    /// <returns>The number the draw is for, or null when the amount names a stat, a filter or a
    /// count the board cannot supply.</returns>
    [RuleRef("208.2")]
    private static int? GreatestStat(GreatestAmong greatest)
    {
        if (greatest.Among.ControlledBy is not (null or You) || !SmokeFillers.Bodies(greatest.Among).Contains(ScenarioFiller.Creature))
            return null;
        var stat = greatest.Of switch
        {
            PowerOf { Of: Self } => SmokeFillers.PowerOf(ScenarioFiller.Creature),
            ToughnessOf { Of: Self } => SmokeFillers.ToughnessOf(ScenarioFiller.Creature),
            _ => null,
        };
        return stat is { } number && number > 0 && number <= LibrarySize ? number : null;
    }

    /// <summary>
    /// CR 601.2 with CR 700.1: "put any number of creature cards from your hand onto the
    /// battlefield" over a hand that holds none of them puts none, so the play shows nothing more
    /// than what the rest of the card did. The board stocks every hand with the same filler
    /// (a land), so which cards are there is the setup's to know rather than a guess — and an
    /// "up to" choice with nothing to choose is the one shape whose emptiness is an assertion the
    /// scenario can stand behind.
    /// </summary>
    /// <returns>True when the choice can take nothing, so the effect asserts nothing and the rest
    /// of the card still gets its scenario.</returns>
    [RuleRef("700.1")]
    private static bool ChoosesNothing(Choose chosen)
    {
        if (chosen is not { Chooser: You, UpTo: true, Among: null, Count: null, Filter.In: Zone.Hand } || chosen.Filter.OwnedBy is not (null or You))
            return false;

        // Read against the one card every hand the board deals holds, the way a search's
        // destination is read against the card it found (CR 205.1a).
        var described = chosen.Filter with { In = null, OwnedBy = null };
        return SmokeFillers.Admits(described, ScenarioFiller.Land) is false;
    }
}
