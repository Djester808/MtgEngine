using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Collector-number matching is what lets the service pin down a printing without
/// trusting the model's set-symbol guess, so the two sides must normalise identically.
/// </summary>
public class CardVisionCollectorNumberTests
{
    [Theory]
    // Model transcribes what is printed (zero-padded); Scryfall stores the canonical form.
    [InlineData("0082", "82")]
    [InlineData("082", "82")]
    [InlineData("82", "82")]
    [InlineData("0001", "1")]
    // Scryfall suffixes and prefixes for variants.
    [InlineData("82a", "82")]
    [InlineData("★82", "82")]
    [InlineData("82★", "82")]
    // Slash form, in case the model returns it unsplit.
    [InlineData("105/280", "105")]
    public void Normalises_ToComparableNumericCore(string input, string expected) =>
        Assert.Equal(expected, CardVisionService.NormalizeCollectorNumber(input));

    [Fact]
    public void ZeroPadded_MatchesCanonical_TheOriginalBug()
    {
        // Observed live: model returned "0082" while the printing was stored as "82",
        // so a raw string comparison silently found no match and the set was dropped.
        Assert.Equal(
            CardVisionService.NormalizeCollectorNumber("82"),
            CardVisionService.NormalizeCollectorNumber("0082"));
    }

    [Fact]
    public void AllZeroes_DoesNotBecomeEmpty() =>
        Assert.Equal("0", CardVisionService.NormalizeCollectorNumber("000"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void NonNumeric_ReturnsEmpty(string? input) =>
        Assert.Equal(string.Empty, CardVisionService.NormalizeCollectorNumber(input));

    [Fact]
    public void DifferentNumbers_DoNotCollide() =>
        Assert.NotEqual(
            CardVisionService.NormalizeCollectorNumber("82"),
            CardVisionService.NormalizeCollectorNumber("820"));
}
