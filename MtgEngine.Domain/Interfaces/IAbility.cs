using MtgEngine.Domain.Models;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Domain.Interfaces;

/// <summary>
/// Base interface for all card abilities.
/// </summary>
public interface IAbility
{
    string RulesText { get; }
}

/// <summary>
/// An ability a player can choose to activate (e.g. "T: Add G").
/// </summary>
public interface IActivatedAbility : IAbility
{
    /// <summary>Returns true if the ability can be activated given the current game state.</summary>
    bool CanActivate(Permanent source, Guid controllerId, GameState state);

    /// <summary>Puts the ability on the stack or applies it (mana abilities bypass the stack).</summary>
    GameState Activate(Permanent source, Guid controllerId, GameState state);

    bool IsManaAbility { get; }
}

/// <summary>
/// An ability that triggers from a game event.
/// </summary>
public interface ITriggeredAbility : IAbility
{
    /// <summary>Returns true if this ability triggers on the given event.</summary>
    bool TriggersOn(GameEvent gameEvent, GameState state);

    /// <summary>Creates the triggered ability stack object when the ability triggers.</summary>
    TriggeredAbilityOnStack CreateStackObject(Permanent source, GameEvent triggeringEvent, GameState state);
}

/// <summary>
/// A static ability that continuously modifies the game (e.g. "All creatures get +1/+1").
/// Static abilities are applied during the layers calculation, not stacked.
/// </summary>
public interface IStaticAbility : IAbility
{
    /// <summary>Applies the continuous effect to the given game state. Called when recalculating state.</summary>
    GameState Apply(Permanent source, GameState state);
}

/// <summary>
/// Represents a game event that abilities may trigger on.
/// </summary>
public abstract record GameEvent;

public sealed record CreatureEnteredBattlefield(Guid PermanentId, Guid ControllerId) : GameEvent;
public sealed record CreatureDied(Guid PermanentId, Card SourceCard, Guid ControllerId) : GameEvent;
public sealed record SpellCast(Guid StackObjectId, Card SourceCard, Guid ControllerId) : GameEvent;
public sealed record PlayerDrawCard(Guid PlayerId) : GameEvent;
public sealed record PlayerGainedLife(Guid PlayerId, int Amount) : GameEvent;
public sealed record PlayerLostLife(Guid PlayerId, int Amount) : GameEvent;
public sealed record DamageDealt(Guid SourceId, Guid TargetId, int Amount, bool IsCombat) : GameEvent;
public sealed record PhaseChanged(Phase From, Phase To) : GameEvent;
public sealed record StepChanged(Step From, Step To) : GameEvent;
public sealed record TurnBegan(int TurnNumber, Guid ActivePlayerId) : GameEvent;
public sealed record PermanentTapped(Guid PermanentId) : GameEvent;
public sealed record AttackDeclared(IReadOnlyList<Guid> AttackerIds) : GameEvent;
public sealed record BlockDeclared(Guid AttackerId, Guid BlockerId) : GameEvent;
