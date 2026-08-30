using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>What a player's abilities currently are (CR 702.11c, 702.18a).</summary>
/// <remarks>
/// The players' half of <see cref="ComputedCharacteristics"/>, and much smaller than it, because
/// a player has no printed values to start from: nothing here is <em>modified</em>, only granted,
/// so what it carries is a set of ability flags and nothing else.
/// <para>
/// Its existence is the point rather than its size. Until it was here the engine could say that a
/// <em>permanent</em> had hexproof and had no way to say that a player did — so "You have
/// hexproof", "You have shroud" and every card in that family were unreadable, and the targeting
/// rules asked the question about permanents only.
/// </para>
/// </remarks>
public sealed record ComputedPlayerCharacteristics
{
    /// <summary>A player nothing on the board is granting anything.</summary>
    /// <remarks>
    /// Shared rather than allocated per call because the overwhelmingly common answer on any
    /// board is this one, and <see cref="TargetSpec.IsLegal"/> asks it of every player for every
    /// target of every spell.
    /// </remarks>
    public static readonly ComputedPlayerCharacteristics Nothing = new();

    /// <summary>The abilities the board is granting this player right now.</summary>
    public KeywordAbility Keywords { get; init; }

    public bool Has(KeywordAbility keyword) => Keywords.HasFlag(keyword);

    // Protection is deliberately absent, and it is a measurement rather than an oversight.
    // CR 702.16a does put protection on a player, and 14 corpus cards print one — but every
    // printed wording names a quality this engine has no flag for: "everything", a chosen card
    // name, a chosen card type, a named player, a creature type. The flags it has are the five
    // colours and artifacts, and no card gives a player any of those. So a protection arm here
    // would be a rule nothing could ever reach, sitting beside two that fire — which is where
    // the next silent defect goes. It belongs with the flag that makes it reachable.
}

/// <summary>
/// The abilities a player has, gathered from the board every time (CR 604.2).
/// </summary>
/// <remarks>
/// Computed, never stored — the founding rule of this engine's characteristics, applied to the
/// one subject it had never reached. A keyword written onto <see cref="PlayerState"/> would
/// survive its source leaving the battlefield, which is precisely the "the lord died but the
/// bonus stuck" failure that ended the previous engine, one subject over.
/// <para>
/// It gathers the way <c>Characteristics.Candidates</c> does and asks the same two questions of
/// each permanent: what card is it <em>now</em> (CR 707.2a — a permanent that has become a copy
/// of Aegis of the Gods has Aegis's static ability, and a lookup on its printed card reports
/// none), and does that ability reach this player.
/// </para>
/// <para>
/// There are no layers here. CR 613 sequences effects that change objects' characteristics, and
/// a player has none: several sources of the same keyword are redundant (CR 702.11h, 702.18b), so
/// the grants are unioned and no order can change the answer.
/// </para>
/// </remarks>
public static class PlayerCharacteristics
{
    /// <summary>Everything the board is granting this player.</summary>
    public static ComputedPlayerCharacteristics Of(
        GameState state, IAbilitySource abilities, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);

        var granted = KeywordAbility.None;

        // Static abilities of permanents on the battlefield (CR 604.2), which is every source of
        // a player quality the engine can produce today. A resolved spell's "you gain hexproof
        // until end of turn" would be a floating effect and would be gathered here beside them;
        // nothing emits one yet, and this deliberately does not pretend to look for it.
        foreach (var id in state.Battlefield)
        {
            var source = state.GetObject(id);

            var qualities = abilities.PlayerQualitiesOf(
                Characteristics.CardOf(state, abilities, source));

            if (qualities.Count == 0)
                continue;

            // CR 613.1f: a permanent that has lost all its abilities has lost this one, and a
            // player standing behind a Humility'd Aegis of the Gods has no hexproof. Asked here
            // rather than folded into the sweep because it is the expensive question in this
            // method — a full layer computation — and it is asked only of the permanents that
            // actually granted something, which on any real board is none or one. The object
            // path pays for the same check the same way, for the same reason.
            if (Characteristics.Of(state, abilities, source).HasLostAllAbilities)
                continue;

            foreach (var quality in qualities)
            {
                if (quality.Applies(state, abilities, source, playerId))
                    granted |= quality.Grants;
            }
        }

        return granted == KeywordAbility.None
            ? ComputedPlayerCharacteristics.Nothing
            : new ComputedPlayerCharacteristics { Keywords = granted };
    }
}
