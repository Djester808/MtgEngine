using System.Text.Json;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Prices come from Scryfall's <c>prices</c> object as decimal strings or null, per
/// printing. These pin the parse, the pinned-printing override in WithPrinting, and
/// the "no data → null DTO" contract the client relies on.
/// </summary>
public class CardPricesParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParsePrices_ReadsDecimalStringsInvariantOfCulture()
    {
        var prices = CardParser.ParsePrices(Json("""
        { "prices": { "usd": "1234.56", "usd_foil": "0.07", "usd_etched": "45.00", "eur": "999.99", "eur_foil": "1.00", "tix": "0.02" } }
        """));

        Assert.Equal(1234.56m, prices.Usd);
        Assert.Equal(0.07m, prices.UsdFoil);
        Assert.Equal(45.00m, prices.UsdEtched);
        Assert.Equal(999.99m, prices.Eur);
        Assert.Equal(1.00m, prices.EurFoil);
        Assert.Equal(0.02m, prices.Tix);
    }

    [Fact]
    public void ParsePrices_ReadsMarketplaceIdsFromTheRoot()
    {
        var prices = CardParser.ParsePrices(Json("""
        {
          "tcgplayer_id": 235542,
          "cardmarket_id": 573841,
          "mtgo_id": 67330,
          "prices": { "usd": "1.55" }
        }
        """));

        Assert.Equal(235542, prices.TcgplayerId);
        Assert.Equal(573841, prices.CardmarketId);
        Assert.Equal(67330, prices.MtgoId);
    }

    [Fact]
    public void ParsePrices_IdsWithoutAnyListedPrice_StillCarryTheIds()
    {
        // A brand-new printing can have marketplace pages before its first price sample;
        // the ids must survive so the client can still link to the listing.
        var prices = CardParser.ParsePrices(Json("""
        { "tcgplayer_id": 111, "prices": { "usd": null } }
        """));

        Assert.NotSame(CardPrices.None, prices);
        Assert.Equal(111, prices.TcgplayerId);
        Assert.Null(prices.Usd);
    }

    [Fact]
    public void ParsePrices_MissingObjectOrAllNulls_ReturnsSharedNone()
    {
        Assert.Same(CardPrices.None, CardParser.ParsePrices(Json("""{ "name": "No Prices" }""")));
        Assert.Same(CardPrices.None, CardParser.ParsePrices(Json("""
        { "prices": { "usd": null, "usd_foil": null, "usd_etched": null, "eur": null, "eur_foil": null, "tix": null } }
        """)));
    }

    [Fact]
    public void ParsePrices_UnparseableValue_BecomesNullNotZero()
    {
        var prices = CardParser.ParsePrices(Json("""
        { "prices": { "usd": "not-a-number", "eur": "2.50" } }
        """));

        Assert.Null(prices.Usd);
        Assert.Equal(2.50m, prices.Eur);
    }

    [Fact]
    public void Parse_CarriesPricesOntoTheDefinition()
    {
        var def = CardParser.Parse(Json("""
        {
          "oracle_id": "oracle-1",
          "name": "Sol Ring",
          "type_line": "Artifact",
          "prices": { "usd": "1.49", "usd_foil": "24.99" }
        }
        """));

        Assert.NotNull(def);
        Assert.Equal(1.49m, def!.Prices.Usd);
        Assert.Equal(24.99m, def.Prices.UsdFoil);
        Assert.Null(def.Prices.Eur);
    }

    [Fact]
    public void WithPrinting_ReplacesPricesWholesale()
    {
        var oracle = new CardDefinition
        {
            OracleId = "oracle-1",
            Name = "Sol Ring",
            Prices = new CardPrices { Usd = 1.49m, UsdFoil = 24.99m },
        };
        // An etched-only printing: no usd listing. Falling back per-field would wrongly
        // keep the oracle printing's usd price.
        var pinned = CardParser.WithPrinting(oracle, null, null, null, null, "sld",
            prices: new CardPrices { UsdEtched = 89.99m });

        Assert.Null(pinned.Prices.Usd);
        Assert.Null(pinned.Prices.UsdFoil);
        Assert.Equal(89.99m, pinned.Prices.UsdEtched);
    }

    [Fact]
    public void WithPrinting_NoPricesGiven_KeepsOraclePrices()
    {
        var oracle = new CardDefinition
        {
            OracleId = "oracle-1",
            Name = "Sol Ring",
            Prices = new CardPrices { Usd = 1.49m },
        };
        var reprinted = CardParser.WithPrinting(oracle, null, null, null, null, "fdn");

        Assert.Equal(1.49m, reprinted.Prices.Usd);
    }

    [Fact]
    public void DomainMapper_MapsPricesOntoCardDto()
    {
        var dto = DomainMapper.ToDto(new CardDefinition
        {
            OracleId = "oracle-1",
            Name = "Sol Ring",
            Prices = new CardPrices { Usd = 1.49m, Tix = 0.03m },
        });

        Assert.NotNull(dto.Prices);
        Assert.Equal(1.49m, dto.Prices!.Usd);
        Assert.Equal(0.03m, dto.Prices.Tix);
        Assert.Null(dto.Prices.UsdFoil);
    }

    [Fact]
    public void DomainMapper_NoPriceData_MapsNullEvenForAFreshAllNullInstance()
    {
        // Value equality: an all-null instance parsed from JSON must map like None.
        var dto = DomainMapper.ToDto(new CardDefinition
        {
            OracleId = "oracle-1",
            Name = "Priceless",
            Prices = new CardPrices(),
        });

        Assert.Null(dto.Prices);
    }
}
