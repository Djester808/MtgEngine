using MtgEngine.Cards;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// The cards a scenario places when it needs a card to exist and does not care which one: a
/// library to draw, mill and search from, a body to point a removal spell at, a spell to counter.
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

    /// <summary>
    /// A basic land, so a search that says "basic land card" (CR 205.4a) finds one. Kept apart from
    /// <see cref="Land"/> because a filter can say either basic or nonbasic, and one card cannot be
    /// both; the plain filler stays nonbasic so it still answers "nonbasic".
    /// </summary>
    [RuleRef("205.4a")]
    public const string BasicLand = "Scenario Filler Basic Land";

    /// <summary>A vanilla 2/2: something a removal spell or a counter can be aimed at.</summary>
    public const string Creature = "Scenario Filler Creature";

    /// <summary>An artifact with no abilities: what "destroy target artifact" is pointed at (CR 301.1).</summary>
    [RuleRef("301.1")]
    public const string Artifact = "Scenario Filler Artifact";

    /// <summary>An enchantment with no abilities: what "destroy target enchantment" is pointed at (CR 303.1).</summary>
    [RuleRef("303.1")]
    public const string Enchantment = "Scenario Filler Enchantment";

    /// <summary>An instant that does nothing: something to put on the stack so a counterspell has a target.</summary>
    public const string Spell = "Scenario Filler Spell";

    /// <summary>A sorcery that does nothing: what a search for a sorcery card finds.</summary>
    public const string Sorcery = "Scenario Filler Sorcery";

    /// <summary>The definition for one of the filler names, or null when the name is a real card's.</summary>
    public static CardDefinition? For(string? name) => name switch
    {
        Land => LandDefinition,
        BasicLand => BasicLandDefinition,
        Creature => CreatureDefinition,
        Artifact => ArtifactDefinition,
        Enchantment => EnchantmentDefinition,
        Spell => SpellDefinition,
        Sorcery => SorceryDefinition,
        _ => null,
    };

    /// <summary>Every filler, so a generator can ask which of them a filter describes.</summary>
    public static IReadOnlyList<string> Names => [Creature, Artifact, Enchantment, Land, BasicLand, Spell, Sorcery];

    /// <summary>The printed type line of one filler, so a generator need not repeat what is written here.</summary>
    public static TypeLine? TypesOf(string? name) => For(name)?.Faces[0].Types;

    private static CardDefinition LandDefinition { get; } = new(
        "scenario-filler-land",
        Land,
        Layout.Normal,
        [new Face(Land, FaceRole.Main, new TypeLine(CardType.Land))]);

    private static CardDefinition BasicLandDefinition { get; } = new(
        "scenario-filler-basic-land",
        BasicLand,
        Layout.Normal,
        [new Face(BasicLand, FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic))]);

    private static CardDefinition CreatureDefinition { get; } = new(
        "scenario-filler-creature",
        Creature,
        Layout.Normal,
        [new Face(Creature, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{2}", Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);

    private static CardDefinition ArtifactDefinition { get; } = new(
        "scenario-filler-artifact",
        Artifact,
        Layout.Normal,
        [new Face(Artifact, FaceRole.Main, new TypeLine(CardType.Artifact), ManaCost: "{2}")]);

    private static CardDefinition EnchantmentDefinition { get; } = new(
        "scenario-filler-enchantment",
        Enchantment,
        Layout.Normal,
        [new Face(Enchantment, FaceRole.Main, new TypeLine(CardType.Enchantment), ManaCost: "{2}")]);

    private static CardDefinition SpellDefinition { get; } = new(
        "scenario-filler-spell",
        Spell,
        Layout.Normal,
        [new Face(Spell, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}")]);

    private static CardDefinition SorceryDefinition { get; } = new(
        "scenario-filler-sorcery",
        Sorcery,
        Layout.Normal,
        [new Face(Sorcery, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}")]);
}
