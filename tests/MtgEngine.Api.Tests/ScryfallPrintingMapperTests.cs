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
