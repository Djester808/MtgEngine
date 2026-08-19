using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The tribe hint the build prompt carries.
/// </summary>
/// <remarks>
/// A Wolf commander produced a deck with barely a Wolf in it, and this is why. The hint
/// found cards two ways — creature type, or the tribe named in rules text — and the second
/// test was a plain substring. One of that commander's tribes was <c>Battle</c>, a real
/// card type its text genuinely referenced, and "Battle" is inside "battlefield".
/// <para>
/// Measured against the real card data for that commander: 12,029 legal cards, 69 with the
/// creature type, and 1,406 more matched on text — 1,349 of which said nothing but
/// "battlefield". Sorting the two kinds together by name and truncating at 220 then kept
/// 204 of the noise and dropped 56 of the 69 real members, under a heading telling the
/// model to draw the deck's core from the list.
/// </para>
/// </remarks>
public sealed class TribeHintTests
{
    private static bool Mentions(string text, params string[] tribes) =>
        TribeText.Mentions(TribeText.MentionPatterns(tribes), text);

    [Fact]
    public void Battlefield_is_not_a_battle()
    {
        // The whole bug in one line.
        Assert.False(Mentions("When this creature enters the battlefield, draw a card.", "Battle"));
    }

    [Theory]
    [InlineData("Put target creature onto the battlefield.")]
    [InlineData("Return all creature cards from your graveyard to the battlefield.")]
    [InlineData("Whenever a creature enters the battlefield under your control...")]
    public void Ordinary_rules_boilerplate_does_not_name_a_tribe(string text)
    {
        Assert.False(Mentions(text, "Battle"));
    }

    [Theory]
    [InlineData("Whenever a battle you control is defeated, draw a card.")]
    [InlineData("Protect target battle.")]
    [InlineData("If a triggered ability of another Wolf or battle you control triggers...")]
    public void A_card_that_really_names_the_tribe_still_matches(string text)
    {
        Assert.True(Mentions(text, "Wolf", "Battle"));
    }

    [Fact]
    public void The_plural_counts_as_naming_the_tribe()
    {
        // Rules text says "Wolves" far more often than "Wolf".
        Assert.True(Mentions("Create two 2/2 green Wolf creature tokens.", "Wolf"));
        Assert.True(Mentions("This creature can't attack unless you control two other Wolves.", "Wolf"));
    }

    [Fact]
    public void Matching_ignores_case_but_not_word_boundaries()
    {
        Assert.True(Mentions("target WOLF you control", "Wolf"));
        // "Wolfir" and "Werewolf" are different words; only the second shares the type, and
        // it is caught by the creature-type check rather than by this one.
        Assert.False(Mentions("Wolfir Silverheart enters.", "Wolf"));
    }

    [Fact]
    public void A_tribe_name_that_cannot_compile_is_simply_not_searched_for()
    {
        // Tribe names come from the analysis pass, which is model output. A pattern that
        // will not build must not take the build down with it.
        var patterns = TribeText.MentionPatterns(["", "   ", "Wolf"]);

        Assert.Single(patterns);
        Assert.True(TribeText.Mentions(patterns, "Create a Wolf token."));
    }
}
