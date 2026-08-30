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

    /// <summary>Whether this card forbids nothing at all, asked before anything is built.</summary>
    public bool IsEmpty => Unpreventable.IsEmpty && NoLifeGain.IsEmpty;
}

/// <summary>Whether a prohibition covers a player (CR 119.7).</summary>
public static class Bans
{
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
