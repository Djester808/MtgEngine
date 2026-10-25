namespace MtgEngine.Cards;

/// <summary>
/// One card as the engine sees it: its printed faces. Nothing about price, art or
/// set; those live in the Scryfall object the card came from.
/// </summary>
public sealed record CardDefinition(
    string OracleId,
    string Name,
    Layout Layout,
    IReadOnlyList<Face> Faces)
{
    /// <summary>Printed face data, independent of visibility or current evaluated characteristics.</summary>
    public Face GetFace(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Faces.Count);
        return Faces[index];
    }

    /// <summary>Structural requirements for creating a card, not validation of executable abilities.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OracleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Faces);
        if (Faces.Count == 0 || Faces.Any(face => face is null || string.IsNullOrWhiteSpace(face.Name) || face.Types is null))
            throw new ArgumentException("A card must have named faces with printed type data.", nameof(Faces));
    }
}

/// <summary>
/// One face of a card: the printed characteristics, CR 109.3, and rules text. Power and
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
    string? OracleText = null);

/// <summary>The type line, CR 205: supertypes, types, subtypes.</summary>
[RuleRef("205")]
public sealed record TypeLine(CardType Types, Supertype Supertypes = Supertype.None, IReadOnlyList<string>? Subtypes = null)
{
    public IReadOnlyList<string> SubtypeList => Subtypes ?? [];
}
