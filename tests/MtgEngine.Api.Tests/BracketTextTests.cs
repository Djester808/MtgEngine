using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The bracket and price text injected into the build prompt.
/// </summary>
/// <remarks>
/// These strings used to name roughly sixty cards, which the doctrine forbids twice: §1.4
/// says Game Changer membership "is supplied as a fact, never inferred", and §0.3 says "A
/// name is a lookup. A property is a reason. Only the reason transfers."
/// <para>
/// It was also simply wrong, which is what a hand-maintained list of names becomes. Sol
/// Ring was named as a Game Changer at brackets 2 and 3 and is not one — the card data
/// flags it false — so the most-played card in the format was talked out of every
/// mid-bracket deck by a sentence in a prompt, while the candidate pool went on offering
/// it. Nothing here needs to police Game Changers at all: the pool already excludes them
/// below bracket 4 on the data flag.
/// </para>
/// </remarks>
public sealed class BracketTextTests
{
    /// <summary>
    /// Cards the prose used to name. Not a general detector — a guard on the exact mistake.
    /// </summary>
    private static readonly string[] PreviouslyNamed =
    [
        "Sol Ring", "Mana Crypt", "Jeweled Lotus", "Rhystic Study", "Smothering Tithe",
        "Consecrated Sphinx", "Cyclonic Rift", "Demonic Tutor", "Vampiric Tutor",
        "Mystical Tutor", "Enlightened Tutor", "Worldly Tutor", "Doubling Season",
        "Parallel Lives", "Vorinclex", "Toxrill", "Elesh Norn", "Jin-Gitaxias",
        "Omniscience", "Tooth and Nail", "Cultivate", "Kodama's Reach", "Farseek",
        "Nature's Lore", "Arcane Signet", "Commander's Sphere", "Counterspell",
        "Swords to Plowshares", "Path to Exile", "Sylvan Library", "Fierce Guardianship",
        "Heroic Intervention", "Teferi's Protection", "Esper Sentinel", "Phyrexian Arena",
        "Read the Bones", "Reclamation Sage", "Divination", "Fellwar Stone", "Mana Vault",
        "Chrome Mox", "Mox Diamond", "Force of Will", "Deflecting Swat", "Blood Crypt",
        "Breeding Pool", "Scalding Tarn", "Verdant Catacombs", "Underground Sea",
        "Tropical Island", "Command Tower", "Evolving Wilds", "Terramorphic Expanse",
        "Wayfarer's Bauble", "Dimir Aqueduct",
    ];

    public static TheoryData<int> Brackets() => [1, 2, 3, 4, 5];

    [Theory]
    [MemberData(nameof(Brackets))]
    public void No_bracket_names_a_card(int bracket)
    {
        var text = AiBuildService.DescribeBracket(bracket);

        foreach (var card in PreviouslyNamed)
        {
            Assert.False(
                text.Contains(card, StringComparison.OrdinalIgnoreCase),
                $"Bracket {bracket} names \"{card}\". State the property instead (doctrine §0.3).");
        }
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("mid")]
    [InlineData("none")]
    public void No_price_tier_names_a_card(string tier)
    {
        var text = AiBuildService.DescribePrice(tier);

        foreach (var card in PreviouslyNamed)
        {
            Assert.False(
                text.Contains(card, StringComparison.OrdinalIgnoreCase),
                $"Price tier \"{tier}\" names \"{card}\". State the property instead (doctrine §0.3).");
        }
    }

    [Theory]
    [MemberData(nameof(Brackets))]
    public void Every_bracket_still_says_which_one_it_is(int bracket)
    {
        // The text is the model's only signal for how hard the deck should try; an empty
        // or unlabelled arm would pass the test above by saying nothing at all.
        var text = AiBuildService.DescribeBracket(bracket);

        Assert.Contains($"Bracket {bracket}", text, StringComparison.Ordinal);
        Assert.True(text.Length > 120, $"Bracket {bracket} text is too thin to steer anything.");
    }

    [Fact]
    public void An_unknown_bracket_still_describes_something()
    {
        Assert.False(string.IsNullOrWhiteSpace(AiBuildService.DescribeBracket(99)));
    }
}
