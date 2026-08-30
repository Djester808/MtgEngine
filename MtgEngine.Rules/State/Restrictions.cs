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

    /// <summary>Whether this card forbids nothing at all, asked before anything is built.</summary>
    public bool IsEmpty => Unpreventable.IsEmpty && NoLifeGain.IsEmpty && NoCounter.IsEmpty;
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
