using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Colour filtering for the search endpoint.
/// </summary>
/// <remarks>
/// These mirror <c>color-filter.spec.ts</c> in the client, because the same pip row filters
/// locally on the card grids and through this query on the search panel — a screen shows
/// both at once, so the two must agree. The rule is "within these colours": a card matches
/// when its whole colour identity fits inside the selection. It previously matched any card
/// that merely *contained* a selected colour, so picking Red returned every Boros, Grixis
/// and five-colour card, and nothing tested the matcher at all.
/// </remarks>
public class CardQueryColorTests
{
    private static bool Matches(string query, params ManaColor[] identity)
    {
        var (hasFilter, multicolor, colorless, colors) = CardQuery.ParseColors(query);
        Assert.True(hasFilter, $"'{query}' should have produced a colour filter");
        var card = new CardDefinition { OracleId = "o", Name = "Card", ColorIdentity = [.. identity] };
        return CardQuery.MatchesColor(card, multicolor, colorless, colors);
    }

    // ---- The reported bug ------------------------------------------------

    [Fact]
    public void OneColour_MeansMonoColoured_NotContainsThatColour()
    {
        Assert.True(Matches("c:r", ManaColor.Red));
        Assert.False(Matches("c:r", ManaColor.Red, ManaColor.White));
        Assert.False(Matches("c:r", ManaColor.Blue, ManaColor.Black, ManaColor.Red));
    }

    [Fact]
    public void TwoColours_MatchAnythingInsideThem_IncludingEachAlone()
    {
        Assert.True(Matches("c:rw", ManaColor.Red));
        Assert.True(Matches("c:rw", ManaColor.White));
        Assert.True(Matches("c:rw", ManaColor.Red, ManaColor.White));
        Assert.False(Matches("c:rw", ManaColor.Blue));
        Assert.False(Matches("c:rw", ManaColor.Red, ManaColor.Blue));
    }

    [Fact]
    public void AColourSelection_ExcludesColourlessCards()
    {
        // Otherwise c:r would return every artifact; 'c' is how a caller asks for those.
        Assert.False(Matches("c:r"));
    }

    // ---- The pseudo-pips -------------------------------------------------

    [Fact]
    public void Colourless_MatchesOnlyAnEmptyIdentity()
    {
        Assert.True(Matches("c:c"));
        Assert.False(Matches("c:c", ManaColor.Blue));
    }

    [Fact]
    public void Multicolour_IsACardinalityQuestion()
    {
        Assert.True(Matches("c:m", ManaColor.Green, ManaColor.White));
        Assert.False(Matches("c:m", ManaColor.Green));
        Assert.False(Matches("c:m"));
    }

    [Fact]
    public void MulticolourWithColours_MeansTheGoldOnesWithinThoseColours()
    {
        // The old parser only understood a whole token of "m", so this combination could
        // not even be expressed — the client discarded the colours before sending.
        Assert.True(Matches("c:rwm", ManaColor.Red, ManaColor.White));
        Assert.False(Matches("c:rwm", ManaColor.Red));
        Assert.False(Matches("c:rwm", ManaColor.Blue, ManaColor.Black));
    }

    [Fact]
    public void ColourlessWithColours_Widens_RatherThanReplaces()
    {
        Assert.True(Matches("c:rc"));
        Assert.True(Matches("c:rc", ManaColor.Red));
        Assert.False(Matches("c:rc", ManaColor.White));
    }

    // ---- Parsing ---------------------------------------------------------

    [Theory]
    [InlineData("c:r")]
    [InlineData("c:rw")]
    [InlineData("c:m")]
    [InlineData("c:c")]
    [InlineData("c:rwm")]
    public void RecognisedTokens_ProduceAFilter(string query) =>
        Assert.True(CardQuery.ParseColors(query).HasFilter);

    [Fact]
    public void PseudoPipsParseAsFlags_NotAsColours()
    {
        var multi = CardQuery.ParseColors("c:rwm");
        Assert.True(multi.Multicolor);
        Assert.False(multi.Colorless);
        Assert.Equal(2, multi.Colors.Count);

        var colourless = CardQuery.ParseColors("c:rc");
        Assert.True(colourless.Colorless);
        Assert.False(colourless.Multicolor);
        Assert.Single(colourless.Colors);
    }

    [Fact]
    public void AMidWordColonIsNotAColourToken() =>
        // Guards the same false positive CardQueryParseNameTests covers for the other tokens.
        Assert.False(CardQuery.ParseColors("epic:rg").HasFilter);

    [Fact]
    public void AnUnrecognisedLetterProducesNoFilter() =>
        Assert.False(CardQuery.ParseColors("c:xyz").HasFilter);
}
