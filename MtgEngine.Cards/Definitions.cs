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
    string? OracleText = null,
    IReadOnlyList<AlternativeCost>? AlternativeCosts = null)
{
    public IReadOnlyList<Ability> AbilityList => Abilities ?? [];
}

/// <summary>
/// A printed alternative cost, CR 118.9: "you may pay {B} rather than pay this spell's mana cost",
/// with the condition the printed line gates it on — "if a creature died this turn". The spell is
/// cast for <paramref name="Cost"/> instead of its mana cost, and only while
/// <paramref name="OnlyIf"/> holds as the cost is chosen (CR 601.2b, where one alternative cost is
/// chosen and CR 601.2f, where it is paid). Leave <paramref name="OnlyIf"/> null for an ungated one.
/// <para>
/// It belongs on the Face rather than on an ability because it is not a thing the card does: it is
/// an option offered as the spell is cast, before any ability of it exists. A face may print more
/// than one, and the caster picks at most one of them (CR 601.2b) — the list is the printed
/// alternatives, never a way of saying "and".
/// </para>
/// <para>
/// It is not <c>AdditionalCosts</c>, which is CR 118.8 and is paid on top of the mana cost; a
/// reading that puts an alternative cost there makes the spell cost both, which is a different card.
/// It is not <see cref="MayPay"/> either: that is a choice made as the spell resolves, and this one
/// is made as the spell is cast, before targets are legal. And it is not the play-permission flags
/// on <see cref="MayPlayFromZone"/>, which grant a cast from another zone; this is the printed line
/// on the card itself, whatever zone it is cast from.
/// </para>
/// <para>
/// <paramref name="WithoutPayingManaCost"/> is the printed "you may cast this spell without paying
/// its mana cost", which CR 118.9 names as an alternative cost in so many words. It is the one
/// spelling for that line: <paramref name="Cost"/> is then an empty <c>Costs</c> and nothing else,
/// and a <c>ManaCost</c> of "{0}" is refused (L050) — CR 118.9c says an alternative cost does not
/// change the spell's mana cost, so a reading that writes a cost of zero has invented a cost that
/// is paid instead of saying that none is. "If you control a commander" goes in
/// <paramref name="OnlyIf"/>, which CR 118.9b allows an alternative cost to carry.
/// </para>
/// </summary>
[RuleRef("118.9")]
public sealed record AlternativeCost(Cost Cost, Condition? OnlyIf = null, bool WithoutPayingManaCost = false);

/// <summary>The type line, CR 205: supertypes, types, subtypes.</summary>
[RuleRef("205")]
public sealed record TypeLine(CardType Types, Supertype Supertypes = Supertype.None, IReadOnlyList<string>? Subtypes = null)
{
    public IReadOnlyList<string> SubtypeList => Subtypes ?? [];
}
