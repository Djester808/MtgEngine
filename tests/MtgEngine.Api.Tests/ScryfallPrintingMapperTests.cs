using System.Text.Json;
using MtgEngine.Api.Mapping;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The mapper is the single Scryfall-JSON → PrintingDto path for both the bulk index
/// and the live API fallback; these tests pin the DFC handling that the live path
/// used to lose (back-face image, combined oracle text).
/// </summary>
public class ScryfallPrintingMapperTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SingleFacedCard_MapsAllFields()
    {
        var parsed = ScryfallPrintingMapper.Parse(Json("""
        {
          "id": "scry-1",
          "set": "fdn",
          "set_name": "Foundations",
          "collector_number": "204",
          "artist": "Lie Setiawan",
          "oracle_text": "Tap: Create goblins.",
          "flavor_text": "Legitimate business.",
          "mana_cost": "{2}{R}{R}",
          "tcgplayer_id": 235542,
          "prices": { "usd": "3.47", "usd_foil": "12.90", "usd_etched": null, "eur": "2.10", "eur_foil": null, "tix": "0.02" },
          "image_uris": {
            "small": "http://img/small.jpg",
            "normal": "http://img/normal.jpg",
            "large": "http://img/large.jpg",
            "art_crop": "http://img/art.jpg"
          }
        }
        """));

        Assert.NotNull(parsed);
        var dto = parsed.Value.Dto;
        Assert.Equal("scry-1", dto.ScryfallId);
        Assert.Equal("fdn", dto.SetCode);
        Assert.Equal("Foundations", dto.SetName);
        Assert.Equal("204", dto.CollectorNumber);
        Assert.Equal("http://img/small.jpg", dto.ImageUriSmall);
        Assert.Equal("http://img/normal.jpg", dto.ImageUriNormal);
        Assert.Equal("http://img/large.jpg", dto.ImageUriLarge);
        Assert.Null(dto.ImageUriNormalBack);
        Assert.Equal("Lie Setiawan", dto.Artist);
        Assert.Equal("Tap: Create goblins.", dto.OracleText);
        Assert.Equal("Legitimate business.", dto.FlavorText);
        Assert.Equal("{2}{R}{R}", dto.ManaCost);
        Assert.Equal("http://img/art.jpg", parsed.Value.ImageUriArtCrop);
        Assert.NotNull(dto.Prices);
        Assert.Equal(3.47m, dto.Prices!.Usd);
        Assert.Equal(12.90m, dto.Prices.UsdFoil);
        Assert.Null(dto.Prices.UsdEtched);
        Assert.Equal(2.10m, dto.Prices.Eur);
        Assert.Null(dto.Prices.EurFoil);
        Assert.Equal(0.02m, dto.Prices.Tix);
        Assert.Equal(235542, dto.Prices.TcgplayerId);
        Assert.Equal(3.47m, parsed.Value.Prices.Usd);
    }

    [Fact]
    public void PricelessPrinting_MapsNullPricesNotAnEmptyObject()
    {
        var parsed = ScryfallPrintingMapper.Parse(Json("""
        {
          "id": "scry-3",
          "set": "fdn",
          "set_name": "Foundations",
          "prices": { "usd": null, "usd_foil": null, "usd_etched": null, "eur": null, "eur_foil": null, "tix": null },
          "image_uris": { "normal": "http://img/n.jpg" }
        }
        """));

        Assert.NotNull(parsed);
        Assert.Null(parsed.Value.Dto.Prices);
    }

    [Fact]
    public void DoubleFacedCard_TakesFrontImagesBackImageAndCombinedText()
    {
        // Transform cards carry no top-level image_uris or oracle_text — everything
        // lives on card_faces. This is exactly what the live path used to drop.
        var parsed = ScryfallPrintingMapper.Parse(Json("""
        {
          "id": "scry-dfc",
          "set": "vow",
          "set_name": "Crimson Vow",
          "collector_number": "42",
          "card_faces": [
            {
              "artist": "Front Artist",
              "mana_cost": "{1}{G}",
              "oracle_text": "Front face text.",
              "image_uris": { "small": "http://f/s.jpg", "normal": "http://f/n.jpg", "large": "http://f/l.jpg", "art_crop": "http://f/a.jpg" }
            },
            {
              "oracle_text": "Back face text.",
              "image_uris": { "normal": "http://b/n.jpg" }
            }
          ]
        }
        """));

        Assert.NotNull(parsed);
        var dto = parsed.Value.Dto;
        Assert.Equal("http://f/n.jpg", dto.ImageUriNormal);
        Assert.Equal("http://b/n.jpg", dto.ImageUriNormalBack);
        Assert.Equal("Front face text.\n//\nBack face text.", dto.OracleText);
        Assert.Equal("Front Artist", dto.Artist);
        Assert.Equal("{1}{G}", dto.ManaCost);
        Assert.Equal("http://f/a.jpg", parsed.Value.ImageUriArtCrop);
    }

    [Fact]
    public void AdventureStyleCard_TopLevelImagesWithTextFaces_CombinesFaceText()
    {
        // Split/adventure layouts keep one shared image but put text on faces.
        var parsed = ScryfallPrintingMapper.Parse(Json("""
        {
          "id": "scry-adv",
          "set": "eld",
          "set_name": "Throne of Eldraine",
          "image_uris": { "normal": "http://one/n.jpg" },
          "card_faces": [
            { "oracle_text": "Creature half." },
            { "oracle_text": "Adventure half." }
          ]
        }
        """));

        Assert.NotNull(parsed);
        Assert.Equal("Creature half.\n//\nAdventure half.", parsed.Value.Dto.OracleText);
        // The shared image is the whole card — there is no separate back face.
        Assert.Null(parsed.Value.Dto.ImageUriNormalBack);
    }

    [Fact]
    public void ArtlessPrinting_IsSkipped()
    {
        Assert.Null(ScryfallPrintingMapper.Parse(Json("""
        { "id": "scry-2", "set": "unk", "set_name": "Unknown" }
        """)));
    }

    [Fact]
    public void MissingId_IsSkipped()
    {
        Assert.Null(ScryfallPrintingMapper.Parse(Json("""
        { "set": "fdn", "image_uris": { "normal": "http://img/n.jpg" } }
        """)));
    }
}
