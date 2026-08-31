using System.Collections.Immutable;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>
/// A ban on gaining life (CR 119.7).
/// </summary>
/// <remarks>
/// Not a replacement effect and deliberately not modelled as one. CR 119.7 says a replacement
/// effect that would replace a life gain event affecting a banned player "won't do anything" —
/// so a ban sits <em>outside</em> the replacement pass rather than competing inside it. Modelled
/// as a candidate there it would be offered to CR 616.1's ordering question, and a prohibition
/// is not something a player chooses the order of.
/// <para>
/// The same record serves both lifetimes, exactly as <see cref="PreventionEffect"/> does:
/// "Players can't gain life this turn" is a one-shot a resolving spell creates and the cleanup
/// step sweeps, while "Players can't gain life" printed on a permanent is a static ability read
/// off the battlefield every time the question is asked. Only <see cref="UntilEndOfTurn"/>
/// differs, and a static one never reaches this list at all.
/// </para>
/// </remarks>
public sealed record LifeGainBan
{
    public required Guid Id { get; init; }

    /// <summary>Whoever created it, which is who "your opponents" is read around.</summary>
    public required Guid ControllerId { get; init; }

    /// <summary>A described set of players — "players", "your opponents".</summary>
    public PlayerScope? Players { get; init; }

    /// <summary>One named player, for a ban that landed on somebody in particular.</summary>
    /// <remarks>
    /// An identity rather than a description for the reason <see cref="PreventionEffect.Source"/>
    /// is one: the player was decided when the ban was made, and re-deriving them from a
    /// description would move the ban when the board moved.
    /// </remarks>
    public Guid? Player { get; init; }

    /// <summary>The turn it ends at the end of, or null for one with no duration.</summary>
    public int? UntilEndOfTurn { get; init; }
}

/// <summary>
/// Damage a permanent's static ability makes unpreventable (CR 615.12).
/// </summary>
/// <remarks>
/// A template rather than a finished <see cref="PreventionEffect"/>, because the two things it
/// can say about the source are questions about the permanent that prints it: Excruciator's
/// "damage that would be dealt by ~" is an identity that is only known once there is an object,
/// and Questing Beast's "creatures you control" is read around that object's controller
/// (CR 613.1b — the controller now, not the one it entered under). Bound to a host at the moment
/// the question is asked, which is what makes the ban stop when the permanent does (CR 611.2c).
/// </remarks>
public sealed record UnpreventableStatic
{
    public required string Id { get; init; }

    /// <summary>Which damage it unshields — all of it, or combat damage only.</summary>
    public DamageKind Kind { get; init; }

    /// <summary>True for "damage that would be dealt by ~": the host is the source.</summary>
    public bool DealtBySource { get; init; }

    /// <summary>A filter the damage's source has to answer — "dealt by creatures".</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Whose sources <see cref="SourceFilter"/> counts, or null for anyone's.</summary>
    public PlayerScope? SourceController { get; init; }
}

/// <summary>
/// A ban on countering a described group of spells (CR 701.6a).
/// </summary>
/// <remarks>
/// The other half of the parameterised-keyword problem, and it needs a different answer from the
/// one <see cref="TargetRestriction"/> gives. There, the parameter describes the <em>source</em>
/// doing the targeting, so it cannot be a characteristic of the thing being protected and has to
/// be a predicate. Here the parameter describes the protected thing itself — "creature spells you
/// control" — and the engine already has a keyword saying exactly what happens to it. So nothing
/// new is needed on the spell at all: what was missing was a way for one permanent to say the
/// keyword about a group, and this is that, read off the battlefield at the moment a counter
/// would happen.
/// <para>
/// A ban rather than a continuous effect for the reason <see cref="StaticBans"/> exists: a spell
/// on the stack is not a permanent, CR 613's layers are about permanents' characteristics, and
/// running the whole layer computation over the stack to set one flag would be a large change to
/// answer a question that is only ever asked at one moment.
/// </para>
/// </remarks>
public sealed record CounterBan
{
    public required string Id { get; init; }

    /// <summary>A filter the spell has to answer — "creature", "instant|sorcery".</summary>
    /// <remarks>Null for "spells can't be countered", which asks nothing of the spell.</remarks>
    public string? SpellFilter { get; init; }

    /// <summary>Whose spells it covers, or null for anybody's.</summary>
    public PlayerScope? Controller { get; init; }
}

/// <summary>
/// A ban on casting or activating anything with the name this permanent chose (CR 201.4).
/// </summary>
/// <remarks>
/// The parameter is not printed on the card at all: Meddling Mage says "spells with the chosen
/// name", and which name that is was decided by a player as the permanent entered
/// (CR 614.12a). So the ban carries no name and the name is read off the <em>host object</em>
/// at the moment the question is asked - which is also what makes it stop when the permanent
/// leaves, and what makes a permanent that entered again choose again.
/// <para>
/// A ban rather than a continuous effect for the reason the rest of <see cref="StaticBans"/> is
/// one: a spell on the stack is not a permanent, and CR 613's layers order objects'
/// characteristics. Nothing here changes a characteristic.
/// </para>
/// </remarks>
public sealed record ChosenNameBan
{
    public required string Id { get; init; }

    /// <summary>
    /// Whose casting it forbids, read around whoever controls the permanent, or null for
    /// everybody's.
    /// </summary>
    /// <remarks>
    /// Nevermore's "spells with the chosen name can't be cast" stops its own controller too and
    /// is the null case; Gideon's Intervention says "your opponents can't cast" and is the
    /// scoped one. Defaulting a missing subject to the controller is the mistake recorded one
    /// file over, so there is no default that means "guess".
    /// </remarks>
    public Abilities.PlayerScope? Who { get; init; }

    /// <summary>Whether mana abilities are exempt (CR 605.1a).</summary>
    /// <remarks>
    /// Pithing Needle prints the exemption and Phyrexian Revoker does not, and the difference is
    /// the whole of what separates the two cards. Read rather than assumed in either direction:
    /// assuming the exemption makes a Revoker that leaves mana rocks alone, and assuming its
    /// absence makes a Needle that turns off a Sol Ring.
    /// </remarks>
    public bool ExceptManaAbilities { get; init; }
}

/// <summary>
/// What a card's static abilities forbid outright (CR 119.7, 615.12).
/// </summary>
/// <remarks>
/// Held apart from <see cref="ContinuousEffectDefinition"/> because a prohibition changes no
/// characteristic and so has no place in CR 613's layers, and apart from
/// <see cref="ReplacementEffectDefinition"/> because it replaces no event: it is a question the
/// engine asks at one particular moment, and the answer comes from whatever is on the
/// battlefield then. One record rather than two interface members, so a card that prints both
/// bans in two lines — Leyline of Punishment does — is answered by one lookup.
/// </remarks>
public sealed record StaticBans
{
    /// <summary>A card that forbids nothing, which is nearly every card.</summary>
    public static StaticBans None { get; } = new();

    /// <summary>Damage this card makes unpreventable while it is on the battlefield.</summary>
    public ImmutableList<UnpreventableStatic> Unpreventable { get; init; } = [];

    /// <summary>Who this card stops from gaining life while it is on the battlefield.</summary>
    public ImmutableList<PlayerScope> NoLifeGain { get; init; } = [];

    /// <summary>Which spells this card stops being countered while it is on the battlefield.</summary>
    public ImmutableList<CounterBan> NoCounter { get; init; } = [];

    /// <summary>Who this card stops casting a spell with the name it chose (CR 201.4).</summary>
    public ImmutableList<ChosenNameBan> NoCastingNamed { get; init; } = [];

    /// <summary>
    /// Whether this card stops activated abilities of sources with the name it chose
    /// (CR 602.5).
    /// </summary>
    public ImmutableList<ChosenNameBan> NoActivatingNamed { get; init; } = [];

    /// <summary>Whether this card forbids nothing at all, asked before anything is built.</summary>
    public bool IsEmpty =>
        Unpreventable.IsEmpty
        && NoLifeGain.IsEmpty
        && NoCounter.IsEmpty
        && NoCastingNamed.IsEmpty
        && NoActivatingNamed.IsEmpty;
}

/// <summary>Whether a prohibition covers a player (CR 119.7).</summary>
public static class Bans
{
    /// <summary>
    /// Every prohibition a permanent on the battlefield is printing right now.
    /// </summary>
    /// <remarks>
    /// CR 613.1f: a permanent that has lost all abilities forbids nothing either, which is the
    /// same exclusion the replacement scan makes and for the same reason. The card asked is the
    /// object's own rather than a computed copy: a prohibition is not a characteristic, so
    /// nothing in CR 613 can move it from one card to another.
    /// <para>
    /// Here rather than on <c>Game</c> because two callers now need it and neither is the other's
    /// business: the life-gain and prevention questions are the engine's, and
    /// <see cref="CannotBeCountered"/> is asked from inside a resolving effect, which holds a
    /// state and an ability source and no game. A second scan written there would be the same
    /// list one repository further out.
    /// </para>
    /// </remarks>
    public static IEnumerable<(GameObject Host, Guid ControllerId, StaticBans Bans)> InPlay(
        GameState state, Abilities.IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);

        foreach (var id in state.Battlefield)
        {
            if (!state.TryGetObject(id, out var host))
                continue;

            var bans = abilities.BansOf(host.Card);
            if (bans.IsEmpty || Characteristics.Of(state, abilities, host).HasLostAllAbilities)
                continue;

            yield return (host, Characteristics.ControllerOf(state, abilities, host), bans);
        }
    }

    /// <summary>Whether this spell on the stack can't be countered (CR 701.6a).</summary>
    /// <remarks>
    /// Both spellings in one place: the keyword a spell says about itself, and the group ban a
    /// permanent says about somebody's spells. The two counter effects each asked the keyword
    /// alone, in a line of their own — which is how "creature spells you control can't be
    /// countered" could be printed on eleven cards and enforced on none of them.
    /// <para>
    /// The keyword is asked of the printed card rather than of computed characteristics, exactly
    /// as it was: CR 613's layers describe permanents, and this matters while the thing is still
    /// a spell on the stack.
    /// </para>
    /// </remarks>
    public static bool CannotBeCountered(
        GameState state, Abilities.IAbilitySource abilities, GameObject spell)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(spell);

        if (abilities.GrantedKeywords(spell.Card)
            .HasFlag(Domain.Enums.KeywordAbility.CantBeCountered))
        {
            return true;
        }

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoCounter)
            {
                if (said.SpellFilter is { } filter
                    && !SearchFilters.Matches(filter, spell.Card))
                {
                    continue;
                }

                // CR 613.1b: "you control" is read around whoever controls the permanent now,
                // which is why the scope is resolved here and not when the card was compiled.
                if (said.Controller is not { } scope
                    || PlayerScopes.Around(scope, state, ban.ControllerId)
                        .Contains(spell.ControllerId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The permanent forbidding this player from casting this card by name, or null (CR 601.3).
    /// </summary>
    /// <remarks>
    /// The name is taken from the host object and the card being cast is compared to it, so a
    /// host that has not been asked yet - <see cref="GameObject.ChosenName"/> null - forbids
    /// nothing. That arm is the one this whole family turns on: read the other way round, a
    /// Meddling Mage entering would stop every spell in the game.
    /// <para>
    /// The comparison is against the printed name and never against a filter id (CR 201.2). A
    /// name is capitalised by definition, and handing one to <c>SearchFilters</c> would ask
    /// whether the card has a <em>subtype</em> spelled that way - which is how a card once
    /// acquired a ban on a creature type called Green.
    /// </para>
    /// </remarks>
    public static GameObject? CastingForbidden(
        GameState state,
        Abilities.IAbilitySource abilities,
        Domain.Models.CardDefinition casting,
        Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(casting);

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoCastingNamed)
            {
                if (!NamesTheSame(ban.Host.ChosenName, casting.Name))
                    continue;

                // CR 613.1b: "your opponents" is read around whoever controls the permanent
                // now, which is why the scope is resolved here and not when the card compiled.
                if (said.Who is not { } scope
                    || PlayerScopes.Around(scope, state, ban.ControllerId).Contains(playerId))
                {
                    return ban.Host;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The permanent forbidding this source's activated ability by name, or null (CR 602.5).
    /// </summary>
    /// <remarks>
    /// "Sources with the chosen name" is any source in any zone, so the ability's source is
    /// asked by name rather than by where it is - which is what makes a Pithing Needle naming a
    /// card in a graveyard do what it says.
    /// <para>
    /// The <em>computed</em> name, unlike the ban itself: a permanent that is a copy of Sol
    /// Ring is named Sol Ring (CR 707.2), and a Needle that named Sol Ring has to stop it.
    /// Reading <c>obj.Card.Name</c> here is the mistake this engine has recorded nine times
    /// over - a printed field where a characteristic was meant.
    /// </para>
    /// </remarks>
    public static GameObject? ActivatingForbidden(
        GameState state,
        Abilities.IAbilitySource abilities,
        GameObject source,
        bool isManaAbility)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(source);

        var activating = Characteristics.CardOf(state, abilities, source);

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoActivatingNamed)
            {
                // CR 605.1a: the exemption the Needle prints and the Revoker does not.
                if (said.ExceptManaAbilities && isManaAbility)
                    continue;

                if (NamesTheSame(ban.Host.ChosenName, activating.Name))
                    return ban.Host;
            }
        }

        return null;
    }

    /// <summary>Whether the name a permanent chose is this card's name (CR 201.2).</summary>
    /// <remarks>
    /// One place, because everything that asks it must agree about the null - and about the
    /// fact that null is "nothing was named" rather than "everything matches". Three callers
    /// now: the two scans above and the cost modifiers, which tax by the same name.
    /// </remarks>
    public static bool NameMatches(GameObject host, string cardName)
    {
        ArgumentNullException.ThrowIfNull(host);

        return NamesTheSame(host.ChosenName, cardName);
    }

    private static bool NamesTheSame(string? chosen, string cardName) =>
        chosen is { Length: > 0 }
        && string.Equals(chosen, cardName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a life-gain ban stops this player gaining life.</summary>
    public static bool Covers(LifeGainBan ban, GameState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(ban);
        ArgumentNullException.ThrowIfNull(state);

        return ban.Player == playerId
            || (ban.Players is { } scope
                && PlayerScopes.Around(scope, state, ban.ControllerId).Contains(playerId));
    }
}
