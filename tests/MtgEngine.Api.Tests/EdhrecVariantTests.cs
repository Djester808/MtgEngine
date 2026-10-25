using MtgEngine.Api.Services.Community;

namespace MtgEngine.Api.Tests;

public sealed class EdhrecVariantTests
{
    [Theory]
    [InlineData("", "pages/commanders/atraxa.json", "pages/average-decks/atraxa.json")]
    [InlineData("budget", "pages/commanders/atraxa/budget.json", "pages/average-decks/atraxa/budget.json")]
    [InlineData("theme-infect", "pages/commanders/atraxa/infect.json", "pages/average-decks/atraxa/infect.json")]
    [InlineData("bracket-3", "pages/commanders/atraxa/bracket-3.json", "pages/average-decks/atraxa/bracket-3.json")]
    public void Valid_variants_have_closed_paths(string key, string path, string average)
    {
        Assert.True(EdhrecVariant.TryParse(key, out var variant));
        Assert.Equal(path, variant.UpstreamPath("atraxa"));
        Assert.Equal(average, variant.AverageDeckPath("atraxa"));
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("theme-../other")]
    [InlineData("theme-a?b")]
    [InlineData("bracket-6")]
    [InlineData("BUDGET")]
    public void Invalid_variants_never_become_paths(string key) => Assert.False(EdhrecVariant.TryParse(key, out _));
}
