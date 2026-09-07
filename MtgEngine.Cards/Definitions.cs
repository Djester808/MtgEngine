namespace MtgEngine.Cards;

/// <summary>
/// One card as the engine sees it: its faces and what they do. Nothing about price, art or
/// set; those live in the Scryfall object the card came from.
/// </summary>
public sealed record CardDefinition(
    string OracleId,
    string Name,
    Layout Layout,
    IReadOnlyList<Face> Faces);

/// <summary>
/// One face of a card: the printed characteristics, CR 109.3, and its abilities. Power and
/// toughness stay as printed so "*" survives; the engine reads the number when there is one.
/// </summary>
[RuleRef("109.3")]
public sealed record Face(
    string Name,
    FaceRole Role,
    TypeLine Types,
    string? ManaCost = null,
    PrintedStat? Power = null,
    PrintedStat? Toughness = null,
    string? Loyalty = null,
    int? Defense = null,
    Color? ColorIndicator = null,
    IReadOnlyList<Ability>? Abilities = null,
    IReadOnlyList<Cost>? AdditionalCosts = null,
    Condition? CastOnlyIf = null,
    IReadOnlyList<ClassLevel>? ClassLevels = null,
    IReadOnlyList<LevelBand>? LevelBands = null,
    string? OracleText = null)
{
    public IReadOnlyList<Ability> AbilityList => Abilities ?? [];
}

/// <summary>The type line, CR 205: supertypes, types, subtypes.</summary>
[RuleRef("205")]
public sealed record TypeLine(CardType Types, Supertype Supertypes = Supertype.None, IReadOnlyList<string>? Subtypes = null)
{
    public IReadOnlyList<string> SubtypeList => Subtypes ?? [];
}
