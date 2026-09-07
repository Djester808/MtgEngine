using System.Collections.Frozen;
using System.Reflection;
using MtgEngine.Cards;

namespace MtgEngine.Rules.Keywords;

/// <summary>
/// Which subsystem gives a CR 702 keyword its meaning. A keyword is never "handled here and
/// also over there": exactly one treatment per keyword, so a reader of the table knows where
/// to look and a builder knows where to add.
/// </summary>
[RuleRef("702.1")]
public enum KeywordTreatment
{
    /// <summary>
    /// Combat and the layer system read the keyword straight off computed characteristics,
    /// CR 613 and CR 509.1b. The <see cref="KeywordAbility"/> stays on the face.
    /// </summary>
    CombatEvasion,

    /// <summary>
    /// Casting reads the keyword when a spell is put on the stack: an alternative cost
    /// (CR 118.9), an additional cost (CR 601.2b), a timing permission (CR 601.3) or an
    /// activated ability whose cost is printed with the keyword (CR 602).
    /// </summary>
    AlternativeOrOptionalCost,

    /// <summary>
    /// The keyword is a triggered or activated ability in disguise, CR 702.1a. The expander
    /// rewrites it into the concrete record the triggers subsystem and the interpreter read.
    /// </summary>
    TriggeredAbilityExpansion,

    /// <summary>The keyword is a static ability, CR 604. Either expanded, or read by casting.</summary>
    StaticExpansion,

    /// <summary>No engine treatment yet. The expander leaves an <see cref="Unsupported"/> marker.</summary>
    Unsupported,
}

/// <summary>
/// What a cost keyword changes about paying for a spell or ability, CR 118. The casting system
/// reads this to know whether the printed cost replaces the mana cost, is paid on top of it,
/// or only widens when the spell may be cast.
/// </summary>
[RuleRef("118")]
public enum CostShape
{
    /// <summary>Not a cost keyword.</summary>
    None,

    /// <summary>Replaces the mana cost, CR 118.9: flashback, evoke, dash, overload, escape, prototype, mutate, morph's turn-up.</summary>
    Alternative,

    /// <summary>Paid in addition to the mana cost, CR 601.2b: kicker, buyback.</summary>
    Additional,

    /// <summary>Only changes when the spell may be cast, CR 601.3: flash, madness's window.</summary>
    TimingPermission,

    /// <summary>An activated ability whose cost is printed with the keyword, CR 602: equip, cycling, foretell's second half.</summary>
    Activated,
}

/// <summary>
/// One keyword's entry: which subsystem owns it, what its printed parameter means, and — for
/// the cost keywords — which cost it is and which zone it lets the card be played from.
/// The CR number is not repeated here; it is read off <see cref="KeywordKind"/>'s own
/// <see cref="RuleRefAttribute"/>, which the vocabulary generator writes from the rulebook,
/// so the table cannot drift from the rules file.
/// </summary>
[RuleRef("702")]
public sealed record KeywordSemantic(
    KeywordKind Keyword,
    KeywordTreatment Treatment,
    string Note,
    bool Expands = false,
    CostShape Cost = CostShape.None,
    Zone? CastFrom = null)
{
    /// <summary>The CR 702 subrule that defines this keyword.</summary>
    public string Rule => KeywordSemantics.RuleOf(Keyword);
}

/// <summary>
/// The one place that says, for every <see cref="KeywordKind"/>, which subsystem gives it
/// meaning and how. CR 702.1: a keyword ability is shorthand for one or more abilities, so
/// every keyword either becomes a concrete ability (<see cref="KeywordExpander"/>), or is read
/// in place by combat, the layers or casting, or is honestly marked unsupported.
///
/// The table is split across files by family; this file holds the model and the lookup.
/// </summary>
[RuleRef("702.1")]
public static partial class KeywordSemantics
{
    // Declared before Table: static field initialisers run in textual order, and a reader who
    // later makes Build() cite a rule would otherwise get an empty map with no error.
    private static readonly FrozenDictionary<KeywordKind, string> RuleNumbers =
        typeof(KeywordKind)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToFrozenDictionary(
                f => (KeywordKind)f.GetValue(null)!,
                f => f.GetCustomAttribute<RuleRefAttribute>()?.Rule ?? "702");

    private static readonly FrozenDictionary<KeywordKind, KeywordSemantic> Table = Build();

    /// <summary>Every keyword's semantics, one entry per <see cref="KeywordKind"/> member.</summary>
    public static IReadOnlyCollection<KeywordSemantic> All => Table.Values;

    /// <summary>The semantics of one keyword. Throws if the table has a gap, which a test forbids.</summary>
    public static KeywordSemantic Of(KeywordKind keyword) =>
        Table.TryGetValue(keyword, out var semantic)
            ? semantic
            : throw new ArgumentOutOfRangeException(
                nameof(keyword),
                keyword,
                "No keyword semantics entry. Every KeywordKind member needs one.");

    /// <summary>The CR 702 subrule for a keyword, read from the generated enum's rule reference.</summary>
    public static string RuleOf(KeywordKind keyword) =>
        RuleNumbers.TryGetValue(keyword, out var rule) ? rule : "702";

    /// <summary>True when combat or the layer system reads the keyword off characteristics, CR 613.</summary>
    public static bool IsCharacteristic(KeywordKind keyword) =>
        Of(keyword).Treatment == KeywordTreatment.CombatEvasion;

    private static FrozenDictionary<KeywordKind, KeywordSemantic> Build()
    {
        // The four families plus the explicit unsupported list partition the enum. Duplicates
        // throw here, so a keyword can never be claimed by two subsystems.
        var entries = Evasion()
            .Concat(CostKeywords())
            .Concat(TriggeredExpansions())
            .Concat(StaticExpansions())
            .Concat(UnsupportedKeywords().Select(k => new KeywordSemantic(
                k,
                KeywordTreatment.Unsupported,
                "No engine treatment yet: the expander leaves an Unsupported marker naming it.")));

        return entries.ToFrozenDictionary(s => s.Keyword);
    }
}
