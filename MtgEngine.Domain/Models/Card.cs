using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Domain.Models;

/// <summary>
/// Immutable oracle definition of a card. Shared across all copies.
/// Think of this as the card's "type" -- loaded once from Scryfall.
/// </summary>
public sealed class CardDefinition
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ManaCost ManaCost { get; init; } = ManaCost.Zero;
    /// <summary>Raw Scryfall mana cost string e.g. "{2}{W}{B}". Used for display only.</summary>
    public string ManaCostRaw { get; init; } = string.Empty;
    /// <summary>Authoritative mana value (CMC) from Scryfall's cmc field. Use this for filtering.</summary>
    public int Cmc { get; init; }
    public CardType CardTypes { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = [];
    public IReadOnlyList<string> Supertypes { get; init; } = [];
    public string OracleText { get; init; } = string.Empty;
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public int? StartingLoyalty { get; init; }
    public KeywordAbility Keywords { get; init; }

    // Scryfall image URIs and metadata -- populated by ScryfallService
    public string? ImageUriNormal { get; init; }
    public string? ImageUriLarge { get; init; }
    public string? ImageUriNormalBack { get; init; }
    public string? ImageUriSmall { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public IReadOnlyList<ManaColor> ColorIdentity { get; init; } = [];

    /// <summary>
    /// The colours the card <em>is</em> (CR 202.2), which is not the same list as
    /// <see cref="ColorIdentity"/> (CR 903.4).
    /// </summary>
    /// <remarks>
    /// A card's colour comes from its mana cost and colour indicator; its identity also counts
    /// the mana symbols in its rules text, and a devoid card's identity keeps colours the card
    /// itself does not have. The two lists differ on 1,158 nonland cards in the corpus - Bosh,
    /// Iron Golem is a colourless spell with a red identity, and Wasteland Strangler is a
    /// colourless spell with a black one - so a rules question about colour answered from the
    /// identity is wrong about every one of them.
    /// </remarks>
    public IReadOnlyList<ManaColor> Colors { get; init; } = [];

    /// <summary>
    /// The faces this card has, when it has more than one set of characteristics.
    /// </summary>
    /// <remarks>
    /// Empty for an ordinary card, which is the overwhelming majority. **853 playable cards are
    /// not ordinary**: every transform, adventure, split, modal double-faced, prepared and flip
    /// card keeps its name, cost, type line, power, toughness and rules text on its faces, and a
    /// definition with one of each has nowhere to put the second set. Until this existed such a
    /// card arrived with both halves' text merged into one body and no way to tell which sentence
    /// belonged to which half.
    /// <para>
    /// The card's own top-level characteristics stay what they were - for a two-faced card they
    /// are the front face's, which is the face it is cast as - so nothing that reads a
    /// <c>CardDefinition</c> has to learn about faces to keep working.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CardFace> Faces { get; init; } = [];
    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public string? SetCode { get; init; }
    public string? Rarity { get; init; }
    public IReadOnlyDictionary<string, string> Legalities { get; init; } = new Dictionary<string, string>();
    public bool GameChanger { get; init; }
    /// <summary>Prices of the printing this definition currently reflects (per-printing, not per-oracle).</summary>
    public CardPrices Prices { get; init; } = CardPrices.None;

    public bool IsCreature => CardTypes.HasFlag(CardType.Creature);
    public bool IsInstant => CardTypes.HasFlag(CardType.Instant);
    public bool IsSorcery => CardTypes.HasFlag(CardType.Sorcery);
    public bool IsLand => CardTypes.HasFlag(CardType.Land);
    public bool IsEnchantment => CardTypes.HasFlag(CardType.Enchantment);
    public bool IsArtifact => CardTypes.HasFlag(CardType.Artifact);
    public bool IsPlaneswalker => CardTypes.HasFlag(CardType.Planeswalker);
    public bool IsNonland => !IsLand;
    public bool IsPermanentType => IsCreature || IsEnchantment || IsArtifact || IsLand || IsPlaneswalker;

    public bool HasKeyword(KeywordAbility kw) => Keywords.HasFlag(kw);

    /// <summary>Returns the basic land color this produces, if applicable.</summary>
    public ManaColor? BasicLandColor => Name switch
    {
        "Plains" => ManaColor.White,
        "Island" => ManaColor.Blue,
        "Swamp" => ManaColor.Black,
        "Mountain" => ManaColor.Red,
        "Forest" => ManaColor.Green,
        _ => null
    };
}

/// <summary>
/// One face of a card that has more than one (CR 712, 713, 715).
/// </summary>
/// <remarks>
/// A subset of <see cref="CardDefinition"/> rather than another one: a face has characteristics
/// but no oracle id, no prices and no images of its own worth carrying twice. What it needs is
/// what the compiler reads - the words, the cost, the types and the numbers.
/// </remarks>
public sealed record CardFace
{
    public string Name { get; init; } = string.Empty;

    public string ManaCostRaw { get; init; } = string.Empty;

    public string TypeLine { get; init; } = string.Empty;

    public CardType CardTypes { get; init; }

    public IReadOnlyList<string> Subtypes { get; init; } = [];

    public IReadOnlyList<string> Supertypes { get; init; } = [];

    public string OracleText { get; init; } = string.Empty;

    public int? Power { get; init; }

    public int? Toughness { get; init; }

    public IReadOnlyList<ManaColor> Colors { get; init; } = [];

    public KeywordAbility Keywords { get; init; }
}
