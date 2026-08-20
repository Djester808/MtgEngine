using MtgEngine.Api.Cards;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Which cards the engine plays correctly with no card-specific code.
/// </summary>
/// <remarks>
/// The value of getting this line right is the difference between a pool of a couple of dozen
/// cards and one that can play a real deck. The risk of getting it wrong is worse than an
/// unimplemented card: a card the engine claims and plays wrong looks correct while quietly
/// changing the game.
/// </remarks>
public sealed class CardCoverageTests
{
    private static CardDefinition Card(
        string text, CardType types = CardType.Creature, KeywordAbility keywords = default) => new()
        {
            OracleId = "oracle-test",
            Name = "Test",
            CardTypes = types,
            OracleText = text,
            Keywords = keywords,
            Power = 2,
            Toughness = 2,
        };

    [Fact]
    public void A_vanilla_creature_needs_nothing()
    {
        Assert.True(CardCoverage.IsFullyCovered(Card(string.Empty)));
    }

    [Fact]
    public void A_basic_land_needs_nothing()
    {
        Assert.True(CardCoverage.IsFullyCovered(new CardDefinition
        {
            OracleId = "oracle-forest",
            Name = "Forest",
            CardTypes = CardType.Land,
            Supertypes = ["Basic"],
            Subtypes = ["Forest"],
        }));
    }

    [Fact]
    public void A_creature_whose_text_is_one_keyword_is_covered()
    {
        // Parsed onto the card and read by combat, so the rules already play it.
        Assert.True(CardCoverage.IsFullyCovered(Card("Flying", keywords: KeywordAbility.Flying)));
    }

    [Fact]
    public void Several_keywords_on_one_line_are_covered()
    {
        Assert.True(CardCoverage.IsFullyCovered(Card("Flying, vigilance")));
    }

    [Fact]
    public void Keywords_on_separate_lines_are_covered()
    {
        Assert.True(CardCoverage.IsFullyCovered(Card("Flying\nLifelink\nTrample")));
    }

    [Fact]
    public void Reminder_text_is_ignored()
    {
        // CR 207.2: reminder text is not rules, so it is never something to implement.
        Assert.True(CardCoverage.IsFullyCovered(
            Card("Deathtouch (Any amount of damage this deals to a creature is enough to destroy it.)")));
    }

    [Fact]
    public void A_keyword_the_engine_does_not_read_is_not_covered()
    {
        // The rule this file exists for. Ward is parsed onto the card and never acted on, so a
        // card with it would look right and play wrong — worse than refusing it.
        Assert.False(CardCoverage.IsFullyCovered(Card("Ward {2}")));
        Assert.False(CardCoverage.IsFullyCovered(Card("Protection from red")));
        Assert.DoesNotContain("ward", CardCoverage.Honoured);
        Assert.DoesNotContain("protection", CardCoverage.Honoured);
    }

    [Fact]
    public void Anything_with_real_rules_text_is_not_covered()
    {
        Assert.False(CardCoverage.IsFullyCovered(
            Card("When this creature enters, draw a card.")));
        Assert.False(CardCoverage.IsFullyCovered(
            Card("{T}: Add {G}.", CardType.Land)));
        Assert.False(CardCoverage.IsFullyCovered(
            Card("Other Elves you control get +1/+1.")));
    }

    [Fact]
    public void A_keyword_mixed_with_real_text_is_not_covered()
    {
        // The whole text has to be keywords; one real sentence is enough to need a definition.
        Assert.False(CardCoverage.IsFullyCovered(
            Card("Flying\nWhen this creature dies, draw a card.")));
    }

    [Fact]
    public void A_token_is_never_covered()
    {
        // A token is created by something; whatever creates it is what needs implementing.
        Assert.False(CardCoverage.IsFullyCovered(
            Card(string.Empty, CardType.Creature | CardType.Token)));
    }

    [Fact]
    public void An_empty_enchantment_is_not_covered()
    {
        // A permanent with no text and no body does nothing at all, which is a card the database
        // got wrong rather than one to play.
        Assert.False(CardCoverage.IsFullyCovered(Card(string.Empty, CardType.Enchantment)));
    }

    [Fact]
    public void The_keywords_a_card_uses_are_reported()
    {
        Assert.Equal(
            ["Flying", "lifelink"],
            CardCoverage.KeywordsOf(Card("Flying\nlifelink")));
    }

    [Fact]
    public void Every_honoured_keyword_is_one_the_engine_actually_reads()
    {
        // The negative control for the whole file. If a keyword is listed here and the engine
        // does not read it, cards carrying it get claimed and played wrong — so the list is
        // checked against the enum the engine branches on rather than trusted.
        var known = Enum.GetValues<KeywordAbility>()
            .Where(k => k != KeywordAbility.None)
            .Select(k => k.ToString().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var honoured in CardCoverage.Honoured)
        {
            var flattened = honoured.Replace(" ", string.Empty, StringComparison.Ordinal);
            Assert.Contains(flattened, known);
        }
    }
}
