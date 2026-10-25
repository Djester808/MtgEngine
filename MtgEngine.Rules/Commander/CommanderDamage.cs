using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Commander;

/// <summary>
/// The Commander variant's own losing condition, CR 903.10a: a player dealt 21 or more combat
/// damage by the same commander over the course of the game loses. It is a state-based action
/// (CR 704.6c), so <see cref="Sba.StateBasedActions"/> asks this on every check.
/// <para>
/// The running total lives in <see cref="PlayerState.CommanderDamage"/>, keyed by the
/// commander that dealt it - a <see cref="CommanderIdentity"/>, which survives the zone
/// changes CR 400.7 makes - and only <c>DamageDealt</c> with <c>FromCommander: true</c> ever
/// adds to it. That is the gate on the variant as well: a game with no commanders never
/// populates the dictionary, so no format flag is needed on the state to keep this rule out
/// of a two-player duel.
/// </para>
/// </summary>
[RuleRef("903.10a")]
public static class CommanderDamage
{
    /// <summary>Twenty-one, CR 903.10a. Not derived from anything; the number is the rule.</summary>
    public const int Lethal = 21;

    /// <summary>
    /// The commander that has dealt this player lethal commander damage, or null. When two
    /// have, the first by card and then by seat is returned, so a check is deterministic and
    /// replays the same.
    /// </summary>
    [RuleRef("903.10a")]
    public static CommanderIdentity? LethalSource(PlayerState player)
    {
        ArgumentNullException.ThrowIfNull(player);
        CommanderIdentity? worst = null;
        foreach (var (source, dealt) in player.CommanderDamage)
        {
            if (dealt < Lethal)
                continue;
            if (worst is null || Before(source, worst.Value))
                worst = source;
        }

        return worst;
    }

    private static bool Before(CommanderIdentity a, CommanderIdentity b)
    {
        int byCard = string.CompareOrdinal(a.OracleId, b.OracleId);
        if (byCard != 0)
            return byCard < 0;
        int byOwner = a.Owner.Value.CompareTo(b.Owner.Value);
        return byOwner != 0 ? byOwner < 0 : Nullable.Compare(a.CardInstanceId?.Value, b.CardInstanceId?.Value) < 0;
    }
}
