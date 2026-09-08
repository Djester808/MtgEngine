using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 23 (seam-requests/V13.md): a record the model can see with nothing telling it which reading
/// to write is the disagreement the pilot pays for, so every wave-23 addition names itself in the
/// later spellings, and the harnessed line says which of two legal-looking spellings is the one.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_three_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(Searches), nameof(ChooseNumberSecretly), nameof(HighestChosen), nameof(LowestChosen),
            nameof(PlayerWhoChose), nameof(CardsDrawnThisTurn), nameof(LandsEnteredThisTurn),
            nameof(BecomesCopyOfNamed), nameof(Targets), nameof(CantCastSpells), nameof(IsHarnessed),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[] { "\"opponent\": true", "\"lowest\": true", "\"notCastFrom\": \"Hand\"", "{\"kind\":\"IsHarnessed\"}" })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }
}
