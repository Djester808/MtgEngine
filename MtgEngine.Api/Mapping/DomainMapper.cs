using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Mapping;

public static class DomainMapper
{
    public static CardDto ToDto(Card card) => ToDto(card.Definition, card.CardId, card.OwnerId);

    public static CardDto ToDto(CardDefinition def, Guid cardId, Guid ownerId) => new()
    {
        CardId = cardId.ToString(),
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
        OwnerId = ownerId.ToString(),
        FlavorText = def.FlavorText,
        Artist = def.Artist,
        SetCode = def.SetCode,
        Rarity = def.Rarity,
        Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger = def.GameChanger,
    };

    public static ManaColorDto ToDto(ManaColor c) => c switch
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
        return result.ToArray();
    }
}
