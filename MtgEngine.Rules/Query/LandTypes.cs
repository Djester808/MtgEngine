using MtgEngine.Cards;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The basic land types of CR 205.3i. This is rules data, not card data: the five words name
/// land subtypes the rules define, and no card is named by reading them — a land with a basic
/// land type need not be a basic land (CR 205.4a), which is the whole reason the question is
/// asked separately from <c>Supertype.Basic</c>.
/// </summary>
/// <remarks>
/// It belongs with the other generated rules data (<c>MtgEngine.Cards/Generated</c>, the way
/// CR 205.3m's creature types are generated from the rules text), and until the generator emits
/// it this is the one place the engine spells them — the literal that used to sit inside the
/// domain count is gone. See seam-requests/E0d.md.
/// </remarks>
[RuleRef("205.3i")]
internal static class LandTypes
{
    /// <summary>The five basic land types, CR 205.3i, in the order the rule lists them.</summary>
    [RuleRef("205.3i")]
    public static ImmutableArray<string> Basic { get; } =
        ["Plains", "Island", "Swamp", "Mountain", "Forest"];
}
