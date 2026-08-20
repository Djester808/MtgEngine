using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// A triggered ability a card has: a condition that watches events, and text (CR 603.1).
/// </summary>
/// <remarks>
/// The condition is a delegate and therefore lives out here, never in <see cref="GameState"/>.
/// State has to fold from a log and compare by value; a captured closure does neither. What the
/// state remembers is that <em>this ability of this object</em> is waiting to go on the stack —
/// see <see cref="PendingTrigger"/> — and the definition is looked up again by id.
/// </remarks>
public sealed record TriggeredAbilityDefinition
{
    /// <summary>Stable within its card, so a pending trigger can name it across a replay.</summary>
    public required string Id { get; init; }

    /// <summary>The ability's text, which is all it has on the stack (CR 405.4).</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether this event triggers the ability (CR 603.2). The object is the source as it was
    /// when the event happened.
    /// </summary>
    public required Func<GameEvent, GameState, GameObject, bool> Triggers { get; init; }

    /// <summary>
    /// Where the source has to be for the ability to work. Almost everything triggers from the
    /// battlefield (CR 603.6); "when this dies" and "when you discard this" do not.
    /// </summary>
    public Zone FunctionsFrom { get; init; } = Zone.Battlefield;
}

/// <summary>
/// Where the engine finds out what abilities a card has.
/// </summary>
/// <remarks>
/// The seam for the card layer. Nothing implements this yet beyond tests: the backbone is being
/// settled before card behaviour exists, and this is the shape the card definitions of slice 8
/// will plug into.
/// </remarks>
public interface IAbilitySource : ISpellSource
{
    /// <summary>The triggered abilities of a card, or an empty list if it has none.</summary>
    IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card);

    /// <summary>
    /// The continuous effects a card's static abilities produce (CR 604.2).
    /// </summary>
    /// <remarks>
    /// Asked of every permanent on the battlefield each time characteristics are computed, which
    /// is what makes a lord's bonus vanish the moment the lord does.
    /// </remarks>
    IReadOnlyList<ContinuousEffectDefinition> StaticsOf(CardDefinition card) => [];

    /// <summary>The replacement effects a card produces (CR 614).</summary>
    IReadOnlyList<ReplacementEffectDefinition> ReplacementsOf(CardDefinition card) => [];

    /// <summary>
    /// Looks up an effect created by a resolved spell or ability, by the id the game recorded.
    /// </summary>
    /// <remarks>
    /// These outlive their source (CR 613.7b) — "target creature gets +3/+3 until end of turn"
    /// keeps working after the spell is in the graveyard — so the game records that the effect
    /// exists and finds out what it does again from here.
    /// </remarks>
    ContinuousEffectDefinition? FloatingEffect(string definitionId) => null;
}

/// <summary>A card pool with no abilities at all — the default while cards do nothing.</summary>
public sealed class NoAbilities : IAbilitySource
{
    public static readonly NoAbilities Instance = new();

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];
}
