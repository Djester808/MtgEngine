using MtgEngine.Domain.Enums;

namespace MtgEngine.Domain.Models;

/// <summary>
/// A target for a spell or ability.
/// </summary>
public sealed class Target
{
    public TargetType Type { get; init; }
    public Guid Id { get; init; } // PermanentId, PlayerId, or CardId depending on Type
}

public enum TargetType
{
    Permanent,
    Player,
    Card,       // targeting something in a zone (e.g. graveyard)
    StackObject,
}

/// <summary>
/// Base interface for all objects that can exist on the stack.
/// </summary>
public interface IStackObject
{
    Guid StackObjectId { get; }
    Guid ControllerId { get; }
    IReadOnlyList<Target> Targets { get; }
    string Description { get; }
}

/// <summary>
/// A spell on the stack -- a card that has been cast but not yet resolved.
/// </summary>
public sealed class SpellOnStack : IStackObject
{
    public Guid StackObjectId { get; init; } = Guid.NewGuid();
    public Guid ControllerId { get; init; }
    public Card SourceCard { get; init; } = null!;
    public IReadOnlyList<Target> Targets { get; init; } = [];
    public string Description => $"{SourceCard.Name} (spell)";
}

/// <summary>
/// An activated ability on the stack.
/// </summary>
public sealed class ActivatedAbilityOnStack : IStackObject
{
    public Guid StackObjectId { get; init; } = Guid.NewGuid();
    public Guid ControllerId { get; init; }
    public Guid SourcePermanentId { get; init; }
    public string AbilityText { get; init; } = string.Empty;
    public IReadOnlyList<Target> Targets { get; init; } = [];
    public string Description => $"Activated ability of {SourcePermanentId}: {AbilityText}";
}

/// <summary>
/// A triggered ability on the stack.
/// </summary>
public sealed class TriggeredAbilityOnStack : IStackObject
{
    public Guid StackObjectId { get; init; } = Guid.NewGuid();
    public Guid ControllerId { get; init; }
    public Guid SourceId { get; init; } // permanent or card that triggered
    public string TriggerText { get; init; } = string.Empty;
    public IReadOnlyList<Target> Targets { get; init; } = [];
    public string Description => $"Triggered ability: {TriggerText}";
}
