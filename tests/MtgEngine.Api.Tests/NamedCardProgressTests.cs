using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The live count behind the build's progress bar.
/// </summary>
/// <remarks>
/// It reads a half-written JSON answer as it streams, so it is approximate by design. What
/// it may not do is overshoot: it counted every quoted string to the end of the text, ran
/// on through <c>side</c>, <c>maybe</c> and <c>substitutes</c>, and a ninety-nine card deck
/// announced "130 named" while the bar sat pinned near full.
/// </remarks>
public sealed class NamedCardProgressTests
{
    [Fact]
    public void Nothing_is_named_before_the_answer_starts()
    {
        Assert.Equal(0, AiBuildService.CountNamedCards(""));
        Assert.Equal(0, AiBuildService.CountNamedCards("""{"thinking":"..."}"""));
        // The key has arrived but the array has not.
        Assert.Equal(0, AiBuildService.CountNamedCards("""{"main"""));
    }

    [Fact]
    public void Names_are_counted_as_they_complete()
    {
        // Ends on the comma, so both names are closed and both count.
        Assert.Equal(2, AiBuildService.CountNamedCards("""{"main":["Sol Ring","Forest","""));
    }

    [Fact]
    public void A_half_written_name_is_not_counted_yet()
    {
        // Its closing quote has not arrived, so it is not a name the model has committed to.
        Assert.Equal(1, AiBuildService.CountNamedCards("""{"main":["Sol Ring","For"""));
    }

    [Fact]
    public void The_count_stops_at_the_end_of_the_main_array()
    {
        // The regression, in miniature: three in main, three more spread across the boards
        // that follow. The answer to "how many of the ninety-nine are named" is three.
        const string answer = """
            {"main":["Sol Ring","Forest","Llanowar Elves"],
             "side":["Naturalize"],
             "maybe":["Regrowth"],
             "substitutes":["Rampant Growth"]}
            """;

        Assert.Equal(3, AiBuildService.CountNamedCards(answer));
    }

    [Fact]
    public void A_bracket_inside_a_card_name_does_not_end_the_count()
    {
        // Card names really do carry brackets, and ending the array on the first ']' seen
        // would stop counting partway through a legitimate list.
        const string answer = """{"main":["Erase [Not the Urza's Legacy One]","Forest"],"side":[]}""";

        Assert.Equal(2, AiBuildService.CountNamedCards(answer));
    }

    [Fact]
    public void The_count_reaches_the_full_deck_and_goes_no_further()
    {
        var main = string.Join(",", Enumerable.Range(0, 99).Select(i => $"\"Card {i}\""));
        var answer = $$"""{"main":[{{main}}],"side":["Extra One","Extra Two"]}""";

        Assert.Equal(99, AiBuildService.CountNamedCards(answer));
    }
}
