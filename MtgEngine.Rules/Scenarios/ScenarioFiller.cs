using MtgEngine.Cards;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// The cards a scenario places when it needs a card to exist and does not care which one: a
/// library to draw and mill from, a body to point a removal spell at, a spell to counter.
/// </summary>
/// <remarks>
/// They are invented here rather than taken from <c>cards/</c> on purpose. CR 103.2 wants a
/// library, not a particular card, and a generated scenario that filled one with a real card
/// would be asserting that card's behaviour as well as the behaviour of the card under test —
/// a failure in the filler would then be read as a failure of whatever was being verified.
/// Nothing here is a real card name, so the rule that no card may appear in engine source still
/// holds: these are furniture, and the definition store never sees them.
/// </remarks>
public static class ScenarioFiller
{
    /// <summary>A land with no abilities: something to draw, mill, discard or search for.</summary>
    public const string Land = "Scenario Filler Land";

    /// <summary>A vanilla 2/2: something a removal spell or a counter can be aimed at.</summary>
    public const string Creature = "Scenario Filler Creature";

    /// <summary>An instant that does nothing: something to put on the stack so a counterspell has a target.</summary>
    public const string Spell = "Scenario Filler Spell";

    /// <summary>The definition for one of the filler names, or null when the name is a real card's.</summary>
    public static CardDefinition? For(string? name) => name switch
    {
        Land => LandDefinition,
        Creature => CreatureDefinition,
        Spell => SpellDefinition,
        _ => null,
    };

    private static CardDefinition LandDefinition { get; } = new(
        "scenario-filler-land",
        Land,
        Layout.Normal,
        [new Face(Land, FaceRole.Main, new TypeLine(CardType.Land))]);

    private static CardDefinition CreatureDefinition { get; } = new(
        "scenario-filler-creature",
        Creature,
        Layout.Normal,
        [new Face(Creature, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{2}", Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);

    private static CardDefinition SpellDefinition { get; } = new(
        "scenario-filler-spell",
        Spell,
        Layout.Normal,
        [new Face(Spell, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}")]);
}
