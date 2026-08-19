using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The price tiers, which are enforced by the candidate pool rather than asked for in prose.
/// </summary>
/// <remarks>
/// A price cap has to be a filter. Prices are not among the facts the model is given —
/// doctrine §0.1 supplies structured fields only — so telling it to keep cards under three
/// dollars asked it to recall a market it cannot see, which §0.3 warns against directly.
/// Measured on a budget build while the prose was the only control: 13 of 99 cards over the
/// ceiling, five of them between thirteen and sixteen dollars.
/// </remarks>
public sealed class PriceCeilingTests
{
    [Fact]
    public void Budget_caps_a_card_at_three_dollars()
    {
        Assert.Equal(3m, AiBuildService.PriceCeiling("budget"));
    }

    [Fact]
    public void Mid_range_caps_at_the_top_of_the_band_it_describes()
    {
        // The prose steers the average under $20 and allows a few to reach $30; the pool
        // stops everything past the upper bound so "a few" cannot become "any number".
        Assert.Equal(30m, AiBuildService.PriceCeiling("mid"));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("")]
    [InlineData("something-else")]
    public void Anything_else_is_uncapped(string tier)
    {
        Assert.Null(AiBuildService.PriceCeiling(tier));
    }

    [Fact]
    public void The_tiers_are_ordered()
    {
        // Guards the pair against being edited apart: a "mid" build must never offer a
        // narrower pool than a "budget" one.
        Assert.True(AiBuildService.PriceCeiling("budget") < AiBuildService.PriceCeiling("mid"));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("mid")]
    public void A_capped_tier_tells_the_model_it_need_not_reason_about_price(string tier)
    {
        // The pool has already removed everything over the ceiling, so the prompt must not
        // send the model looking for prices it does not have. This is the same argument the
        // bracket text makes about Game Changers.
        var text = AiBuildService.DescribePrice(tier);

        Assert.Contains("already inside the budget", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_uncapped_tier_says_so()
    {
        Assert.Contains("None", AiBuildService.DescribePrice("any"), StringComparison.Ordinal);
    }
}
