using System.Text.Json;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Mapping;

/// <summary>
/// The single Scryfall-card-JSON → <see cref="PrintingDto"/> mapping. Both printing
/// sources go through here: the bulk-data index and the live API fallback.
/// </summary>
/// <remarks>
/// It was previously implemented twice, and the copies had drifted: the bulk path read
/// card_faces for double-faced cards while the live path did not, so the same oracle id
/// returned a printing with a back-face image and combined oracle text on a bulk hit,
/// and without them whenever the live fallback fired.
/// </remarks>
public static class ScryfallPrintingMapper
{
    /// <summary>
    /// A parsed printing plus the art crop, which the DTO does not carry — only the
    /// bulk index stores it (for fly-ghost art and cover suggestions).
    /// </summary>
    public readonly record struct ParsedPrinting(PrintingDto Dto, string? ImageUriArtCrop);

    /// <summary>
    /// Parses one Scryfall card object. Returns null for entries without a usable id or
    /// front image — an art-less printing is not worth offering in a printing picker.
    /// </summary>
    public static ParsedPrinting? Parse(JsonElement card)
    {
        var id = GetStr(card, "id");
        if (string.IsNullOrEmpty(id))
            return null;

        // Face elements — single-faced cards have none; DFCs carry text and (when the
        // top-level image_uris is absent) per-face images here.
        JsonElement? face0 = null, face1 = null;
        if (card.TryGetProperty("card_faces", out var faces)
            && faces.ValueKind == JsonValueKind.Array
            && faces.GetArrayLength() > 0)
        {
            face0 = faces[0];
            if (faces.GetArrayLength() > 1)
                face1 = faces[1];
        }

        // Images: top-level image_uris, else the front face's.
        string? imgSmall = null, imgNormal = null, imgLarge = null, imgArtCrop = null, imgNormalBack = null;
        var hasTopImages = card.TryGetProperty("image_uris", out var topImgs);
        JsonElement? imgSource = hasTopImages
            ? topImgs
            : face0 is not null && face0.Value.TryGetProperty("image_uris", out var faceImgs)
                ? faceImgs
                : null;
        if (imgSource is not null)
        {
            imgSmall = GetStr(imgSource, "small");
            imgNormal = GetStr(imgSource, "normal");
            imgLarge = GetStr(imgSource, "large");
            imgArtCrop = GetStr(imgSource, "art_crop");
        }
        if (!hasTopImages
            && face1 is not null
            && face1.Value.TryGetProperty("image_uris", out var backImgs)
            && backImgs.TryGetProperty("normal", out var backNormal))
        {
            imgNormalBack = backNormal.GetString();
        }

        if (imgNormal is null)
            return null;

        var artist = GetStr(card, "artist") ?? GetStr(face0, "artist");
        var flavorText = GetStr(card, "flavor_text") ?? GetStr(face0, "flavor_text");
        var manaCost = GetStr(card, "mana_cost") ?? GetStr(face0, "mana_cost");

        // For DFCs, combine both faces with the same separator CardParser uses so the
        // modal can split them when showing the flipped side's oracle text.
        var oracleText = GetStr(card, "oracle_text");
        if (oracleText is null)
        {
            var f0Text = GetStr(face0, "oracle_text") ?? "";
            var f1Text = GetStr(face1, "oracle_text") ?? "";
            oracleText = f0Text.Length > 0 && f1Text.Length > 0
                ? f0Text + "\n//\n" + f1Text
                : f0Text.Length > 0 ? f0Text : null;
        }

        var dto = new PrintingDto
        {
            ScryfallId = id,
            SetCode = GetStr(card, "set") ?? "",
            SetName = GetStr(card, "set_name") ?? "",
            CollectorNumber = GetStr(card, "collector_number"),
            ImageUriSmall = imgSmall,
            ImageUriNormal = imgNormal,
            ImageUriLarge = imgLarge,
            ImageUriNormalBack = imgNormalBack,
            Artist = artist,
            OracleText = oracleText,
            FlavorText = flavorText,
            ManaCost = manaCost,
        };

        return new ParsedPrinting(dto, imgArtCrop);
    }

    private static string? GetStr(JsonElement? el, string prop)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object)
            return null;
        return el.Value.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }
}
