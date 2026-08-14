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
    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public string? SetCode { get; init; }
    public string? Rarity { get; init; }
    public IReadOnlyDictionary<string, string> Legalities { get; init; } = new Dictionary<string, string>();
    public bool GameChanger { get; init; }

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


