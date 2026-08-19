using System.Text.Json;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The oracle bulk file is not a list of playable cards, and the index has to know it.
/// </summary>
/// <remarks>
/// A tenth of the file — 3,953 of 36,494 entries — is art cards, tokens, emblems and
/// format supplements. Indexing them made them real everywhere at once: search returned
/// them, the card modal opened them, and the AI candidate pool counted them as legal.
/// <para>
/// The damage was worst as a commander. An art card carries the front-face name twice, a
/// type line of "Card // Card", and an <b>empty colour identity</b>, so picking one made
/// every card in Magic colour-legal — the candidate pool went from 12,063 to 30,727 and
/// carried no creature types for the build to find a tribe in.
/// </para>
/// </remarks>
public sealed class NonCardLayoutTests
{
    private static bool IsNonCard(string json) =>
        BulkDataService.IsNonCard(JsonDocument.Parse(json).RootElement);

    [Theory]
    // The art-series entry that shipped a broken commander, verbatim in shape.
    [InlineData("art_series", "Tovolar, Dire Overlord // Tovolar, Dire Overlord")]
    [InlineData("front_card", "Surprise!")]
    [InlineData("token", "Tyranid")]
    [InlineData("double_faced_token", "Foraging Squirrels // Foraging Squirrels (cont'd)")]
    [InlineData("emblem", "Koth of the Hammer Emblem")]
    [InlineData("planar", "The Great Aerie")]
    [InlineData("scheme", "What's Yours Is Now Mine")]
    [InlineData("vanguard", "Titania")]
    public void Things_that_are_not_cards_stay_out_of_the_index(string layout, string name)
    {
        Assert.True(IsNonCard($$"""{"layout":"{{layout}}","name":"{{name}}"}"""));
    }

    [Theory]
    // Double-faced and split layouts are real cards and must survive: the whole bug was a
    // fake "X // X" entry outranking the genuine transform card of the same name.
    [InlineData("normal")]
    [InlineData("transform")]
    [InlineData("modal_dfc")]
    [InlineData("adventure")]
    [InlineData("split")]
    [InlineData("saga")]
    [InlineData("meld")]
    [InlineData("prototype")]
    public void Real_cards_are_kept(string layout)
    {
        Assert.False(IsNonCard($$"""{"layout":"{{layout}}","name":"A Card"}"""));
    }

    [Fact]
    public void An_entry_with_no_layout_is_kept()
    {
        // Absent rather than unrecognised: dropping an entry because a field is missing
        // would quietly shrink the index if Scryfall ever stopped emitting it.
        Assert.False(IsNonCard("""{"name":"A Card"}"""));
    }

    [Fact]
    public void Layout_matching_ignores_case()
    {
        Assert.True(IsNonCard("""{"layout":"Art_Series","name":"X // X"}"""));
    }
}
