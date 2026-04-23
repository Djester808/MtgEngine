using System.Text.Json;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Services;

/// <summary>
/// Parses Scryfall JSON card objects into CardDefinition domain models.
/// Shared between ScryfallService (individual API lookups) and BulkDataService.
/// </summary>
internal static class CardParser
{
    public static CardDefinition? Parse(JsonElement json)
    {
        try
        {
            var oracleId = json.GetProperty("oracle_id").GetString() ?? Guid.NewGuid().ToString();
            var name     = json.GetProperty("name").GetString() ?? "";
            var typeLine = json.GetProperty("type_line").GetString() ?? "";
            var oracle   = json.TryGetProperty("oracle_text", out var ot) ? ot.GetString() ?? "" : "";
            var mc       = json.TryGetProperty("mana_cost", out var mcEl)
                           ? ParseManaCost(mcEl.GetString() ?? "")
                           : ManaCost.Zero;

            int? power = null, toughness = null, loyalty = null;
            if (json.TryGetProperty("power",     out var pw) && int.TryParse(pw.GetString(), out var p)) power     = p;
            if (json.TryGetProperty("toughness", out var th) && int.TryParse(th.GetString(), out var t)) toughness = t;
            if (json.TryGetProperty("loyalty",   out var lo) && int.TryParse(lo.GetString(), out var l)) loyalty   = l;

            string? imgNormal = null, imgSmall = null, imgArtCrop = null;
            if (json.TryGetProperty("image_uris", out var imgs))
            {
                if (imgs.TryGetProperty("normal",   out var n)) imgNormal  = n.GetString();
                if (imgs.TryGetProperty("small",    out var s)) imgSmall   = s.GetString();
                if (imgs.TryGetProperty("art_crop", out var a)) imgArtCrop = a.GetString();
            }
            else if (json.TryGetProperty("card_faces", out var faces) && faces.GetArrayLength() > 0)
            {
                // DFC: images live on individual faces
                var face = faces[0];
                if (face.TryGetProperty("image_uris", out var fi))
                {
                    if (fi.TryGetProperty("normal",   out var n)) imgNormal  = n.GetString();
                    if (fi.TryGetProperty("small",    out var s)) imgSmall   = s.GetString();
                    if (fi.TryGetProperty("art_crop", out var a)) imgArtCrop = a.GetString();
                }
            }

            var flavorText = json.TryGetProperty("flavor_text", out var ft) ? ft.GetString() : null;
            var artist     = json.TryGetProperty("artist",       out var ar) ? ar.GetString() : null;
            var setCode    = json.TryGetProperty("set",          out var sc) ? sc.GetString() : null;

            var cardTypes  = ParseCardTypes(typeLine);
            var subtypes   = ParseSubtypes(typeLine);
            var supertypes = ParseSupertypes(typeLine);
            var keywords   = ParseKeywords(json);
            var colorId    = ParseColorIdentity(json);
            var speed      = cardTypes.HasFlag(CardType.Instant) || keywords.HasFlag(KeywordAbility.Flash)
                ? SpeedRestriction.Instant
                : SpeedRestriction.Sorcery;

            return new CardDefinition
            {
                OracleId        = oracleId,
                Name            = name,
                ManaCost        = mc,
                CardTypes       = cardTypes,
                Subtypes        = subtypes,
                Supertypes      = supertypes,
                OracleText      = oracle,
                Power           = power,
                Toughness       = toughness,
                StartingLoyalty = loyalty,
                Keywords        = keywords,
                ColorIdentity   = colorId,
                ImageUriNormal  = imgNormal,
                ImageUriSmall   = imgSmall,
                ImageUriArtCrop = imgArtCrop,
                CastingSpeed    = speed,
                FlavorText      = flavorText,
                Artist          = artist,
                SetCode         = setCode,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a CardDefinition but overrides the image URIs and set code for a specific printing.
    /// </summary>
    public static CardDefinition WithPrinting(CardDefinition oracle, string? imgNormal, string? imgSmall, string? imgArtCrop, string? setCode) =>
        new()
        {
            OracleId        = oracle.OracleId,
            Name            = oracle.Name,
            ManaCost        = oracle.ManaCost,
            CardTypes       = oracle.CardTypes,
            Subtypes        = oracle.Subtypes,
            Supertypes      = oracle.Supertypes,
            OracleText      = oracle.OracleText,
            Power           = oracle.Power,
            Toughness       = oracle.Toughness,
            StartingLoyalty = oracle.StartingLoyalty,
            Keywords        = oracle.Keywords,
            CastingSpeed    = oracle.CastingSpeed,
            ColorIdentity   = oracle.ColorIdentity,
            FlavorText      = oracle.FlavorText,
            Artist          = oracle.Artist,
            ImageUriNormal  = imgNormal  ?? oracle.ImageUriNormal,
            ImageUriSmall   = imgSmall   ?? oracle.ImageUriSmall,
            ImageUriArtCrop = imgArtCrop ?? oracle.ImageUriArtCrop,
            SetCode         = setCode    ?? oracle.SetCode,
        };

    // ---- Parsers -------------------------------------------------------

    private static ManaCost ParseManaCost(string cost)
    {
        var cleaned = cost.Replace("{", "").Replace("}", "").Replace("X", "");
        try { return ManaCost.Parse(cleaned); }
        catch { return ManaCost.Zero; }
    }

    private static CardType ParseCardTypes(string typeLine)
    {
        var flags = CardType.None;
        if (typeLine.Contains("Creature"))     flags |= CardType.Creature;
        if (typeLine.Contains("Instant"))      flags |= CardType.Instant;
        if (typeLine.Contains("Sorcery"))      flags |= CardType.Sorcery;
        if (typeLine.Contains("Enchantment"))  flags |= CardType.Enchantment;
        if (typeLine.Contains("Artifact"))     flags |= CardType.Artifact;
        if (typeLine.Contains("Land"))         flags |= CardType.Land;
        if (typeLine.Contains("Planeswalker")) flags |= CardType.Planeswalker;
        return flags == CardType.None ? CardType.Sorcery : flags;
    }

    private static IReadOnlyList<string> ParseSubtypes(string typeLine)
    {
        var idx = typeLine.IndexOf('—');
        if (idx < 0) return [];
        return typeLine[(idx + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> ParseSupertypes(string typeLine)
    {
        var supers = new[] { "Legendary", "Basic", "Snow", "World" };
        return supers.Where(typeLine.Contains).ToArray();
    }

    private static KeywordAbility ParseKeywords(JsonElement json)
    {
        var flags = KeywordAbility.None;
        if (!json.TryGetProperty("keywords", out var kwArr)) return flags;

        foreach (var kw in kwArr.EnumerateArray())
        {
            flags |= kw.GetString() switch
            {
                "Flying"         => KeywordAbility.Flying,
                "Reach"          => KeywordAbility.Reach,
                "First strike"   => KeywordAbility.FirstStrike,
                "Double strike"  => KeywordAbility.DoubleStrike,
                "Trample"        => KeywordAbility.Trample,
                "Deathtouch"     => KeywordAbility.Deathtouch,
                "Lifelink"       => KeywordAbility.Lifelink,
                "Vigilance"      => KeywordAbility.Vigilance,
                "Haste"          => KeywordAbility.Haste,
                "Hexproof"       => KeywordAbility.Hexproof,
                "Indestructible" => KeywordAbility.Indestructible,
                "Menace"         => KeywordAbility.Menace,
                "Flash"          => KeywordAbility.Flash,
                "Shroud"         => KeywordAbility.Shroud,
                _                => KeywordAbility.None,
            };
        }
        return flags;
    }

    private static IReadOnlyList<ManaColor> ParseColorIdentity(JsonElement json)
    {
        if (!json.TryGetProperty("color_identity", out var ci)) return [];
        return ci.EnumerateArray()
            .Select(c => c.GetString() switch
            {
                "W" => ManaColor.White, "U" => ManaColor.Blue, "B" => ManaColor.Black,
                "R" => ManaColor.Red,   "G" => ManaColor.Green, _ => ManaColor.Colorless,
            })
            .ToArray();
    }
}
