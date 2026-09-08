using MtgEngine.Cards;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// The abilities an object has because of what it is rather than because of what it printed or
/// what an effect granted it. CR 305.6: an object with the land card type and a basic land type
/// has the intrinsic ability "{T}: Add [mana symbol]" for that type, "even if the text box
/// doesn't actually contain that text or the object has no text box" — which is every real
/// Plains, Island, Swamp, Mountain and Forest, whose printed face carries no ability at all.
/// CR 305.7 is the other half of the same sentence: a land that gains a basic land type from an
/// effect gains that type's mana ability with it. Both are answered by reading the subtypes
/// layer 4 settled (CR 613.1d) and never the printed face, so one arm serves the printed land
/// and the animated one alike.
/// </summary>
public sealed partial class LayerSystem
{
    /// <summary>
    /// Add the mana ability of every basic land type this object now has, CR 305.6 with 305.7.
    /// <para>
    /// It is applied after layer 4 has settled the subtypes and before layer 6 applies the
    /// ability-adding and ability-removing effects (CR 613.1f), so the object carries this
    /// ability into layer 6 exactly the way it carries the ones its face printed: an effect that
    /// removes all abilities removes this one too, and a later grant sits beside it.
    /// </para>
    /// </summary>
    [RuleRef("305.6")]
    private static void IntrinsicManaAbilities(Draft draft)
    {
        // CR 305.6 asks for the land card type, and nothing else: not the Basic supertype (CR
        // 305.8 keeps "is it a basic land" a separate question, and a nonbasic land with a basic
        // land type still taps for that mana), not the battlefield (a land card in any zone has
        // the ability; CR 605.3a is what stops it being activated from a library), and not a
        // permanent. A creature that is a Forest without being a land therefore gains nothing
        // here — CR 305.6 speaks of objects with the land card type, so the subtype alone is not
        // enough, and layer 4 is where a card becomes a land if an effect says it is one.
        if (!draft.Types.HasFlag(CardType.Land))
            return;

        foreach (var subtype in draft.Subtypes)
        {
            if (BasicLandMana(subtype) is { } mana)
                draft.Abilities.Add(ManaAbility(mana));
        }
    }

    /// <summary>
    /// The mana CR 305.6 pairs with each basic land type, or null for every other subtype. The
    /// five names are the rules data of CR 205.3i (<see cref="LandTypes.Basic"/>, generated from
    /// the rules text); which mana symbol goes with each is CR 305.6's own sentence. The table is
    /// built in a method because the files of a partial class initialise in an unspecified order.
    /// </summary>
    [RuleRef("305.6")]
    private static string? BasicLandMana(string subtype)
    {
        foreach (var (type, mana) in BasicLandManaTable())
        {
            if (string.Equals(subtype, type, StringComparison.OrdinalIgnoreCase))
                return mana;
        }

        return null;
    }

    /// <summary>CR 305.6: "For Plains, [mana symbol] is {W}; for Islands, {U}; ... for Forests, {G}."</summary>
    [RuleRef("305.6")]
    private static (string Type, string Mana)[] BasicLandManaTable() =>
    [
        ("Plains", "{W}"),
        ("Island", "{U}"),
        ("Swamp", "{B}"),
        ("Mountain", "{R}"),
        ("Forest", "{G}"),
    ];

    /// <summary>
    /// "{T}: Add [mana symbol]" as an ability the rest of the engine can read: an activated
    /// ability (CR 602) whose cost is tapping the permanent itself and whose effect adds one
    /// fixed mana. It is a mana ability by every one of CR 605.1a's four criteria and by
    /// construction rather than by inspection — it requires no target, it could add mana when it
    /// resolves, it is not a loyalty ability, and neither its cost nor its effect moves a card to
    /// or from a library — so it resolves without using the stack (CR 605.3a).
    /// </summary>
    [RuleRef("605.1a")]
    private static ActivatedAbility ManaAbility(string mana) =>
        new([new TapSelf()], new AddMana(new ManaProduction(Fixed: mana)));
}
