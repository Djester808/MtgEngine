using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 29 (seam-requests/V18.md): every addition names itself in the later spellings, and each line
/// that exists to settle a choice between two legal-looking readings says which one is the spelling —
/// a target range against two targets, a turn's life loss against a trigger's, distinct names against
/// a count of objects, the commander tax's count against a stored key, a keyword cost that is not
/// mana against a printed one, a cast at instant speed against a turn question, a prepared entry
/// against a counter, a named card's type against an options list, and one damage replacement against
/// two.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_nine_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(Target), nameof(LifeLostThisTurn), nameof(TriggeringLifeLost), nameof(DistinctNameCount),
            nameof(CountOf), nameof(CommanderCastsThisGame), nameof(GrantKeyword), nameof(CastAtInstantSpeed),
            nameof(EntersPrepared), nameof(EntersTapped), nameof(ChooseValue), nameof(WouldDealDamage),
            nameof(WouldBeDealtDamage),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[]
        {
            "\"min\"", "\"ofType\": \"Land\"", "\"costs\"", "\"kind\":\"CastAtInstantSpeed\"",
            "\"what\": \"CardName\"", "\"toPlayer\"",
        })
            Assert.Contains(member, later, StringComparison.Ordinal);

        foreach (var rule in new[] { "601.2c", "119.3", "201.2b", "903.8", "702.21a", "307.5a", "722.3a", "614.1" })
            Assert.Contains(rule, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wave_twenty_nine_records_reach_the_reference()
    {
        // The reference is rendered from the registered records, so a new kind appears there without
        // a hand-written line; the fields ride on records it already lists.
        var reference = VocabularyReference.Text();
        foreach (var record in new[] { nameof(LifeLostThisTurn), nameof(DistinctNameCount), nameof(CommanderCastsThisGame), nameof(CastAtInstantSpeed), nameof(EntersPrepared) })
            Assert.Contains(record, reference, StringComparison.Ordinal);
    }
}
