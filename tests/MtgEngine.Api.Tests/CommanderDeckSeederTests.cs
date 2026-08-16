using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The seeder's deck-count taper. Everything else it does is network I/O against EDHREC and
/// Scryfall; this is the part with a decision in it.
/// </summary>
public class CommanderDeckSeederTests
{
    [Fact]
    public void AskingForOneDeckEach_ProducesExactlyOne()
    {
        // The floor used to be a flat Math.Max(3, …), so "20 commanders, 1 deck each"
        // quietly seeded 60 decks.
        for (var rank = 0; rank < 20; rank++)
            Assert.Equal(1, CommanderDeckSeeder.DeckCountForRank(rank, 20, 1));
    }

    [Fact]
    public void AskingForTwoDecksEach_NeverExceedsTheRequest()
    {
        for (var rank = 0; rank < 20; rank++)
        {
            var n = CommanderDeckSeeder.DeckCountForRank(rank, 20, 2);
            Assert.InRange(n, 1, 2);
        }
    }

    [Fact]
    public void OnALargeRun_TheTopCommanderGetsTheFullCount()
    {
        Assert.Equal(10, CommanderDeckSeeder.DeckCountForRank(0, 50, 10));
    }

    [Fact]
    public void OnALargeRun_TheTaperHalvesByTheEndButNeverDropsBelowThree()
    {
        var last = CommanderDeckSeeder.DeckCountForRank(49, 50, 10);
        Assert.Equal(5, last);

        // A long tail with a small request still bottoms out at the floor, not at zero.
        Assert.Equal(3, CommanderDeckSeeder.DeckCountForRank(199, 200, 4));
    }

    [Fact]
    public void ASingleCommander_DoesNotDivideByZero()
    {
        Assert.Equal(1, CommanderDeckSeeder.DeckCountForRank(0, 1, 1));
        Assert.Equal(10, CommanderDeckSeeder.DeckCountForRank(0, 1, 10));
    }

    // ---- EDHREC slugs ---------------------------------------------------

    [Theory]
    [InlineData("Atraxa, Praetor's Voice", "atraxa-praetors-voice")]
    [InlineData("Syr Konrad, the Grim", "syr-konrad-the-grim")]
    [InlineData("Six", "six")]
    public void Slug_StripsPunctuationAndSpaces(string name, string expected) =>
        Assert.Equal(expected, CommanderDeckSeeder.ToEdhrecSlug(name));

    [Theory]
    [InlineData("Birgi, God of Storytelling // Harnfel, Horn of Bounty", "birgi-god-of-storytelling")]
    [InlineData("Brallin, Skyshark Rider // Shabraz, the Skyshark", "brallin-skyshark-rider")]
    public void Slug_UsesOnlyTheFrontFaceOfADoubleFacedCard(string name, string expected)
    {
        // EDHREC 403s the combined "front-back" slug, which the seeder read as an empty
        // pool and turned into a silent skip — one commander short on every run that
        // happened to include a DFC.
        Assert.Equal(expected, CommanderDeckSeeder.ToEdhrecSlug(name));
    }
}
