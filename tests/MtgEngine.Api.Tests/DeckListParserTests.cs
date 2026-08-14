using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The plain-text importer takes arbitrary pasted input, so the parser has to
/// survive hostile quantities without a 500 and without inventing card names.
/// </summary>
public class DeckListParserTests
{
    [Theory]
    [InlineData("4 Lightning Bolt", 4)]
    [InlineData("4x Lightning Bolt", 4)]
    [InlineData("Lightning Bolt", 1)]
    [InlineData("0 Lightning Bolt", 1)]    // zero clamps up — a listed card exists at least once
    [InlineData("500 Lightning Bolt", 99)] // absurd counts clamp to the per-line cap
    public void Quantities_ParseAndClamp(string line, int expectedQty)
    {
        var result = DeckListParser.Parse(line);
        var (qty, name) = Assert.Single(result.Cards);
        Assert.Equal(expectedQty, qty);
        Assert.Equal("Lightning Bolt", name);
    }

    [Fact]
    public void QuantityBeyondInt_ClampsInsteadOfOverflowing()
    {
        // int.Parse on this used to throw OverflowException → 500 for the whole import.
        var result = DeckListParser.Parse("99999999999 Sol Ring");
        var (qty, name) = Assert.Single(result.Cards);
        Assert.Equal(DeckListParser.MaxQuantityPerLine, qty);
        Assert.Equal("Sol Ring", name);
    }

    [Fact]
    public void CommanderSection_TagsTheFirstCard()
    {
        var result = DeckListParser.Parse("Commander\n1 Krenko, Mob Boss\n\nDeck\n40 Mountain");
        Assert.Equal("Krenko, Mob Boss", result.CommanderName);
        Assert.Equal("commander", result.DetectedFormat);
        Assert.Equal(2, result.Cards.Count);
    }

    [Fact]
    public void SetNotation_And_Comments_AreStripped()
    {
        var result = DeckListParser.Parse("// my deck\n3 Shock (M21) 159\n# note");
        var (qty, name) = Assert.Single(result.Cards);
        Assert.Equal(3, qty);
        Assert.Equal("Shock", name);
    }
}

/// <summary>
/// Board is a free string on the wire; everything unrecognised lands in "main"
/// so casing or a bogus value can never strand cards on an invisible board.
/// </summary>
public class CollectionBoardTests
{
    [Theory]
    [InlineData(null, "main")]
    [InlineData("", "main")]
    [InlineData("main", "main")]
    [InlineData("Main", "main")]
    [InlineData("  SIDE  ", "side")]
    [InlineData("maybe", "maybe")]
    [InlineData("sideboard", "main")] // not in the whitelist → main
    public void NormalizeBoard_WhitelistsAndDefaults(string? input, string expected) =>
        Assert.Equal(expected, CollectionService.NormalizeBoard(input));
}
