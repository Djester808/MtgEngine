using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 22 (seam-requests/V12.md): a record the model can see with nothing telling it which of two
/// readings to write is what cost the pilot 105 cards to disagreement, so every wave-22 addition
/// names itself in the later spellings and the whole block reaches the system text.
/// </summary>
public sealed partial class TranslationPromptTests
{
    [Fact]
    public void Every_wave_twenty_two_record_has_a_one_spelling_line()
    {
        var later = TranslationPrompt.LaterSpellings;
        foreach (var record in new[]
        {
            nameof(OptionalAdditionalCost), nameof(CostModifier), nameof(DealDamage),
            nameof(ChooseMode), nameof(Draws), nameof(PayCost), nameof(MayPay),
        })
            Assert.Contains(record, later, StringComparison.Ordinal);

        foreach (var member in new[] { "\"ifPaid\"", "\"onlyColor\"", "\"excessTo\"", "\"excessIf\"", "\"budget\"", "\"exceptFirstInDrawStep\": true", "\"then\"" })
            Assert.Contains(member, later, StringComparison.Ordinal);

        Assert.Contains(later, TranslationPrompt.SystemWithVocabulary(), StringComparison.Ordinal);
    }
}
