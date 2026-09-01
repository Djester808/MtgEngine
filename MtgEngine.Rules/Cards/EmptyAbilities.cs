using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// An ability source with nothing in it, for questions asked outside a game.
/// </summary>
/// <remarks>
/// A target filter takes an <see cref="IAbilitySource"/> because some of them ask for computed
/// characteristics, and computing those can need granted abilities. Plenty of callers have no
/// game to hand — the compiler counting permanents for affinity, a generated definition working
/// out how big a "for each" pump is — and for the questions they ask, nothing granted matters.
/// <para>
/// One shared instance rather than a private copy per file. There were three, which is how a
/// helper starts to drift: each copy is small enough that nobody notices the fourth is subtly
/// different from the first.
/// </para>
/// </remarks>
internal sealed class EmptyAbilities : IAbilitySource
{
    public static readonly EmptyAbilities Instance = new();

    public IReadOnlyList<TriggeredAbilityDefinition> TriggersOf(CardDefinition card) => [];
}
