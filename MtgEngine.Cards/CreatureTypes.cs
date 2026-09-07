namespace MtgEngine.Cards;

/// <summary>
/// The hand-written half of the creature-type table: the questions the engine asks of it that
/// are more than "is this one word a creature type". The list itself is generated into
/// Generated/CreatureTypes.g.cs from CR 205.3m, so no type name is written here, and neither
/// half holds static state beyond the generated set — the field initialisers of a partial
/// class's files run in an unspecified order.
/// </summary>
public static partial class CreatureTypes
{
    /// <summary>
    /// Does this subtype list carry a creature type at all? CR 205.3d: an object that is every
    /// creature type shares a creature type with anything that has one, and with nothing that
    /// has none, so this is the question the sharing rule asks of the other object.
    /// </summary>
    [RuleRef("205.3d")]
    public static bool AnyIn(IEnumerable<string> subtypes) =>
        subtypes is not null && subtypes.Any(IsCreatureType);

    /// <summary>
    /// Do these two subtype lists have a creature type in common? CR 205.3m: only the creature
    /// types count, so a shared artifact or land subtype is not a shared creature type.
    /// </summary>
    [RuleRef("205.3m")]
    public static bool Shared(IEnumerable<string> left, IEnumerable<string> right)
    {
        if (left is null || right is null)
            return false;

        var mine = right.Where(IsCreatureType).ToList();
        return left.Where(IsCreatureType).Any(s => mine.Contains(s, StringComparer.OrdinalIgnoreCase));
    }
}
