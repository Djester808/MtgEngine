using System.Collections.Immutable;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>What a target is (CR 115.1).</summary>
public enum TargetKind
{
    Permanent,
    Player,
    SpellOnStack,
    CardInGraveyard,
}

/// <summary>One chosen target (CR 115.1).</summary>
public readonly record struct Target(TargetKind Kind, ObjectId Subject, Guid Player)
{
    public static Target ToPermanent(ObjectId id) => new(TargetKind.Permanent, id, Guid.Empty);

    public static Target ToPlayer(Guid id) => new(TargetKind.Player, default, id);

    public static Target ToSpell(ObjectId id) => new(TargetKind.SpellOnStack, id, Guid.Empty);

    public static Target ToCard(ObjectId id) => new(TargetKind.CardInGraveyard, id, Guid.Empty);
}

/// <summary>
/// What a spell or ability may target (CR 115.1).
/// </summary>
/// <remarks>
/// The legality question is asked twice: when targets are chosen, as the spell is cast
/// (CR 601.2c), and again when it resolves (CR 608.2b). A spell whose only target has become
/// illegal in between does not resolve at all. That second check is why this is a rule the
/// engine keeps rather than something checked once at the point of casting.
/// </remarks>
public sealed record TargetSpec
{
    public required TargetKind Kind { get; init; }

    /// <summary>Reads naturally in an error: "target creature you control".</summary>
    public required string Description { get; init; }

    /// <summary>Which objects qualify. Null accepts any object of the right kind.</summary>
    public Func<GameState, IAbilitySource, GameObject, Guid, bool>? ObjectFilter { get; init; }

    /// <summary>Which players qualify. Null accepts any player still in the game.</summary>
    public Func<GameState, Guid, Guid, bool>? PlayerFilter { get; init; }

    /// <summary>Whether the given target is currently legal for the given controller.</summary>
    public bool IsLegal(GameState state, IAbilitySource abilities, Target target, Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (target.Kind != Kind)
            return false;

        if (Kind == TargetKind.Player)
        {
            return state.Players.ContainsKey(target.Player)
                && !state.GetPlayer(target.Player).HasLost
                && (PlayerFilter?.Invoke(state, target.Player, controllerId) ?? true);
        }

        if (!state.TryGetObject(target.Subject, out var obj))
            return false;

        var expectedZone = Kind switch
        {
            TargetKind.Permanent => Zone.Battlefield,
            TargetKind.SpellOnStack => Zone.Stack,
            TargetKind.CardInGraveyard => Zone.Graveyard,
            _ => Zone.Battlefield,
        };

        return obj.Zone == expectedZone
            && (ObjectFilter?.Invoke(state, abilities, obj, controllerId) ?? true);
    }
}

/// <summary>Everything an effect needs to know while it resolves (CR 608.2).</summary>
public sealed record ResolutionContext
{
    public required GameState State { get; init; }

    public required IAbilitySource Abilities { get; init; }

    /// <summary>Who controls the spell or ability, and so who "you" means (CR 608.2).</summary>
    public required Guid ControllerId { get; init; }

    /// <summary>The object on the stack that is resolving.</summary>
    public required ObjectId SourceId { get; init; }

    public ImmutableList<Target> Targets { get; init; } = [];

    /// <summary>The value chosen for X as the spell was cast (CR 601.2b).</summary>
    public int VariableValue { get; init; }

    public Target? TargetAt(int index) =>
        index >= 0 && index < Targets.Count ? Targets[index] : null;
}

/// <summary>
/// One thing a spell or ability does when it resolves.
/// </summary>
/// <remarks>
/// The vocabulary cards are built from. An effect reports the events it wants to happen; it does
/// not apply them, so it cannot mutate state behind the reducer's back and everything it does
/// lands in the log like everything else.
/// </remarks>
public interface IEffect
{
    IReadOnlyList<GameEvent> Resolve(ResolutionContext context);
}

/// <summary>Deals damage to a target creature or player (CR 119.3, 120).</summary>
public sealed record DealDamage(int Amount, int TargetIndex = 0, bool Deathtouch = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { } target)
            return [];

        return target.Kind switch
        {
            TargetKind.Player =>
                [new PlayerDamaged(target.Player, context.SourceId, Amount, IsCombat: false)],
            TargetKind.Permanent =>
                [new DamageMarked(target.Subject, Amount, Deathtouch)],
            _ => [],
        };
    }
}

/// <summary>Destroys a target permanent (CR 701.7).</summary>
/// <remarks>
/// Destruction is a move to the graveyard, which indestructible replaces and regeneration can
/// replace (CR 701.7b). It goes through the same event as any other zone change, so those
/// replacements see it.
/// </remarks>
public sealed record DestroyTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var permanent))
            return [];

        return
        [
            new ObjectMoved(
                target.Subject, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                permanent.ControllerId, MoveCause.Destroy),
        ];
    }
}

/// <summary>Exiles a target permanent (CR 406.2).</summary>
public sealed record ExileTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var permanent))
            return [];

        return
        [
            new ObjectMoved(
                target.Subject, ObjectId.New(), Zone.Battlefield, Zone.Exile,
                permanent.ControllerId, MoveCause.Exile),
        ];
    }
}

/// <summary>Draws cards (CR 121.3). "You" is the controller unless a target says otherwise.</summary>
public sealed record DrawCards(int Count, int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var who = TargetIndex is null
            ? context.ControllerId
            : context.TargetAt(TargetIndex.Value)?.Player ?? context.ControllerId;

        var events = new List<GameEvent>();
        var library = context.State.GetPlayer(who).Library;

        for (var i = 0; i < Count; i++)
        {
            if (i >= library.Count)
            {
                // CR 121.4: the draw does not happen and the attempt is remembered. The loss is
                // a state-based action later, so the rest of the effect still resolves.
                events.Add(new DrawFromEmptyLibraryAttempted(who));
                break;
            }

            events.Add(new ObjectMoved(
                library[i], ObjectId.New(), Zone.Library, Zone.Hand, who, MoveCause.Draw));
        }

        return events;
    }
}

/// <summary>Gains or loses life (CR 119.3).</summary>
public sealed record ChangeLife(int Amount, int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var who = TargetIndex is null
            ? context.ControllerId
            : context.TargetAt(TargetIndex.Value)?.Player ?? context.ControllerId;

        return [new LifeChanged(who, Amount, context.State.GetPlayer(who).Life + Amount)];
    }
}

/// <summary>Puts counters on a target permanent (CR 121.2).</summary>
public sealed record PutCounters(string Kind, int Count, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return [new CountersChanged(target.Subject, Kind, Count)];
    }
}

/// <summary>
/// Gives a target creature a bonus until end of turn — the pump effect (CR 611.2).
/// </summary>
/// <remarks>
/// Creates a continuous effect rather than editing the creature, so it applies in layer 7c and
/// ends during cleanup (CR 514.2) without anything having to remember to take it off.
/// </remarks>
public sealed record PumpUntilEndOfTurn(string DefinitionId, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, [target.Subject], context.State.TurnNumber),
        ];
    }
}

/// <summary>Counters a target spell (CR 701.5).</summary>
/// <remarks>
/// A countered spell is put into its owner's graveyard from the stack; it does not resolve, so
/// none of its effects happen (CR 701.5a).
/// </remarks>
public sealed record CounterTargetSpell(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.SpellOnStack } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var spell))
            return [];

        return
        [
            new ObjectMoved(
                target.Subject, ObjectId.New(), Zone.Stack, Zone.Graveyard,
                spell.ControllerId, MoveCause.Other),
        ];
    }
}

/// <summary>Creates a token under the controller's control (CR 111.1).</summary>
public sealed record CreateToken(Domain.Models.CardDefinition Token, int Count = 1) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. Enumerable.Range(0, Count).Select(_ => new ObjectCreated(
                ObjectId.New(), Token, context.ControllerId, context.ControllerId,
                Zone.Battlefield)),
        ];
    }
}
