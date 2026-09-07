namespace MtgEngine.Rules.Query;

/// <summary>
/// The names of the designations the rules give an object or a player. A designation is not a
/// counter and not an ability (CR 701.60a): it is a marker the rules put on an object, so it is
/// stored as a string in <c>GameObject.Designations</c> and <c>PlayerState.Designations</c> and
/// read here. The names live in one place so the effect that puts a designation on and the query
/// that reads it cannot drift apart; extend here, never with an ad-hoc string.
/// </summary>
public static class Designations
{
    /// <summary>CR 701.60a: a suspected creature has menace and can't block.</summary>
    public const string Suspected = "suspected";

    /// <summary>CR 701.54a: the Ring-bearer, one creature its controller chose.</summary>
    public const string RingBearer = "ring-bearer";

    /// <summary>CR 701.64a: a harnessed permanent, harnessed by the player named in the designation.</summary>
    public const string Harnessed = "harnessed";

    /// <summary>CR 702.180: a prepared creature, put on by Prepare and taken off by Unprepare.</summary>
    public const string Prepared = "prepared";

    /// <summary>CR 701.38a: a monstrous creature.</summary>
    public const string Monstrous = "monstrous";

    /// <summary>CR 702.131a: the city's blessing, which a player keeps for the rest of the game.</summary>
    public const string CityBlessing = "city's blessing";

    /// <summary>
    /// CR 701.15a: goading names the player who goaded, because the creature must attack a player
    /// other than that one. So the designation carries the goader's seat and there is one per
    /// goader, not one flag.
    /// </summary>
    public const string GoadedPrefix = "goaded:";

    /// <summary>The designation a creature carries while it is goaded by that player, CR 701.15a.</summary>
    public static string GoadedBy(State.PlayerId player) =>
        GoadedPrefix + player.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
