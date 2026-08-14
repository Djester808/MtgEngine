using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Mapping;

/// <summary>
/// The single card-to-DTO mapping. Every endpoint that returns a card goes through here.
/// </summary>
/// <remarks>
/// It was previously copied inline into five callers, which had already drifted: four of
/// them left <see cref="CardDto.ImageUriLarge"/> unset, so the same card carried a large
/// image URI when fetched from the cards endpoint and null when it arrived inside a
/// collection, a deck suggestion, a commander page or a forum post.
/// </remarks>
public static class DomainMapper
{
    /// <summary>
    /// For callers rendering an oracle card with no owned instance behind it. The oracle id
    /// stands in as the card id, and <see cref="CardDto.OwnerId"/> is left empty.
    /// </summary>
    public static CardDto ToDto(CardDefinition def) => ToDto(def, def.OracleId, string.Empty);

    private static CardDto ToDto(CardDefinition def, string cardId, string ownerId) => new()
    {
        CardId = cardId,
        OracleId = def.OracleId,
        Name = def.Name,
        ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        // Scryfall's cmc, not ManaCost.ManaValue: the parsed cost sums generic + coloured
        // pips, which reads X as 0 and counts hybrid pips twice. Cmc is authoritative --
        // see the remarks on CardDefinition.Cmc.
        ManaValue = def.Cmc,
        CardTypes = ToCardTypeDto(def.CardTypes),
        Subtypes = def.Subtypes.ToArray(),
        Supertypes = def.Supertypes.ToArray(),
        OracleText = def.OracleText,
        Power = def.Power,
        Toughness = def.Toughness,
        StartingLoyalty = def.StartingLoyalty,
        Keywords = def.Keywords.ToString().Split(',').Select(s => s.Trim()).Where(s => s != "None").ToArray(),
        ImageUriNormal = def.ImageUriNormal,
        ImageUriLarge = def.ImageUriLarge,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall = def.ImageUriSmall,
        ImageUriArtCrop = def.ImageUriArtCrop,
        ColorIdentity = def.ColorIdentity.Select(ToDto).ToArray(),
        OwnerId = ownerId,
        FlavorText = def.FlavorText,
        Artist = def.Artist,
        SetCode = def.SetCode,
        Rarity = def.Rarity,
        Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger = def.GameChanger,
    };

    /// <summary>
    /// The single collection-entry mapping — decks, collections, and forum posts all
    /// return their card rows through here.
    /// </summary>
    /// <remarks>
    /// Previously copied inline into six callers, which had drifted twice over: only the
    /// deck and forum copies normalized <see cref="CollectionCardDto.Board"/>, and two
    /// copies resolved the definition by oracle id even when the row pinned a printing.
    /// Resolve <paramref name="def"/> via
    /// <see cref="CardLookupExtensions.ResolveForEntryAsync"/> so the pinned printing's
    /// art wins on every endpoint.
    /// </remarks>
    public static CollectionCardDto ToDto(CollectionCard card, CardDefinition? def) => new()
    {
        Id = card.Id,
        OracleId = card.OracleId,
        ScryfallId = card.ScryfallId,
        Quantity = card.Quantity,
        QuantityFoil = card.QuantityFoil,
        Notes = card.Notes,
        Board = card.Board is "main" or "side" or "maybe" ? card.Board : "main",
        AddedAt = card.AddedAt,
        CardDetails = def is null ? null : ToDto(def),
    };

    private static ManaColorDto ToDto(ManaColor c) => c switch
    {
        ManaColor.White => ManaColorDto.W,
        ManaColor.Blue => ManaColorDto.U,
        ManaColor.Black => ManaColorDto.B,
        ManaColor.Red => ManaColorDto.R,
        ManaColor.Green => ManaColorDto.G,
        ManaColor.Colorless => ManaColorDto.C,
        _ => ManaColorDto.C
    };

    public static CardTypeDto[] ToCardTypeDto(CardType flags)
    {
        var result = new List<CardTypeDto>();
        if (flags.HasFlag(CardType.Creature))
            result.Add(CardTypeDto.Creature);
        if (flags.HasFlag(CardType.Instant))
            result.Add(CardTypeDto.Instant);
        if (flags.HasFlag(CardType.Sorcery))
            result.Add(CardTypeDto.Sorcery);
        if (flags.HasFlag(CardType.Enchantment))
            result.Add(CardTypeDto.Enchantment);
        if (flags.HasFlag(CardType.Artifact))
            result.Add(CardTypeDto.Artifact);
        if (flags.HasFlag(CardType.Land))
            result.Add(CardTypeDto.Land);
        if (flags.HasFlag(CardType.Planeswalker))
            result.Add(CardTypeDto.Planeswalker);
        if (flags.HasFlag(CardType.Battle))
            result.Add(CardTypeDto.Battle);
        if (flags.HasFlag(CardType.Tribal))
            result.Add(CardTypeDto.Tribal);
        if (flags.HasFlag(CardType.Token))
            result.Add(CardTypeDto.Token);
        if (flags.HasFlag(CardType.Other))
            result.Add(CardTypeDto.Other);
        return result.ToArray();
    }
}
