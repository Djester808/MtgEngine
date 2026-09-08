using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 30 (seam-requests/V19.md): every addition names itself in the later spellings, and each line
/// that exists to settle a choice between two legal-looking readings says which one is the spelling —
/// life loss against a life total that can't change, the player half of hexproof against an anonymous
/// restriction, a trigger's cause against the sources it applies to, one player's count against the
/// greatest of the others, and the free cast against a cost of zero.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_thirty_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(PlayerCant), nameof(PlayerHexproof), nameof(AbilitiesTriggerTwice),
            nameof(MoreThanEachOtherPlayer), nameof(PlayerWithMostOf), nameof(AlternativeCost),
            nameof(EntersBattlefield), nameof(ManaCost),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[]
        {
            "\"what\": [\"LoseLife\"]", "\"cause\"", "\"ofSources\"", "EachPlayer",
            "\"withoutPayingManaCost\": true", "\"cost\": {\"kind\":\"Costs\",\"all\":[]}", "\"orEqual\"",
        })
            Assert.Contains(member, later, StringComparison.Ordinal);

        foreach (var rule in new[] { "119.3", "702.11c", "603.2d", "400.2", "118.9", "118.9c" })
            Assert.Contains(rule, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wave_thirty_records_reach_the_reference()
    {
        // The reference is rendered from the registered records, so a new kind appears there without
        // a hand-written line; the fields ride on records it already lists.
        var reference = VocabularyReference.Text();
        foreach (var record in new[] { nameof(PlayerHexproof), nameof(MoreThanEachOtherPlayer) })
            Assert.Contains(record, reference, StringComparison.Ordinal);
    }
}
