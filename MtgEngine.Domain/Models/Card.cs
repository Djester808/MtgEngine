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

    /// <summary>
    /// The keywords Scryfall lists for this card, spelled the way it spells them
    /// ("First strike", "Ward", "Cycling").
    /// </summary>
    /// <remarks>
    /// Carried as the printed strings rather than as a flags enum. The enum this replaced
    /// recognised 34 of the 890 keyword spellings in the bulk file and silently dropped the
    /// rest, so 12,399 of 38,633 cards reached the client and the AI's fact sheet missing at
    /// least one keyword — Ward, Cycling, Flashback, Kicker and Landfall among them.
    /// </remarks>
    public IReadOnlyList<string> Keywords { get; init; } = [];

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

    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public int? EdhrecRank { get; init; }
    public string? CollectorNumber { get; init; }
    public IReadOnlyList<string>? ProducedMana { get; init; }
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
    public bool IsBattle => CardTypes.HasFlag(CardType.Battle);
    public bool IsNonland => !IsLand;
}
