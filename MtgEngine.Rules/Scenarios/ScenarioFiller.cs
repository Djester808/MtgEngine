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

    /// <summary>
    /// One basic land per basic land type, CR 305.6, so a search that names a land type ("a Forest
    /// card", CR 205.3i) finds one. Each carries only the type line: the intrinsic mana ability is
    /// CR 305.6 data the engine already reads off the subtype, and a smoke scenario asserts the zone.
    /// </summary>
    [RuleRef("305.6")]
    public const string Plains = "Scenario Filler Plains";

    /// <inheritdoc cref="Plains"/>
    public const string Island = "Scenario Filler Island";

    /// <inheritdoc cref="Plains"/>
    public const string Swamp = "Scenario Filler Swamp";

    /// <inheritdoc cref="Plains"/>
    public const string Mountain = "Scenario Filler Mountain";

    /// <inheritdoc cref="Plains"/>
    public const string Forest = "Scenario Filler Forest";

    /// <summary>
    /// A green 1/1 for {G}, CR 202.2: the one coloured filler, so a search that says "green creature
    /// card" finds one. Its colour is printed as an indicator too (CR 202.2b), so a generator reads
    /// it off <see cref="ColorOf"/> without parsing the cost; the two agree.
    /// </summary>
    [RuleRef("202.2")]
    public const string GreenCreature = "Scenario Filler Green Creature";

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

    /// <summary>
    /// An instant for {R} that deals 1 damage to any target, CR 115.4: something on the stack that
    /// has a target, so "change the target of target spell" has a spell to retarget.
    /// </summary>
    [RuleRef("115.4")]
    public const string Bolt = "Scenario Filler Bolt";

    /// <summary>A sorcery that does nothing: what a search for a sorcery card finds.</summary>
    public const string Sorcery = "Scenario Filler Sorcery";

    /// <summary>
    /// A creature that costs nothing, and one that costs six. A filter can compare a mana value
    /// (CR 202.3) — "a creature card with mana value X or less", "with mana value 5 or greater" —
    /// and a library holding one printed cost answers neither comparison. These two stand at the
    /// ends of the range so a search that names a bound finds a card on the right side of it; the
    /// plain <see cref="Creature"/> at two is what everything else still gets.
    /// </summary>
    [RuleRef("202.3")]
    public const string CheapCreature = "Scenario Filler Cheap Creature";

    /// <inheritdoc cref="CheapCreature"/>
    [RuleRef("202.3")]
    public const string CostlyCreature = "Scenario Filler Costly Creature";

    /// <summary>The definition for one of the filler names, or null when the name is a real card's.</summary>
    public static CardDefinition? For(string? name) => name switch
    {
        Land => LandDefinition,
        BasicLand => BasicLandDefinition,
        Plains => PlainsDefinition,
        Island => IslandDefinition,
        Swamp => SwampDefinition,
        Mountain => MountainDefinition,
        Forest => ForestDefinition,
        GreenCreature => GreenCreatureDefinition,
        Creature => CreatureDefinition,
        Artifact => ArtifactDefinition,
        Enchantment => EnchantmentDefinition,
        Spell => SpellDefinition,
        Bolt => BoltDefinition,
        Sorcery => SorceryDefinition,
        CheapCreature => CheapCreatureDefinition,
        CostlyCreature => CostlyCreatureDefinition,
        _ => null,
    };

    /// <summary>Every filler, so a generator can ask which of them a filter describes.</summary>
    public static IReadOnlyList<string> Names =>
        [Creature, Artifact, Enchantment, Land, BasicLand, Spell, Sorcery, CheapCreature, CostlyCreature, Plains, Island, Swamp, Mountain, Forest, GreenCreature, Bolt];

    /// <summary>The printed colour of one filler, CR 202.2b: the indicator it carries, none for a colourless filler, null for a real card.</summary>
    [RuleRef("202.2b")]
    public static Color? ColorOf(string? name) => For(name) is { } definition ? definition.Faces[0].ColorIndicator ?? Color.Colorless : null;

    /// <summary>The printed type line of one filler, so a generator need not repeat what is written here.</summary>
    public static TypeLine? TypesOf(string? name) => For(name)?.Faces[0].Types;

    /// <summary>
    /// CR 202.3: the mana value of one filler's printed cost — the total of its symbols, with a
    /// generic symbol worth what it says and a coloured one worth one. Null when the name is not a
    /// filler's; a card with no printed cost is worth nothing, which is a mana value of zero.
    /// </summary>
    [RuleRef("202.3")]
    public static int? ManaValueOf(string? name)
    {
        if (For(name) is not { } definition)
            return null;
        var cost = definition.Faces[0].ManaCost;
        if (cost is not { Length: > 0 })
            return 0;

        var total = 0;
        foreach (var symbol in cost.Split('{', StringSplitOptions.RemoveEmptyEntries))
        {
            var body = symbol.TrimEnd('}');
            total += int.TryParse(body, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var generic)
                ? generic
                : 1;
        }

        return total;
    }

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

    private static CardDefinition PlainsDefinition { get; } = BasicOfType(Plains, "scenario-filler-plains", "Plains");

    private static CardDefinition IslandDefinition { get; } = BasicOfType(Island, "scenario-filler-island", "Island");

    private static CardDefinition SwampDefinition { get; } = BasicOfType(Swamp, "scenario-filler-swamp", "Swamp");

    private static CardDefinition MountainDefinition { get; } = BasicOfType(Mountain, "scenario-filler-mountain", "Mountain");

    private static CardDefinition ForestDefinition { get; } = BasicOfType(Forest, "scenario-filler-forest", "Forest");

    private static CardDefinition GreenCreatureDefinition { get; } = new(
        "scenario-filler-green-creature",
        GreenCreature,
        Layout.Normal,
        [new Face(GreenCreature, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{G}", Power: new PrintedStat("1"), Toughness: new PrintedStat("1"), ColorIndicator: Color.Green)]);

    /// <summary>A basic land of one basic land type, CR 305.6: the supertype and the subtype, nothing printed besides.</summary>
    [RuleRef("305.6")]
    private static CardDefinition BasicOfType(string name, string oracleId, string landType) => new(
        oracleId,
        name,
        Layout.Normal,
        [new Face(name, FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic, [landType]))]);

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

    private static CardDefinition BoltDefinition { get; } = new(
        "scenario-filler-bolt",
        Bolt,
        Layout.Normal,
        [new Face(Bolt, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{R}", Abilities: [new SpellAbility(new DealDamage(new Fixed(1), new AnyTarget()))])]);

    private static CardDefinition SpellDefinition { get; } = new(
        "scenario-filler-spell",
        Spell,
        Layout.Normal,
        [new Face(Spell, FaceRole.Main, new TypeLine(CardType.Instant), ManaCost: "{1}")]);

    private static CardDefinition CheapCreatureDefinition { get; } = new(
        "scenario-filler-cheap-creature",
        CheapCreature,
        Layout.Normal,
        [new Face(CheapCreature, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{0}", Power: new PrintedStat("1"), Toughness: new PrintedStat("1"))]);

    private static CardDefinition CostlyCreatureDefinition { get; } = new(
        "scenario-filler-costly-creature",
        CostlyCreature,
        Layout.Normal,
        [new Face(CostlyCreature, FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: "{6}", Power: new PrintedStat("6"), Toughness: new PrintedStat("6"))]);

    private static CardDefinition SorceryDefinition { get; } = new(
        "scenario-filler-sorcery",
        Sorcery,
        Layout.Normal,
        [new Face(Sorcery, FaceRole.Main, new TypeLine(CardType.Sorcery), ManaCost: "{1}")]);
}
