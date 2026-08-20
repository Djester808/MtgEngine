using System.Collections.Immutable;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// What a card does when it is cast: what it targets, and what happens on resolution.
/// </summary>
/// <remarks>
/// A permanent spell needs none of this — it resolves by becoming a permanent (CR 608.3) — so
/// only instants, sorceries, and permanents with an ETB-style effect need a definition at all.
/// </remarks>
public sealed record SpellDefinition
{
    /// <summary>Targets, in the order the effects index them (CR 601.2c).</summary>
    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    /// <summary>What happens on resolution, in order (CR 608.2c).</summary>
    public ImmutableList<IEffect> Effects { get; init; } = [];

    /// <summary>An additional cost beyond the mana cost printed on the card (CR 601.2f).</summary>
    public ManaCostSpec? AlternateCost { get; init; }
}

/// <summary>
/// An ability a player can activate (CR 602.1): a cost, then an effect.
/// </summary>
/// <remarks>
/// A mana ability is one that could add mana, has no target, and is not a loyalty ability
/// (CR 605.1a). It does not use the stack and nobody can respond to it (CR 605.3b), which is why
/// it is a flag here rather than a separate kind — everything else about it is the same.
/// </remarks>
public sealed record ActivatedAbilityDefinition
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public ManaCostSpec ManaCost { get; init; } = ManaCostSpec.Free;

    /// <summary>Whether {T} is part of the cost (CR 602.5b).</summary>
    public bool RequiresTap { get; init; }

    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    public ImmutableList<IEffect> Effects { get; init; } = [];

    /// <summary>
    /// Mana this ability adds, if it is a mana ability (CR 605.1a). Non-empty means it bypasses
    /// the stack entirely.
    /// </summary>
    public ImmutableList<ManaProduction> Produces { get; init; } = [];

    public bool IsManaAbility => !Produces.IsEmpty && Targets.IsEmpty;

    /// <summary>Where the source has to be for the ability to be activatable (CR 602.5).</summary>
    public Zone FunctionsFrom { get; init; } = Zone.Battlefield;
}

/// <summary>Mana a mana ability adds (CR 106.1).</summary>
public readonly record struct ManaProduction(Domain.Enums.ManaColor? Color, int Amount = 1)
{
    /// <summary>Colourless mana, which is a kind of its own (CR 106.1b).</summary>
    public static ManaProduction Colorless(int amount = 1) => new(null, amount);
}

/// <summary>
/// Where the engine finds out what a card's spell and activated abilities do.
/// </summary>
/// <remarks>
/// Extends <see cref="IAbilitySource"/> rather than replacing it, so a card pool can grow into
/// this a piece at a time. Everything defaults to "nothing", which is what a card the engine
/// does not implement looks like — and slice 8's legality gate is what stops such a card
/// reaching a game in the first place.
/// </remarks>
public interface ISpellSource
{
    /// <summary>What the card does when it resolves as a spell, or null if it just becomes a permanent.</summary>
    SpellDefinition? SpellOf(CardDefinition card) => null;

    /// <summary>The abilities a player can activate from this card (CR 602.1).</summary>
    IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) => [];
}
